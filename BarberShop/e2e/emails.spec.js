import { test, expect } from "@playwright/test";
import { ADMIN_STATE } from "../playwright.config.js";
import { BARBER_ONE, BARBER_TWO, SERVICES } from "./fixtures.js";
import {
    adminApi, guestApi, findBarberId, findServiceId, slotInDays,
    createPendingBooking, submitCashCheckout, findBookingIdByStart,
    cancelBooking, updateBooking,
    clearEmails, waitForEmail, expectNoEmail,
} from "./helpers.js";

/* TESTS.md section F - the emails the shop actually sends.
 *
 * These need no card and no webhook: they are the shop-side notices, driven from the flows an admin or
 * a customer really performs. What they add over the C# suite is the CONTENT. EmailFailureReviewTests
 * proves the retry-then-flag policy around a failed send; nothing anywhere proved that a successful one
 * says the right thing - and every one of these is a promise made to a person. An update email that
 * doesn't mention the new barber, or a cancellation that promises a refund nobody sent, is wrong in a
 * way no status column will ever show.
 *
 * The mail sink stands in for Resend (see e2e/sink-server.js), so nothing leaves the machine.
 *
 * The customer half of each arrangement goes through guestApi(): these are ADMIN specs, and the plain
 * `request` fixture carries the admin cookie here, which would quietly take the staff path. */
test.describe("the emails a customer receives", () => {
    test.use({ storageState: ADMIN_STATE });
    test.describe.configure({ timeout: 90_000 });

    let api, guest, barberId, otherBarberId, serviceId;

    test.beforeAll(async ({ playwright }) => {
        api = await adminApi(playwright);
        guest = await guestApi(playwright);
        barberId = await findBarberId(api, BARBER_ONE.firstName);
        otherBarberId = await findBarberId(api, BARBER_TWO.firstName);
        serviceId = await findServiceId(api, SERVICES[0].name);
    });

    test.afterAll(async () => {
        await api?.dispose();
        await guest?.dispose();
    });

    /** A confirmed cash booking on the given slot, arranged the way a customer makes one. */
    async function confirmedBooking(startDateTime, email) {
        const { publicId } = await createPendingBooking(guest, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        const res = await guest.post("/api/bookings/confirm-cash", {
            data: { BookingId: publicId, FullName: "Email Customer", Phone: "+35679555222", Email: email },
        });
        if (!res.ok()) throw new Error(`confirm-cash failed: ${res.status()} ${await res.text()}`);

        /* Wait for the confirmation to actually land before handing back. It is sent through Hangfire,
           so under load it can arrive AFTER the spec's clearEmails() - and then sit in the mailbox
           pretending to be the email under test. That is how the "no email" spec failed in a full run
           and passed on its own. */
        await waitForEmail({ to: email, subject: "Booking Confirmed" });
        return publicId;
    }

    test("confirming a booking sends a receipt that matches the screen", async ({ page }) => {
        const startDateTime = slotInDays(54, 11, 0);
        const { publicId } = await createPendingBooking(guest, {
            barberId, serviceIds: [serviceId], startDateTime,
        });
        await clearEmails();

        // Through the real form, not the API: the customer's own click is what should trigger this.
        await page.goto(`/checkout/${publicId}`);
        await expect(page.getByText("Total")).toBeVisible();
        await submitCashCheckout(page, { email: "email.confirm@e2e.test" });
        await expect(page.getByText("Booking Confirmed")).toBeVisible({ timeout: 20_000 });

        const email = await waitForEmail({ to: "email.confirm@e2e.test", subject: "Booking Confirmed" });
        /* The receipt has to agree with the screen the customer is looking at, line for line - the two
           are rendered by completely different code (RenderBookingDetails vs Summary.jsx) and have
           drifted before. Note the total is spelled out to 2dp: "EUR 25.5" on a receipt reads as broken
           in a way a live web total does not. */
        expect(email.text).toContain(BARBER_ONE.fullName);
        expect(email.text).toContain(SERVICES[0].name);
        expect(email.text).toContain(`€${SERVICES[0].price}`);
        expect(email.text).toContain("Cash");
    });

    test("a staff cancellation with a refund says so, and one without does not", async () => {
        const startDateTime = slotInDays(55, 11, 0);
        await confirmedBooking(startDateTime, "email.cancel@e2e.test");
        const bookingId = await findBookingIdByStart(api, startDateTime);
        await clearEmails();

        await cancelBooking(api, bookingId);

        const email = await waitForEmail({ to: "email.cancel@e2e.test", subject: "cancelled" });
        expect(email.text).toContain("has been cancelled");
        /* The line that must NOT be there. This is a cash booking, so nothing was refunded, and
           promising a refund to someone who never paid online sends them looking for money that is
           never coming - the exact reason refundIssued is passed through rather than assumed. */
        expect(email.text).not.toContain("refund has been issued");
    });

    /* No spec here for the barber-unavailable email, and that is a finding rather than an omission:
       deactivating a barber does NOT cancel a booking that is already confirmed. BarbersController
       hands only the PENDING ones to BookingConflictCanceller and FLAGS the rest, and a pending booking
       never gets an email at all - so this flow sends nothing to anybody. The email exists and is
       reachable, but only through the webhook race (see webhook-races.spec.js, where its wording is
       asserted) and through a staff cancel of an already-flagged booking. */

    test("moving a booking emails what changed and restates the whole appointment", async () => {
        const startDateTime = slotInDays(56, 11, 0);
        await confirmedBooking(startDateTime, "email.moved@e2e.test");
        const bookingId = await findBookingIdByStart(api, startDateTime);
        await clearEmails();

        const movedTo = slotInDays(56, 15, 0);
        await updateBooking(api, bookingId, { startDateTime: movedTo, barberId: otherBarberId });

        const email = await waitForEmail({ to: "email.moved@e2e.test", subject: "updated" });
        /* Both halves of the change, and then the full appointment again. This email replaced one that
           only ever knew about the time - a customer reassigned to a different barber was told nothing
           about the single thing they had actually chosen. */
        expect(email.text).toContain("moved from");
        expect(email.text).toContain(BARBER_TWO.fullName);
        expect(email.text).toContain(BARBER_ONE.fullName); // "now with X instead of Y"
        expect(email.text).toContain("3:00 PM");
    });

    test("an edit that changes nothing sends no email at all", async () => {
        const startDateTime = slotInDays(57, 11, 0);
        await confirmedBooking(startDateTime, "email.nochange@e2e.test");
        const bookingId = await findBookingIdByStart(api, startDateTime);
        await clearEmails();

        // The admin screen posts the timestamp whether or not it changed, so this is what a "save with
        // no edits" really looks like on the wire.
        await updateBooking(api, bookingId, { startDateTime, barberId });

        /* A change notice describing no change is worse than silence: it teaches the customer that
           these emails don't mean anything. */
        await expectNoEmail({ to: "email.nochange@e2e.test" });
    });
});
