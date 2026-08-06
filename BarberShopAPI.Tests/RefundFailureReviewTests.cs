using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* Tier 2: what happens when Stripe won't give the money back.
     *
     * Covers BookingConflictCanceller's NeedsReview site and BookingCanceller's RefundFailed outcome. The
     * invariant they protect is the one that actually costs money: NEVER cancel a booking while still
     * holding the customer's payment. So the refund is attempted BEFORE the cancellation, and a failure
     * either stops the cancellation outright (staff cancel - there's a human to retry) or flags the booking
     * for manual reconciliation (shop-side cancel - nothing will retry and no one is watching).
     *
     * Stripe is faked at the HTTP boundary, so the real StripeRefunds runs - including its
     * charge_already_refunded handling, which is the difference between "refund succeeded" and "we tried to
     * refund twice". */
    public class RefundFailureReviewTests : IntegrationTestBase
    {
        public RefundFailureReviewTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        /// <summary>A confirmed, card-paid booking well outside the refund cutoff, so a refund is genuinely due.</summary>
        private (int BarberId, int BookingId, int PaymentId) ArrangePaidCardBooking(int daysAhead = 14)
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            var booking = db.AddBooking(barber.Id, TestData.FutureAt(daysAhead, 16), BookingStatus.COMPLETED);
            var payment = db.AddCardPayment(booking.Id);
            return (barber.Id, booking.Id, payment.Id);
        }

        // ---------------------------------------------------------------------------------------------
        // Shop-side cancellations - BookingConflictCanceller's NeedsReview site
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_closure_never_touches_a_confirmed_bookings_money()
        {
            AuthenticateAsAdmin();
            var (_, bookingId, paymentId) = ArrangePaidCardBooking();

            // Armed to fail, but it must never be called at all: a closure no longer cancels or refunds a
            // confirmed booking, it hands it to the admin. Refunding here would move money on a decision
            // nobody has made yet - and a mistyped closure date would do it irreversibly.
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.PostAsync("/api/dates", Body(new
            {
                barberId = (int?)null,
                startDate = ShopClock.Today.AddDays(14),
                endDate = (DateOnly?)null,
                isFullDay = true,
                startTime = (TimeOnly?)null,
                endTime = (TimeOnly?)null,
                reason = "Test closure",
                confirmCancelBookings = true
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, stripe.RefundAttempts);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.True(booking.NeedsReview);

            Assert.Equal(PaymentStatus.COMPLETED, (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Cancelling_a_booking_the_closure_flagged_refunds_in_full_and_sends_the_closure_email()
        {
            AuthenticateAsAdmin();
            // Deliberately INSIDE the refund cutoff, which is the case that used to go wrong: the closure
            // used to guarantee a full refund, so routing this through the admin's cancel button had to keep
            // that guarantee rather than applying the customer-only 24h penalty to the shop's own decision.
            var (_, bookingId, paymentId) = ArrangePaidCardBooking(daysAhead: 0);
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            var closure = await Client.PostAsync("/api/dates", Body(new
            {
                barberId = (int?)null,
                startDate = ShopClock.Today,
                endDate = (DateOnly?)null,
                isFullDay = true,
                startTime = (TimeOnly?)null,
                endTime = (TimeOnly?)null,
                reason = "Test closure",
                confirmCancelBookings = true
            }));
            Assert.Equal(HttpStatusCode.OK, closure.StatusCode);

            // The admin works the flagged row and decides to cancel it.
            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Not the "no refund within 24 hours" message - the shop closed the shop, not the customer.
            Assert.DoesNotContain("No refund", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            // Worked out from the slot's state, not from who clicked cancel.
            Assert.Equal(CancellationReason.ShopClosure, booking.CancellationReason);
            Assert.Equal(PaymentStatus.REFUNDED, (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);

            // The closure email, not the generic cancellation notice.
            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendBookingCancelledDueToClosureEmailAsync" && j.BookingId == bookingId);
        }

        [Fact]
        public async Task Deactivating_a_barber_never_touches_a_confirmed_bookings_money()
        {
            AuthenticateAsAdmin();
            var (barberId, bookingId, paymentId) = ArrangePaidCardBooking();

            // Armed to fail, but it should never be called: deactivation no longer cancels or refunds a
            // confirmed booking, it hands it to the admin. A refund here would be money moved on a
            // decision nobody has made yet.
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, stripe.RefundAttempts);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);

            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.True(booking.NeedsReview);
            Assert.Equal(PaymentStatus.COMPLETED, (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        [Fact]
        public async Task An_already_refunded_charge_counts_as_success_rather_than_a_failure()
        {
            AuthenticateAsAdmin();
            var (_, bookingId, paymentId) = ArrangePaidCardBooking();

            // Stripe says the money is already back - a previous attempt whose DB write failed, or a
            // manual refund. There is nothing left to do, so this must NOT be treated as a failure and
            // must NOT land in the worklist. Driven through the staff cancel now that deactivation no
            // longer refunds anything.
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.AlreadyRefunded };

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.AdminCancelled, booking.CancellationReason);
            Assert.False(booking.NeedsReview);
            Assert.Equal(PaymentStatus.REFUNDED, (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        // ---------------------------------------------------------------------------------------------
        // Staff cancel - BookingCanceller's RefundFailed outcome
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_staff_cancel_whose_refund_fails_returns_502_and_leaves_the_booking_intact()
        {
            AuthenticateAsAdmin();
            var (_, bookingId, paymentId) = ArrangePaidCardBooking();

            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            // 502, not 500: the failure is Stripe's, and the message tells the admin the booking was NOT
            // cancelled so they know to try again rather than assuming it went through.
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var message = (await ReadJson(response)).GetProperty("message").GetString();
            Assert.Contains("was not cancelled", message);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.Equal(CancellationReason.None, booking.CancellationReason);
            // Unlike the shop-side path, nothing is flagged - there is a human here who can retry.
            Assert.False(booking.NeedsReview);
            Assert.Equal(PaymentStatus.COMPLETED, (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_staff_cancel_outside_the_cutoff_refunds_then_cancels()
        {
            AuthenticateAsAdmin();
            var (_, bookingId, paymentId) = ArrangePaidCardBooking();

            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, stripe.RefundAttempts);

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.CANCELLED, (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
            Assert.Equal(PaymentStatus.REFUNDED, (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        [Fact]
        public async Task Forcing_a_refund_inside_the_cutoff_overrides_the_no_refund_policy()
        {
            AuthenticateAsAdmin();
            int bookingId, paymentId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, ShopClock.Now.AddHours(3), BookingStatus.COMPLETED);
                bookingId = booking.Id;
                paymentId = db.AddCardPayment(booking.Id).Id;
            }

            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            // Inside the cutoff this would normally withhold the refund; refundAnyway is the staff override
            // for goodwill or a shop-side reason that isn't a formal closure.
            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}?refundAnyway=true", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("No refund", (await ReadJson(response)).GetProperty("message").GetString());
            Assert.Equal(1, stripe.RefundAttempts);

            using var assertDb = NewDb();
            Assert.Equal(PaymentStatus.REFUNDED, (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        [Fact]
        public async Task A_cancel_inside_the_cutoff_never_contacts_stripe_at_all()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, ShopClock.Now.AddHours(3), BookingStatus.COMPLETED);
                bookingId = booking.Id;
                db.AddCardPayment(booking.Id);
            }

            // Refunds are set to fail: if the policy check were evaluated in the wrong order, the cancel
            // would break. It must never get that far.
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("No refund was issued", (await ReadJson(response)).GetProperty("message").GetString());
            Assert.Equal(0, stripe.RefundAttempts);
        }

        // ---------------------------------------------------------------------------------------------
        // The ASYNC refund failure - Stripe accepted the refund, then failed it hours later
        // ---------------------------------------------------------------------------------------------

        /* Everything above is the synchronous failure: StripeRefunds returns Failed and the caller reacts
         * on the spot. This section is the other one, which no amount of test-mode card work can reach -
         * `4000 0000 0000 5126` returns a refund of status `succeeded` and only flips it to `failed` later,
         * in a webhook. So by the time the shop finds out, the booking is CANCELLED, the Payment row says
         * REFUNDED and the customer has an email promising their money back. Nothing retries, and until
         * this branch existed the event was discarded before the controller's switch even saw it.
         *
         * Arranged in exactly that end state, because that IS the state Stripe's event arrives into. */
        private (int BookingId, string PaymentIntentId, string Phone) ArrangeRefundedBooking()
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.CANCELLED);
            var payment = db.AddCardPayment(booking.Id, status: PaymentStatus.REFUNDED);
            var phone = db.Users.Single(u => u.Id == booking.UserId).Phone;
            return (booking.Id, payment.StripePaymentIntentId!, phone!);
        }

        [Fact]
        public async Task A_refund_that_fails_after_stripe_accepted_it_reaches_the_worklist()
        {
            var (bookingId, paymentIntentId, phone) = ArrangeRefundedBooking();
            // Armed to fail, and it must never be called: this branch does not retry the refund. A refund the
            // bank rejected usually needs a different route entirely, and a silent retry would bury it.
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.SendAsync(StripeWebhookRequest.RefundOutcome(paymentIntentId, amount: 2550));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, stripe.RefundAttempts);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.True(booking.NeedsReview);

            // What the admin has to be able to act on: that it failed, how much is still with the shop, the
            // id to search for in Stripe, and the number to ring - the customer was already told otherwise.
            Assert.Contains(ReviewMarkers.RefundFailed, booking.ReviewReason);
            Assert.Contains("25.50", booking.ReviewReason);
            Assert.Contains("re_test_refund", booking.ReviewReason);
            Assert.Contains(phone, booking.ReviewReason);

            // No second automated email. They have one saying the money is coming; the correction is a call.
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_refund_that_settles_normally_flags_nothing()
        {
            var (bookingId, paymentIntentId, _) = ArrangeRefundedBooking();

            // The same event fires as a refund settles - the failure is the only news in it. Flagging on
            // arrival rather than on status would put EVERY refunded booking in the worklist.
            var response = await Client.SendAsync(StripeWebhookRequest.RefundOutcome(paymentIntentId, status: "succeeded"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        [Fact]
        public async Task The_newer_event_name_lands_in_the_same_branch()
        {
            var (bookingId, paymentIntentId, _) = ArrangeRefundedBooking();

            /* This account sends the legacy `charge.refund.updated`; a newer API version sends
             * `refund.failed` for the same thing. The controller dispatches on the Refund OBJECT, not the
             * event name, so an API version bump must not quietly stop the shop hearing about failed
             * refunds. Same payload, different name, same outcome. */
            var response = await Client.SendAsync(
                StripeWebhookRequest.RefundOutcome(paymentIntentId, eventType: "refund.failed"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.True(booking.NeedsReview);
            Assert.Contains(ReviewMarkers.RefundFailed, booking.ReviewReason);
        }

        [Fact]
        public async Task A_redelivered_failure_does_not_write_the_note_twice()
        {
            var (bookingId, paymentIntentId, _) = ArrangeRefundedBooking();

            // Stripe redelivers, and a refund reports more than once as it settles. The note carries the
            // refund id, which is what makes the repeat identical and lets FlagForReview swallow it.
            for (var i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.OK,
                    (await Client.SendAsync(StripeWebhookRequest.RefundOutcome(paymentIntentId))).StatusCode);

            using var assertDb = NewDb();
            var reason = (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).ReviewReason;
            Assert.Equal(1, reason!.Split(ReviewMarkers.RefundFailed).Length - 1);
        }

        [Fact]
        public async Task A_failed_refund_for_a_payment_we_never_recorded_is_accepted_and_ignored()
        {
            // A refund issued by hand in the Dashboard against a charge this shop has no Payment row for.
            // Nothing to flag, and Stripe retrying will not conjure one - so take the event and move on
            // rather than 500ing into its retry schedule forever.
            var response = await Client.SendAsync(StripeWebhookRequest.RefundOutcome("pi_not_a_booking_of_ours"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
