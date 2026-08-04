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
