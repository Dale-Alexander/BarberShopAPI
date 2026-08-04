import { test, expect } from "@playwright/test";
import { BARBER_TWO, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, confirmAsCash,
} from "./helpers.js";

/* TESTS.md A7 - paying, then hitting Back.
 *
 * WHAT THIS PROVES: a booking that has been paid for cannot be returned to a live payment form by
 * navigating back to its checkout URL. The customer ends up on the completed screen instead.
 *
 * WHAT IT DOES NOT PROVE, despite the obvious temptation to claim it: the Cache-Control: no-store
 * header on GetBookingDetailsForCheckout. That header exists because Back was once serving a CACHED
 * checkout response for an already-paid booking. This spec was mutation-tested against it - the
 * headers were commented out and the spec still passed - so it is NOT a regression guard for them.
 * Playwright's goBack re-runs the SPA's fetch either way, so the browser never reaches for the cache
 * the way a real Back in a real session did.
 *
 * Left in place because what it does cover is worth covering: the status guard behind the redirect.
 * But if you are here because the caching bug came back, this spec would not have caught it, and
 * making it do so needs a different approach (bfcache, or asserting on the response headers).
 *
 * Uses BARBER_TWO to stay out of the way of the specs that lean on BARBER_ONE. */
test.describe("paying and then going back", () => {
    let api, barberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_TWO.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterAll(async () => await api?.dispose());

    test("the back button cannot return a paid booking to a live payment form", async ({ page, request }) => {
        const startDateTime = slotInDays(9, 15, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });

        // The customer is on the payment page, and it works - this is the state Back will try to
        // restore from cache later.
        await page.goto(`/checkout/${publicId}`);
        await expect(page.getByText("Total")).toBeVisible();

        // They pay. Cash rather than card, because what is under test is the browser's history and
        // caching behaviour, not Stripe - and a card would drag a payment element into a test that
        // has nothing to do with one.
        await confirmAsCash(request, publicId);
        await page.goto(`/booking/success/${publicId}`);
        await expect(page).toHaveURL(new RegExp(`/booking/success/${publicId}`));

        await page.goBack();

        /* The whole point. Landing back on /checkout with a working form is the bug: the customer
           has already paid and the page would happily take another payment. */
        await expect(page).not.toHaveURL(new RegExp(`/checkout/${publicId}`));
        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`));
        await expect(page.getByText("Transaction Complete")).toBeVisible();
    });
});
