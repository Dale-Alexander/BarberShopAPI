import { test, expect } from "@playwright/test";
import { BARBER_ONE, SERVICES, adminPassword } from "./fixtures.js";
import {
    adminApi, findBarberId, findServiceId, slotInDays, dateOf, createPendingBooking,
    createClosure, clearAllClosures, deactivateBarber, reviveBarber,
    getSchedule, setShifts, FULL_WEEK_SHIFTS, confirmAsCash,
    startCardCheckout, payWithCard, holdWebhook, releaseWebhook,
    sinkStatus, clearEmails, waitForEmail,
} from "./helpers.js";

/* TESTS.md section F - the card has succeeded, and the slot dies before the webhook lands.
 *
 * This is the gap the harness's webhook gate exists for, and the reason A3 sat in the not-reachable
 * list. The webhook's refund-and-cancel branches only fire in the window BETWEEN Stripe taking the
 * money and the webhook arriving - under a second in real life. Kill the slot before that and nothing
 * interesting happens: BookingConflictCanceller voids the still-voidable PaymentIntent and the customer
 * never gets to pay. Kill it after, and the booking is already COMPLETED and merely gets flagged.
 *
 * So each spec here holds the delivery, changes the shop while the customer sits on "Finalising your
 * booking...", and then lets it through. The event is genuinely Stripe's, signed and verified as usual;
 * only the moment it arrives is ours.
 *
 * Every one of these charges a real test card and refunds it for real, so they also prove the money
 * went back - not merely that a status column changed. */
