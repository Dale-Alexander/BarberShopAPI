import { test, expect } from "@playwright/test";
import { BARBER_ONE, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, createClosure, dateOf,
    confirmAsCash, findBookingIdByStart, cancelBooking, submitCashCheckout,
} from "./helpers.js";

/* TESTS.md section A - the booking dies while the customer is still in checkout.
 *
 * The C# suite already proves the booking ends up CANCELLED. What it cannot answer, and what these
 * specs exist for, is whether the person sitting on the payment page ever finds out - or whether
 * they're left staring at a form that will never work. */
test.describe("a booking killed while the customer is in checkout", () => {
    let api, barberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterAll(async () => await api?.dispose());

    /* A2. A1 ("admin cancels it") is in the Not-reachable list: admin-fetch hides PENDING bookings
       on purpose, so an admin never sees a checkout in progress and cannot cancel one. A closure is
       what actually kills a live checkout, and it needs no booking id - the closure finds it. */
    test("a closure lands on the slot - the customer is moved to the cancelled screen, not left on the form", async ({ page, request }) => {
        const startDateTime = slotInDays(3, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });

        /* Put the customer on the payment page FIRST and prove they got there. Without this the test
           could pass having never reached checkout at all - the redirect below would then be proving
           nothing about a customer mid-payment. */
        await page.goto(`/checkout/${publicId}`);
        await expect(page.getByText("Total")).toBeVisible();
        expect(page.url()).toContain(`/checkout/${publicId}`);

        const closure = await createClosure(api, { date: dateOf(startDateTime) });
        expect(closure.status(), await closure.text()).toBe(200);

        /* The customer acts on a page that is now dead - the next thing they do must tell them so.
           They fill in the form and click Confirm, which is what actually happens: confirm-cash
           answers 400 and the app has to read that as "gone", not as a bad input. */
        await submitCashCheckout(page);

        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`), { timeout: 15_000 });
        await expect(page.getByText("Cancelled").first()).toBeVisible();
        /* And the opposite must not be true. A cancelled booking that still says "Confirmed" anywhere
           on this screen is the worst version of this bug, so assert it explicitly. */
        await expect(page.getByText("Booking Confirmed")).toHaveCount(0);
    });

    /* A3, the half of it that is actually about a screen.
     *
     * The scenario as written assumed a closure cancels a booking that has already been paid for. It
     * does not: DatesController leaves confirmed bookings on their slot with their money and FLAGS
     * them, with a note saying in as many words that the customer has not been told. The only way a
     * paid booking dies from a closure is the webhook race - closure lands while the card is in
     * flight - and that needs Stripe to call localhost, which this harness cannot arrange. That race
     * is covered from the other side in WebhookRefundTests.
     *
     * What IS worth proving here, and is not covered anywhere else, is the success screen's own
     * behaviour: a booking cancelled after it was confirmed must never still read "Booking
     * Confirmed". Summary.jsx polls for exactly this, so the assertion has to allow for the delay
     * rather than demand it instantly. */
    test("a booking cancelled after confirmation never shows as confirmed on the success screen", async ({ page, request }) => {
        const startDateTime = slotInDays(4, 14, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await confirmAsCash(request, publicId);

        // Confirmed bookings DO reach the admin list - they have a Payment row now.
        const bookingId = await findBookingIdByStart(api, startDateTime);
        await cancelBooking(api, bookingId, { refundAnyway: true });

        await page.goto(`/booking/success/${publicId}`);

        /* toHaveURL waits, which is what lets the poll in Summary.jsx settle. Given a generous
           timeout on purpose: the component polls every 2s up to 4 times, so a tighter budget would
           make this fail on timing rather than on behaviour. */
        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`), { timeout: 15_000 });
        await expect(page.getByText("Booking Confirmed")).toHaveCount(0);
        await expect(page.getByText("Cancelled").first()).toBeVisible();
    });
});
