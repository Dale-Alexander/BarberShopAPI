import { test, expect } from "@playwright/test";
import { BARBER_ONE, BARBER_TWO } from "./fixtures.js";

/* Proves the harness itself, not the product. If this fails, nothing else in e2e/ can be trusted, so
   it's worth keeping as its own spec rather than folding into a journey test.

   Passing means the whole chain is live: the fixture reached the E2E database, the API booted against
   the E2E .env (not the developer's), vite proxied /api through to it, and a real browser rendered
   what came back. */
test.describe("harness", () => {
    test("the booking page lists the seeded barbers, fetched from the real API", async ({ page }) => {
        /* Captured before navigating so the response can't land first. Asserting on the response as
           well as the pixels distinguishes "the API answered" from "something painted a stale cache". */
        const barbersResponse = page.waitForResponse(
            (r) => r.url().includes("/api/Barbers/barbers-with-bookings") && r.request().method() === "GET"
        );

        await page.goto("/datetime");

        expect((await barbersResponse).status()).toBe(200);
        /* Note the order: We start listening before navigating. If you navigated first, the response could arrive before you began watching and youd wait forever for
        something already gone. There is no await on the first line on purpose - it creates a pending promise we collect later.
        ✅ Start listening for the network response.
        ✅ Navigate to /datetime.
        ✅ The page makes the GET /api/Barbers/barbers-with-bookings request.
        ✅ waitForResponse catches it and resolves.
        ✅ You check that the status is 200.
        If you reversed the order, when you navigate to /datetime, the response might have actually already happened. The promise might then wait forever, or until it times
        out because it only observes future responses. There is no await because you would be waiting for something that only happens after you have navigated. 
        So waitForResponse would be waiting forever because it never navigates and makes the request and therefore stays stuck waiting. barbersResponse is a Promise<Response>,
        you want to create the promise first so it begins listening. then await is used to wait for the promise to resolve and inspect the status. */

        await expect(page.getByRole("heading", { name: "Choose Your Barber" })).toBeVisible();
        await expect(page.locator(".bp-barber-name", { hasText: BARBER_ONE.firstName })).toBeVisible();
        await expect(page.locator(".bp-barber-name", { hasText: BARBER_TWO.firstName })).toBeVisible();
        //every expect here auto-waits - up to 5 seconds by default, rechecking continuously. 
    });

    test("the seeded shop is the only thing in the database", async ({ request }) => {
        //Request instead of page - talk to API directly, no brwoser. Faster when you dont need a screen. 

        /* A guard against the worst possible misconfiguration: the suite silently running against the
           development database. Two barbers is what the fixture creates; a real shop would have other
           numbers and other names. */
        const res = await request.get("/api/Barbers/barbers-with-bookings");
        expect(res.status()).toBe(200);

        // The endpoint returns the roster alongside the shop's closures and booking settings, not a
        // bare array - the page needs all three to grey out unavailable slots.
        const { barbers } = await res.json();
        const names = barbers.map((b) => b.barberName).sort();
        expect(names).toEqual([BARBER_ONE.firstName, BARBER_TWO.firstName].sort());
    });
});