test.describe("the slot dies between the card succeeding and the webhook arriving", () => {
    /* Real Stripe, a real refund and a held webhook - comfortably past the 30s default. */
    test.describe.configure({ timeout: 120_000 });

    let api, barberId, serviceId, scheduleId, stripeReady, stripeReason;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
        const versions = await getSchedule(api, barberId);
        scheduleId = (Array.isArray(versions) ? versions : versions.versions)[0].id;

        const status = await sinkStatus();
        stripeReady = status.stripeReady;
        stripeReason = status.stripeReason;
    });

    /* Nothing may be left held, closed, narrowed or deactivated. A spec that fails halfway would
       otherwise park a webhook forever, shut the shop, or take a barber off the roster for everything
       that runs after it - and the damage would surface somewhere else entirely, which is the worst
       kind of failure to read. Reviving unconditionally rather than at the end of the spec that
       deactivates: that line is exactly the one a failure skips. */
    test.afterEach(async () => {
        await releaseWebhook();
        await clearAllClosures(api);
        await setShifts(api, scheduleId, FULL_WEEK_SHIFTS);
        await reviveBarber(api, { fullName: BARBER_ONE.fullName, email: BARBER_ONE.email, password: adminPassword });
    });

    test.afterAll(async () => await api?.dispose());

    test("a closure lands on the slot - the customer is refunded and told, not shown a confirmation", async ({ page, request }) => {
        test.skip(!stripeReady, `no Stripe webhook forwarder: ${stripeReason}`);

        const startDateTime = slotInDays(44, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await clearEmails();

        await startCardCheckout(page, publicId, { email: "race.closure@e2e.test" });
        await holdWebhook();
        await payWithCard(page);

        /* They have paid and are watching the spinner. Proving they got this far matters: if the card
           never went through, everything below would pass for the wrong reason. */
        await expect(page).toHaveURL(new RegExp(`/booking/success/${publicId}`), { timeout: 30_000 });

        const closure = await createClosure(api, { date: dateOf(startDateTime) });
        expect(closure.status(), await closure.text()).toBe(200);
        await releaseWebhook();

        /* The whole point: the card succeeded, so without the webhook's closure re-check this customer
           would be looking at "Booking Confirmed" for an appointment on a day the shop is shut. */
        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`), { timeout: 30_000 });
        await expect(page.getByText("This Time Slot Is No Longer Available")).toBeVisible();
        await expect(page.getByText("Booking Confirmed")).toHaveCount(0);

        /* They were charged, so the money and the explanation both have to arrive. The email is the
           only place they are told a refund is coming - the screen doesn't say it. */
        const email = await waitForEmail({ to: "race.closure@e2e.test", subject: "cancelled" });
        expect(email.text).toContain("close the shop");
        expect(email.text).toContain("refund has been issued");
    });

    test("the barber is deactivated - the customer is sent to rebook with someone else", async ({ page, request }) => {
        test.skip(!stripeReady, `no Stripe webhook forwarder: ${stripeReason}`);

        const startDateTime = slotInDays(45, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await clearEmails();

        await startCardCheckout(page, publicId, { email: "race.barber@e2e.test" });
        await holdWebhook();
        await payWithCard(page);
        await expect(page).toHaveURL(new RegExp(`/booking/success/${publicId}`), { timeout: 30_000 });

        await deactivateBarber(api, barberId);
        await releaseWebhook();

        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`), { timeout: 30_000 });
        /* The barber wording beats the slot wording here, and it has to: this customer's way out is a
           different barber, not a different time, and telling them otherwise sends them straight back
           to someone who no longer works here. */
        await expect(page.getByText("Your Barber Is No Longer Available")).toBeVisible();
        await expect(page.getByText("This Time Slot Is No Longer Available")).toHaveCount(0);
        await expect(page.getByRole("button", { name: "Choose Another Barber" })).toBeVisible();

        const email = await waitForEmail({ to: "race.barber@e2e.test", subject: "cancelled" });
        expect(email.text).toContain("no longer available");
        expect(email.text).toContain("refund has been issued");
        // afterEach puts him back on the roster - the seed only rebuilds once per run.
    });

    test("the barber's hours are narrowed - the customer is told the slot went, not the barber", async ({ page, request }) => {
        test.skip(!stripeReady, `no Stripe webhook forwarder: ${stripeReason}`);

        // 16:00 sits inside the seeded 09:00-17:30 and outside the 09:00-12:00 it is about to become.
        const startDateTime = slotInDays(46, 16, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await clearEmails();

        await startCardCheckout(page, publicId, { email: "race.hours@e2e.test" });
        await holdWebhook();
        await payWithCard(page);
        await expect(page).toHaveURL(new RegExp(`/booking/success/${publicId}`), { timeout: 30_000 });

        const narrowed = Array.from({ length: 7 }, (_, d) => ({
            DayOfWeek: d, StartTime: "09:00:00", EndTime: "12:00:00",
        }));
        const res = await setShifts(api, scheduleId, narrowed);
        expect(res.status(), await res.text()).toBe(200);
        await releaseWebhook();

        await expect(page).toHaveURL(new RegExp(`/cancelledorcompleted/${publicId}`), { timeout: 30_000 });
        await expect(page.getByText("This Time Slot Is No Longer Available")).toBeVisible();
        await expect(page.getByText("Your Barber Is No Longer Available")).toHaveCount(0);

        /* Its own wording, and emphatically not the closure's. Until the webhook grew a separate
           schedule branch this case borrowed the closure copy and told the customer the shop was shut
           on a day it was open. */
        const email = await waitForEmail({ to: "race.hours@e2e.test", subject: "cancelled" });
        expect(email.text).toContain("working hours have changed");
        expect(email.text).not.toContain("close the shop");
    });

    test("the customer switches to cash first - the late card payment is refunded and the appointment stands", async ({ page, request }) => {
        test.skip(!stripeReady, `no Stripe webhook forwarder: ${stripeReason}`);

        const startDateTime = slotInDays(47, 11, 0);
        const { publicId } = await createPendingBooking(request, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await clearEmails();

        await startCardCheckout(page, publicId, { email: "race.cash@e2e.test" });
        await holdWebhook();
        await payWithCard(page);
        await expect(page).toHaveURL(new RegExp(`/booking/success/${publicId}`), { timeout: 30_000 });

        /* The booking is settled as CASH while the card payment is still in the air - the shape that
           really happens when someone gives up on the card form in one tab and pays at the shop.
           ConfirmCashBooking tries to void the PaymentIntent and can't, because it has already
           succeeded, so the charge lands on a booking that is no longer PENDING. */
        await confirmAsCash(request, publicId, { email: "race.cash@e2e.test", phone: "+35679555111" });
        await releaseWebhook();

        /* Their appointment is real and unaffected - this must NOT end on the cancelled screen. */
        await expect(page.getByText("Booking Confirmed")).toBeVisible({ timeout: 30_000 });
        await expect(page.locator(".ap-receipt__row", { hasText: "Payment Method" })
            .locator(".ap-receipt__row-value")).toHaveText("CASH");

        /* The wording that matters: they still owe the money at the shop, so this email must not read
           as "you have already paid" or call the refund a duplicate. */
        const email = await waitForEmail({ to: "race.cash@e2e.test", subject: "refunded" });
        expect(email.subject).toContain("Your online card payment has been refunded");
        expect(email.text).toContain("settle up at the shop");
        expect(email.text).not.toContain("duplicate");
    });
});
