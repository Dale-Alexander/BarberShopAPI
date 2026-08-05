import { ADMIN_STATE, appBaseUrl } from "../playwright.config.js";

/* An API client already signed in as the admin.
 *
 * The pattern every spec here should follow: ARRANGE THROUGH THE API, ASSERT THROUGH THE BROWSER.
 * Clicking through six admin screens to set up a fixture gives you five extra ways to fail for a
 * reason that has nothing to do with what you're testing - and when it breaks at 3am you can't tell
 * a real bug from a moved button.
 *
 * Separate from the `page` a customer test drives, on purpose: this carries the admin cookie, the
 * page does not. A customer page that quietly had staff rights would be testing a different
 * application - staff skip the booking lead-time buffer and see unbookable barbers.
 *
 * Reuses the session global-setup saved rather than logging in. /api/auth/login allows 10 requests a
 * minute per IP; a suite that logs in per-spec starts returning 429 partway through and the failures
 * look exactly like product bugs.
 */
export async function adminApi(playwright) {
    return await playwright.request.newContext({
        baseURL: appBaseUrl,
        storageState: ADMIN_STATE,
    });
}

/* Barber ids are database identities - they differ every time the fixture is rebuilt, so they can
   never be hard-coded. Look them up by the name the fixture created.
   Remember the roster returns FIRST names only (BarberName comes from User.Name alone). */
export async function findBarberId(api, firstName) {
    const res = await api.get("/api/Barbers/barbers-with-bookings?includeUnbookable=true");
    if (!res.ok()) throw new Error(`Could not load barbers: ${res.status()}`);

    const { barbers } = await res.json();
    const match = barbers.find((b) => b.barberName === firstName);
    if (!match) {
        throw new Error(
            `No barber called "${firstName}". Roster: ${barbers.map((b) => b.barberName).join(", ")}. ` +
            `If this is empty the fixture didn't seed - check the [e2e] lines at the top of the run.`
        );
    }
    return match.barberId;
}

/** Opens or closes a barber to new customer bookings (the Team page toggle). ADMIN only. */
export async function setAcceptingBookings(api, barberId, accepting) {
    const res = await api.patch(`/api/Barbers/${barberId}/accepting-bookings`, {
        data: { AcceptsNewBookings: accepting },
    });
    if (!res.ok()) throw new Error(`Toggle failed: ${res.status()} ${await res.text()}`);
}

/* A service's database id, looked up by the name the fixture created.
   Deliberately the ADMIN catalogue: the public /api/services projection leaves the Id out on purpose,
   so it cannot be used here - pass an adminApi client, not the plain request fixture. */
export async function findServiceId(api, name) {
    const res = await api.get("/api/services/admin");
    if (!res.ok()) throw new Error(`Could not load services: ${res.status()}`);
    const services = await res.json();
    const match = services.find((s) => s.name === name);
    if (!match) throw new Error(`No service called "${name}". Got: ${services.map((s) => s.name).join(", ")}`);
    return match.id;
}

/* A bookable wall-clock slot, N days out at the given hour.
   Deliberately several days ahead and mid-morning: the customer path enforces a lead-time buffer
   (MinAdvanceBookingMinutes) and a 60-day horizon, and the fixture barbers work 09:00-17:30. A slot
   at either edge would make a spec fail for a reason that has nothing to do with what it tests.
   Formatted as a naive local string, exactly what the booking page posts - no timezone suffix, so
   the API reads it as Malta wall-clock like every other booking. */
