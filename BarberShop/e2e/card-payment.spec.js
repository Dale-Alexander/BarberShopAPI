import { test, expect } from "@playwright/test";
import { BARBER_ONE, SERVICES } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays, createPendingBooking,
    startCardCheckout, payWithCard, TEST_CARDS, findBookingIdByStart, cancelBooking,
    sinkStatus, clearEmails, waitForEmail, expectNoEmail,
} from "./helpers.js";

/* TESTS.md section F - paying by card, for real.
 *
 * Everything else in this suite pays cash, because until now nothing could deliver Stripe's webhook to
 * localhost and a card booking therefore never got past "Finalising your booking...". With `stripe
 * listen` forwarding through the harness's gate, the whole round trip runs: a real PaymentIntent, a
 * real card, a real webhook, and the screen the customer is actually left looking at.
 *
 * These are CUSTOMER specs - no admin storageState on the page. The admin client is only used to look
 * up the fixture's barber and service ids. */

/** One row of the confirmation receipt, by its label - the value cell, not the whole row. */
const receiptRow = (page, label) =>
    page.locator(".ap-receipt__row", { hasText: label }).locator(".ap-receipt__row-value");
test.describe("paying by card", () => {
    /* Well past the 30s default, and not padding: a card spec creates a real PaymentIntent, loads
       Stripe's element over the network, confirms with Stripe, then waits out the success screen's
       webhook poll. The default budget expires in the middle of that and reports it as a product
       failure. */
    test.describe.configure({ timeout: 120_000 });

    let api, barberId, serviceId, stripeReady, stripeReason;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);

        /* Read once and skip per test with the reason attached. A card spec that can't run must say so
           in words - a silent skip and a passing test look identical in a report six weeks later. */
        const status = await sinkStatus();
        stripeReady = status.stripeReady;
        stripeReason = status.stripeReason;
    });

    test.afterAll(async () => await api?.dispose());

    test("a customer pays by card and ends up on the confirmed booking, receipt and all", async ({ page, request }) => {
        test.skip(!stripeReady, `no Stripe webhook forwarder: ${stripeReason}`);

        const startDateTime = slotInDays(10, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await clearEmails();

        await startCardCheckout(page, publicId, { email: "card.happy@e2e.test" });
        await payWithCard(page, TEST_CARDS.success);

        /* Stripe redirects here the instant the card succeeds, while the booking is still PENDING - the
           screen sits on "Finalising your booking..." and polls until the webhook lands. A generous
           timeout on purpose: this is the poll window plus a real network round trip, and squeezing it
           would make the spec fail on timing rather than on behaviour. */
        await expect(page).toHaveURL(new RegExp(`/booking/success/${publicId}`), { timeout: 30_000 });
        await expect(page.getByText("Booking Confirmed")).toBeVisible({ timeout: 30_000 });

        /* The receipt, because "Booking Confirmed" on its own would pass for a booking confirmed with
           the wrong barber, the wrong service or the wrong money. Read row by row rather than as loose
           page text: "CARD" appears more than once on this screen, and a bare getByText would be
           satisfied by the wrong one. Note the FULL name here - the success screen projects Name +
           Surname, unlike the picker's first-name-only roster. */
        await expect(receiptRow(page, "Barber")).toHaveText(BARBER_ONE.fullName);
        await expect(receiptRow(page, "Service")).toHaveText(SERVICES[0].name);
        // The payment method the customer actually used, not the cash wording every other spec sees.
        await expect(receiptRow(page, "Payment Method")).toHaveText("CARD");
        await expect(page.locator(".ap-receipt__footer-amount")).toHaveText(`€${SERVICES[0].price}`);

        // The screen promises "We've sent a confirmation to your email" - so one had better exist.
        const email = await waitForEmail({ to: "card.happy@e2e.test", subject: "Booking Confirmed" });
        expect(email.text).toContain(SERVICES[0].name);
        expect(email.text).toContain(`€${SERVICES[0].price}`);
    });

    test("a declined card leaves the customer on the form, told why, and able to try again", async ({ page, request }) => {
        test.skip(!stripeReady, `no Stripe webhook forwarder: ${stripeReason}`);

        const startDateTime = slotInDays(11, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await clearEmails();

        await startCardCheckout(page, publicId, { email: "card.declined@e2e.test" });
        await payWithCard(page, TEST_CARDS.declined);

        /* Stripe's own words, shown to the customer. A decline is a fixable problem - the wrong card,
           or one with no money on it - so the one thing that must not happen is being bounced to the
           cancelled screen as though the booking were gone. */
        await expect(page.getByText(/declined/i).first()).toBeVisible({ timeout: 20_000 });
        await expect(page).toHaveURL(new RegExp(`/checkout/${publicId}`));

        /* Still payable, and the button is live again so they can put in another card. PaymentForm
           re-checks the booking after a failure precisely to tell "your card was refused" apart from
           "your slot is gone", and only re-enables for the first. */
        await expect(page.getByRole("button", { name: "Confirm Booking" })).toBeEnabled({ timeout: 20_000 });

        // Nothing was paid, so nothing may be confirmed - to the customer or in their inbox.
        await expectNoEmail({ to: "card.declined@e2e.test" });
    });

    test("cancelling a card booking promises the refund the customer is actually getting", async ({ page, request }) => {
        test.skip(!stripeReady, `no Stripe webhook forwarder: ${stripeReason}`);

        const startDateTime = slotInDays(12, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });

        await startCardCheckout(page, publicId, { email: "card.refund@e2e.test" });
        await payWithCard(page, TEST_CARDS.success);
        await expect(page.getByText("Booking Confirmed")).toBeVisible({ timeout: 30_000 });

        const bookingId = await findBookingIdByStart(api, startDateTime);
        await clearEmails();
        await cancelBooking(api, bookingId, { refundAnyway: true });

        /* The counterpart to the cash cancellation in emails.spec.js, and the reason refundIssued is
           threaded through rather than assumed: this customer really was charged and really is getting
           it back, so the email has to say so. Silence here reads as "we kept your money". */
        const email = await waitForEmail({ to: "card.refund@e2e.test", subject: "cancelled" });
        expect(email.text).toContain("refund has been issued");
    });

    /* No forwarder needed for this one - it is entirely the frontend's handling of Stripe's redirect
       back, which is what a Revolut Pay customer gets when they abandon the payment in the app. */
    test("a payment abandoned on Stripe's side sends the customer back to pay, not to a confirmation", async ({ page, request }) => {
        const startDateTime = slotInDays(13, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });

        await page.goto(`/booking/success/${publicId}?redirect_status=failed`);

        /* Straight back to checkout with the slot still theirs. The booking is still PENDING, so without
           this the success screen would wait out its full 30 seconds for a webhook that is never coming,
           and then tell them their payment had been received - which for an abandoned payment is false. */
        await expect(page).toHaveURL(new RegExp(`/checkout/${publicId}`), { timeout: 20_000 });
        await expect(page.getByText("Booking Confirmed")).toHaveCount(0);
        await expect(page.getByText("Total")).toBeVisible();
    });
});
