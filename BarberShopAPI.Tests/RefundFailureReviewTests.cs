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
    }
}
