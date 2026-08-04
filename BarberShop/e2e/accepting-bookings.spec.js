import { test, expect } from "@playwright/test";
import { BARBER_ONE, BARBER_TWO } from "./fixtures.js";
import { adminApi, findBarberId, setAcceptingBookings, visibleBarberNames } from "./helpers.js";

/* TESTS.md C6 and C7 - the "working their notice" switch.
 *
 * This file is also the WORKED EXAMPLE for the queue in TESTS.md. Copy its shape:
 *   - arrange through the admin API, assert through a customer's browser
 *   - restore what you changed, because the fixture is only rebuilt once per RUN, not per test
 *   - assert on what a person sees, and assert the negative as well as the positive
 */
test.describe("closing a barber to new bookings", () => {
    let api;
    let barberId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
    });

    test.afterAll(async () => {
        /* Put the shop back. Specs share one database and the seed only runs once per run, so a test
           that leaves a barber hidden silently changes the world for every spec after it - and the
           failure surfaces somewhere else entirely, which is the worst kind to debug. */
        if (api && barberId) await setAcceptingBookings(api, barberId, true);
        await api?.dispose();
    });

    test("a barber closed to new bookings disappears from the customer's picker", async ({ page }) => {
        // Both barbers are offered to begin with - otherwise the assertion below proves nothing.
        expect(await visibleBarberNames(page)).toContain(BARBER_ONE.firstName);

        await setAcceptingBookings(api, barberId, false);

        const namesAfter = await visibleBarberNames(page);
        expect(namesAfter).not.toContain(BARBER_ONE.firstName);
        /* The other barber must STILL be there. Without this the test would also pass if the toggle
           broke the whole roster, which is a far worse bug than the one being tested. */
        expect(namesAfter).toContain(BARBER_TWO.firstName);
    });

    test("turning it back on returns them to the picker", async ({ page }) => {
        await setAcceptingBookings(api, barberId, false);
        expect(await visibleBarberNames(page)).not.toContain(BARBER_ONE.firstName);

        await setAcceptingBookings(api, barberId, true);
        expect(await visibleBarberNames(page)).toContain(BARBER_ONE.firstName);
    });
});
