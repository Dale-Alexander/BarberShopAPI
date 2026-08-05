import { test, expect } from "@playwright/test";
import { BARBER_ONE_STATE, BARBER_TWO_STATE } from "../playwright.config.js";
import { BARBER_ONE, BARBER_TWO, SERVICES, adminPassword } from "./fixtures.js";
import {
    adminApi, guestApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, confirmAsCash, findBookingIdByStart,
    setAcceptingBookings, deactivateBarber, reviveBarber,
} from "./helpers.js";

/* TESTS.md C8 and C9 - what a barber's own session can and cannot still do.
 *
 * The only specs in the suite that run as a BARBER rather than an admin, and that is the whole point:
 * an admin cookie would pass every assertion here while proving nothing. global-setup saves a session
 * for each fixture barber (same password as the admin, so no extra secret) - see BARBER_ONE_STATE.
 *
 * Two describes with different sessions, because C9 destroys the one it uses. */

/* C8. "Accepting bookings" is a switch about CUSTOMERS - it stops new online bookings. It must not
   touch the barber's own working life: they still have appointments on the books and have to be able
   to open and edit them.
   This regressed once. The staff edit page fetches the roster with includeUnbookable=true, and without
   it the barber vanished from their own edit page and could not touch the work they were still doing. */
test.describe("a barber winding down", () => {
    test.use({ storageState: BARBER_ONE_STATE });

    let api, guest, barberId, serviceId, bookingId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        guest = await guestApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);

        const startDateTime = slotInDays(40, 11, 0);
        const { publicId } = await createPendingBooking(guest, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await confirmAsCash(guest, publicId);
        bookingId = await findBookingIdByStart(api, startDateTime);
    });

    test.afterAll(async () => {
        // Reopen them to customers - a barber left hidden changes the shop for every spec after this.
        if (api && barberId) await setAcceptingBookings(api, barberId, true);
        await api?.dispose();
        await guest?.dispose();
    });

    test("can still open and edit their own bookings after being closed to new ones", async ({ page }) => {
        await setAcceptingBookings(api, barberId, false);

        // Their own bookings page still loads, and still shows their name and their work.
        await page.goto(`/admin/team/${barberId}`);
        await expect(page.getByRole("heading", { name: "Bookings" })).toBeVisible({ timeout: 15_000 });
        await expect(page.getByText(BARBER_ONE.fullName).first()).toBeVisible();

        /* And the edit page still offers them as the booking's barber. This is the exact regression:
           the roster that feeds this page filters out barbers closed to new bookings unless asked for
           includeUnbookable, and a barber who fell out of it could not edit their own appointment. */
        await page.goto(`/datetime/${bookingId}`);
        await page.locator(".bp-barber-card").first().waitFor({ timeout: 15_000 });
        const offered = await page.locator(".bp-barber-name").allTextContents();
        expect(offered, "a barber closed to new bookings must still appear on their own edit page")
            .toContain(BARBER_ONE.firstName);
    });
});

/* C9. Deactivation is the "they have left" action, and it has to end their access there and then -
   a cookie is good until it expires, so the only thing that can kill it mid-session is the
   TokenVersion bump DeleteBarber does and TokenVersionMiddleware enforces.
   Proving it needs a session that is genuinely live first, then genuinely dead after. */
test.describe("a barber who is deactivated mid-session", () => {
    test.use({ storageState: BARBER_TWO_STATE });

    let api, barberId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_TWO.firstName);
    });

    test.afterAll(async () => {
        /* Bring them back for the rest of the suite. Note this does NOT restore the session: reviving
           leaves TokenVersion where the bump put it, so BARBER_TWO_STATE is dead for the remainder of
           the run. Any future spec needing a live barber session should use BARBER_ONE. */
        await reviveBarber(api, {
            fullName: BARBER_TWO.fullName, email: BARBER_TWO.email, password: adminPassword,
        });
        await api?.dispose();
    });

    test("is signed out - their existing session stops working", async ({ page }) => {
        // Live first. Without this the assertion below would also pass for a session that never worked.
        await page.goto(`/admin/team/${barberId}`);
        await expect(page.getByRole("heading", { name: "Bookings" })).toBeVisible({ timeout: 15_000 });

        await deactivateBarber(api, barberId);

        /* Same cookie, same page, one moment later. The middleware compares the JWT's TokenVersion with
           the database's and rejects the mismatch, so the app treats them as signed out and the guard
           sends them to login. */
        await page.goto(`/admin/team/${barberId}`);
        await expect(page).toHaveURL(/\/login/, { timeout: 15_000 });
        await expect(page.getByRole("heading", { name: "Bookings" })).toHaveCount(0);
    });
});
