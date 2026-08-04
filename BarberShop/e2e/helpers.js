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

/** Staff cancellation. refundAnyway overrides the 24h no-refund policy. */
export async function cancelBooking(api, bookingId, { refundAnyway = false } = {}) {
    const res = await api.patch(`/api/bookings/cancel/${bookingId}?refundAnyway=${refundAnyway}`);
    if (!res.ok()) throw new Error(`cancel failed: ${res.status()} ${await res.text()}`);
}

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