export function slotInDays(days, hour = 11, minute = 0) {
    const d = new Date();
    d.setDate(d.getDate() + days);
    const p = (n) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(hour)}:${p(minute)}:00`;
}

/* Creates a booking the way a CUSTOMER does - unauthenticated, through the real endpoint, leaving it
   PENDING and awaiting payment. Use the plain `request` fixture, never the admin one: the staff path
   skips the lead-time buffer and would let a spec arrange a booking a customer could never make. */
export async function createPendingBooking(request, { barberId, serviceIds, startDateTime }) {
    const res = await request.post("/api/bookings/create-pending", {
        data: { ServicesIds: serviceIds, StartDateTime: startDateTime, BarberId: barberId },
    });
    if (!res.ok()) throw new Error(`create-pending failed: ${res.status()} ${await res.text()}`);
    return await res.json(); // { publicId, startDateTime, status, barberId, barberName, durationMin }
}

/* The internal integer id, which the staff endpoints use and the customer-facing responses never
   expose (PublicId is deliberately the only id in a customer's URL). Matched on the exact slot the
   spec just booked rather than "the newest", so a spec can't pick up another spec's booking. */
export async function findBookingIdByStart(api, startDateTime) {
    const res = await api.get("/api/bookings/admin-fetch?pageSize=200");
    if (!res.ok()) throw new Error(`admin-fetch failed: ${res.status()}`);
    const body = await res.json();
    const rows = body.bookings ?? body.items ?? body;
    const wanted = new Date(startDateTime).getTime();
    const match = (Array.isArray(rows) ? rows : []).find(
        (b) => new Date(b.startDateTime).getTime() === wanted
    );
    if (!match) {
        throw new Error(
            `No booking at ${startDateTime} in the admin list. Saw ${rows.length} row(s): ` +
            JSON.stringify((Array.isArray(rows) ? rows : []).map((b) => ({ id: b.id, start: b.startDateTime })))
        );
    }
    return match.id;
}

/* Closes the shop (or one barber) for a day. ConfirmCancelBookings is the "yes, I know this kills
   live bookings" flag the admin UI collects from the conflict modal - without it the endpoint
   refuses with a 409 listing what it would have cancelled. */
export async function createClosure(api, { date, barberId = null, reason = "E2E closure", confirm = true }) {
    const res = await api.post("/api/Dates", {
        data: {
            BarberId: barberId,
            StartDate: date,
            EndDate: null,
            IsFullDay: true,
            Reason: reason,
            ConfirmCancelBookings: confirm,
        },
    });
    return res; // caller decides - a 409 is a legitimate outcome worth asserting on
}

/** The yyyy-MM-dd date part of a slot string, which is what closures are keyed on. */
export const dateOf = (slot) => slot.split("T")[0];

/* The closures an admin can currently see (today onwards, still active).
   Only way to get at a closure's id - POST /api/Dates doesn't hand one back. */
export async function listClosures(api) {
    const res = await api.get("/api/Dates/admin/closures");
    if (!res.ok()) throw new Error(`admin/closures failed: ${res.status()}`);
    return await res.json();
}

/* Switches a closure back off. PATCH, not DELETE - it's a soft delete (IsActive = false).

   Any spec that creates a closure MUST call this afterwards. The seed runs once per RUN, not per
   spec, so a closure left standing silently shuts the shop for every spec that follows and the
   failure surfaces somewhere else entirely - the same trap reviveBarber warns about. */
export async function deleteClosure(api, closureId) {
    const res = await api.patch(`/api/Dates/delete/${closureId}`);
    if (!res.ok()) throw new Error(`delete closure failed: ${res.status()} ${await res.text()}`);
}

/* Deletes every closure the admin can see. Used in afterEach rather than tracking ids one by one:
   a spec that fails midway still has to leave the calendar as it found it. */
export async function clearAllClosures(api) {
    const closures = await listClosures(api);
    for (const c of Array.isArray(closures) ? closures : []) await deleteClosure(api, c.id);
}

/* The admin edit a booking gets from /datetime/:id - moving it in time, or onto another barber.
   Only COMPLETED bookings can be updated; the endpoint 400s on anything else. confirmOutsideHours is
   the staff override for a slot outside the barber's shifts - without it such a move is refused 409. */
export async function updateBooking(api, bookingId, { barberId = null, startDateTime = null, confirmOutsideHours = false } = {}) {
    const res = await api.patch(`/api/bookings/update-booking/${bookingId}`, {
        data: { BarberId: barberId, StartDateTime: startDateTime, ConfirmOutsideHours: confirmOutsideHours },
    });
    if (!res.ok()) throw new Error(`update-booking failed: ${res.status()} ${await res.text()}`);
}

/** Clears one booking's review flag - the API behind the "Mark reviewed" button. */
export async function markReviewed(api, bookingId) {
    const res = await api.patch(`/api/bookings/mark-reviewed/${bookingId}`);
    if (!res.ok()) throw new Error(`mark-reviewed failed: ${res.status()} ${await res.text()}`);
}

/* Empties the worklist. Cleanup only - never call it as part of what a spec is proving.

   Section B asserts on badge NUMBERS, so it has to start from a known floor; and a flag left behind
   leaks into every later spec's worklist. Reads the same needsReview=true list the admin sees. */
export async function clearAllFlags(api) {
    const res = await api.get("/api/bookings/admin-fetch?needsReview=true&pageSize=200");
    if (!res.ok()) throw new Error(`admin-fetch failed: ${res.status()}`);
    const body = await res.json();
    const rows = body.bookings ?? body.items ?? body;
    for (const b of Array.isArray(rows) ? rows : []) await markReviewed(api, b.id);
}

/* An API client with NO session at all, for arranging bookings the way a customer makes them.

   Section B's specs run under test.use({ storageState: ADMIN_STATE }), which applies to the `request`
   fixture as well as the page - so the plain fixture is NOT anonymous there, and createPendingBooking
   would quietly take the staff path (no lead-time buffer, unbookable barbers allowed). This is the
   customer's client; use it for anything a customer would do. */
export async function guestApi(playwright) {
    return await playwright.request.newContext({ baseURL: appBaseUrl });
}

/* How many bookings are currently flagged, straight from the endpoint the badge reads.

   Specs take this as a BASELINE and assert on the delta, never on an absolute number. The suite
   shares one database serially and a flag can appear without any spec asking for one - a cancellation
   email that exhausts its Hangfire retries flags its own booking - so "the badge says 3" is a test
   that passes until it doesn't. */
export async function needsReviewCount(api) {
    const res = await api.get("/api/bookings/needs-review-count");
    if (!res.ok()) throw new Error(`needs-review-count failed: ${res.status()}`);
    return (await res.json()).count ?? 0;
}

/* Opens the admin bookings table and switches it to the needs-review worklist.

   The Needs Review button only exists while something is flagged (needsReviewCount > 0 gates it), so
   waiting for it doubles as proof the flag actually landed - and gives a readable failure if it
   didn't, instead of a timeout on some row further down. */
export async function openNeedsReviewList(page) {
    await page.goto("/admin");
    const reviewBtn = page.getByRole("button", { name: /Needs Review/ });
    await reviewBtn.waitFor({ timeout: 15_000 });
    await reviewBtn.click();
    await page.locator("tr.needs-review-reason-row").first().waitFor({ timeout: 15_000 });
}

/** The full-width amber row under a booking, carrying the note the admin has to read. */
export const reviewReasonFor = (page, bookingId) =>
    page.locator(`tr:has(.booking-id-cell:text-is("${bookingId}")) + tr.needs-review-reason-row`);

/* Takes a PENDING booking all the way to COMPLETED the way a customer choosing "pay at the shop"
   does - no Stripe involved, so specs that only need a CONFIRMED booking don't have to drive a card
   form. Confirming is what creates the User and the Payment row, which is also what makes the
   booking visible to admin-fetch. */
export async function confirmAsCash(request, publicId, {
    fullName = "Test Customer", phone = "+35679123456", email = "customer@e2e.test",
} = {}) {
    const res = await request.post("/api/bookings/confirm-cash", {
        data: { BookingId: publicId, FullName: fullName, Phone: phone, Email: email },
    });
    if (!res.ok()) throw new Error(`confirm-cash failed: ${res.status()} ${await res.text()}`);
    return await res.json();
}

/* Fills the contact fields and submits the REAL checkout form on the cash path - the browser
   equivalent of confirmAsCash above, for specs where the customer's own click has to be the thing
   that happens, not an API call made on their behalf.

   Reaching for this instead of page.reload() in the "booking died mid-checkout" specs is deliberate.
   A reload only re-runs Checkout.jsx's mount fetch, which redirects on any error status. The click
   goes through confirm-cash and lands in PaymentForm's catch, where isBookingNoLongerPending has to
   recognise the server's 400 prose as "this booking is gone" rather than a fixable input error - the
   fragile part, and the part a customer actually meets.

   Pay at Store is clicked explicitly rather than relied on as the default: the component starts on
   CARD whenever a clientSecret is in sessionStorage, and a spec should not depend on which. */
export async function submitCashCheckout(page, {
    fullName = "Test Customer", phone = "79123456", email = "customer@e2e.test",
} = {}) {
    await page.locator('input[name="customerName"]').fill(fullName);
    await page.getByPlaceholder("john@example.com").fill(email);
    await page.locator("input[type='tel']").fill(phone);
    await page.getByRole("button", { name: "Pay at Store" }).click();
    await page.getByRole("button", { name: "Confirm Booking" }).click();
}

/* Deactivates a barber - the "they've left the shop" action, not the notice-period toggle. Soft
   delete: the row survives so the barber can be revived, but their PENDING bookings are cancelled
   outright and their CONFIRMED ones are flagged for an admin. confirm=true is the UI's second step,
   after it has shown the admin what they're about to affect. */
export async function deactivateBarber(api, barberId) {
    const res = await api.delete(`/api/barbers/delete/${barberId}?confirm=true`);
    if (!res.ok()) throw new Error(`deactivate failed: ${res.status()} ${await res.text()}`);
}

/* Brings a deactivated barber back. There is no dedicated reactivate endpoint - create-barber with
   the same email finds the soft-deleted row and revives it, restoring AcceptsNewBookings and reusing
   the schedule that survived. Multipart because the real form carries an optional image file.

   Any spec that deactivates a fixture barber MUST call this afterwards. The seed only runs once per
   run, so a barber left deactivated silently changes the shop for every spec that follows - and the
   failure lands somewhere else entirely, which is the worst kind to debug. */
export async function reviveBarber(api, { fullName, email, password }) {
    const res = await api.post("/api/barbers/create-barber", {
        multipart: { FullName: fullName, Email: email, Password: password },
    });
    if (!res.ok()) throw new Error(`revive failed: ${res.status()} ${await res.text()}`);
}

/** Every 09:00-17:30 - the hours the fixture seeds, and what specs restore to. */
export const FULL_WEEK_SHIFTS = Array.from({ length: 7 }, (_, d) => ({
    DayOfWeek: d, StartTime: "09:00:00", EndTime: "17:30:00",
}));

/** The barber's current schedule versions, newest rules first. */
export async function getSchedule(api, barberId) {
    const res = await api.get(`/api/schedules/barber/${barberId}`);
    if (!res.ok()) throw new Error(`getSchedule failed: ${res.status()}`);
    return await res.json();
}

/* Replaces the shifts on an existing schedule version.
   ConfirmOrphaned is the second click of the admin's conflict modal: without it the endpoint answers
   409 and lists the CONFIRMED bookings the change would strand. PENDING ones are never part of that
   conversation - they are cancelled outright with CancellationReason.ScheduleChange. */
export async function setShifts(api, scheduleId, shifts, { confirmOrphaned = true } = {}) {
    const res = await api.put(`/api/schedules/version/${scheduleId}`, {
        data: { Shifts: shifts, ConfirmOrphaned: confirmOrphaned },
    });
    return res; // a 409 is a legitimate outcome some specs assert on
}

/* Starts a new seasonal version from a date. The current open-ended version is closed the day before,
   so versions stay contiguous with exactly one open-ended row. Needed by any spec that has to DELETE a
   version - a barber must always keep one, so there has to be a prior to fall back to. */
export async function createScheduleVersion(api, barberId, { effectiveFrom, shifts, confirmOrphaned = true }) {
    const res = await api.post(`/api/schedules/barber/${barberId}`, {
        data: { EffectiveFrom: effectiveFrom, Shifts: shifts, ConfirmOrphaned: confirmOrphaned },
    });
    return res; // a 409 is a legitimate outcome some specs assert on
}

/** Removes the current version, snapping the prior one's hours back over everything it governed. */
export async function deleteScheduleVersion(api, scheduleId, { confirmOrphaned = true } = {}) {
    return await api.delete(`/api/schedules/version/${scheduleId}?confirmOrphaned=${confirmOrphaned}`);
}

/** Sunday-first, matching System.DayOfWeek and the DAYS array the schedule editor renders. */
export const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/** The weekday a slot string falls on, as the editor labels it. */
export const dayNameOf = (slot) => DAY_NAMES[new Date(slot).getDay()];

/* Opens the admin schedule editor on a given barber, by id through ?barberId=.

   NOT via the toolbar dropdown, and not by accident. onPickBarber clears `versions` and then relies on
   the barberId effect to reload them - so picking the barber who is ALREADY selected (the first in the
   roster, preselected on load) clears the editor and never refills it: blank week, no spinner, no
   error, until the page is reloaded. The ?barberId= param is read on first load and takes the same
   path the create-barber handoff uses, so it lands on the right barber in one go.

   Waits for a time input rather than the page shell: the week renders only once the versions request
   has come back, and asserting against an empty editor would pass for the wrong reason. */
export async function openSchedules(page, barberId) {
    await page.goto(`/admin/schedules?barberId=${barberId}`);
    await page.locator(".sched-day input[type='time']").first().waitFor({ timeout: 15_000 });
}

/* Types a new end time into one day's shift row - the narrowing an admin actually performs.
   Only the day the spec cares about, rather than all seven: a booking sits on one weekday, and
   rewriting the whole week would strand bookings other specs are relying on. */
export async function setDayEndTime(page, dayName, hhmm) {
    const row = page.locator(".sched-day", { hasText: dayName }).first();
    await row.locator("input[type='time']").nth(1).fill(hhmm);
}

/** Staff cancellation. refundAnyway overrides the 24h no-refund policy. */
export async function cancelBooking(api, bookingId, { refundAnyway = false } = {}) {
    const res = await api.patch(`/api/bookings/cancel/${bookingId}?refundAnyway=${refundAnyway}`);
    if (!res.ok()) throw new Error(`cancel failed: ${res.status()} ${await res.text()}`);
}

/* Walks the customer booking page as far as the time grid: pick the barber, pick the day.
   Only works for a date inside the currently shown month, which every helper here stays within
   (slotInDays is used with small offsets on purpose). */
export async function openSlotGrid(page, { barberFirstName, slot }) {
    await page.goto("/datetime");
    await page.locator(".bp-barber-card", { hasText: barberFirstName }).click();

    const dayNumber = String(Number(dateOf(slot).split("-")[2])); // "07" -> "7", matching format(day,"d")
    await page.locator(".bp-cal-day:not([disabled])").filter({ hasText: new RegExp(`^${dayNumber}$`) }).first().click();

    // The grid only renders once a date is chosen; waiting on a chip avoids reading an empty grid
    // and concluding "no slots", which would let a spec pass for the wrong reason.
    await page.locator(".bp-time-chip").first().waitFor();
}

/** A single time chip in the grid, e.g. "11:00". */
export const timeChip = (page, hhmm) =>
    page.locator(".bp-time-chip").filter({ hasText: new RegExp(`^${hhmm}$`) });

/** The barber names a customer is currently offered on the booking page. */
export async function visibleBarberNames(page) {
    await page.goto("/datetime");
    await page.getByRole("heading", { name: "Choose Your Barber" }).waitFor();
    /* Wait for the roster request to have been rendered before reading the grid - otherwise an empty
       list reads as "no barbers offered" when it only means "not painted yet", and the test passes
       for entirely the wrong reason. */
    await page.locator(".bp-barber-card").first().waitFor({ state: "attached" }).catch(() => { });
    return await page.locator(".bp-barber-name").allTextContents();
}
