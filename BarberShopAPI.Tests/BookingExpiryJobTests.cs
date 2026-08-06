using BarberShopAPI.Common;
using BarberShopAPI.CronJob;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Tests
{
    /* Tier 2: the expiry job, and the one case in it where money is already gone.
     *
     * The job's normal work is freeing slots held by abandoned checkouts. The interesting branch is the
     * other one: it cancels the PaymentIntent BEFORE cancelling the booking, and when Stripe refuses that
     * cancel it means the card has already been charged. The job then deliberately leaves the booking
     * alone rather than binning a booking somebody has paid for - which is right, but until now it left
     * that booking stuck and invisible. A PENDING booking is not on the admin table (admin-fetch lists
     * only bookings with a payment recorded), the customer has no confirmation email, and nothing else in
     * the app ever looks at it again.
     *
     * So after an hour it goes on the Needs Review worklist, which is the one screen that shows a booking
     * whatever its status. These tests cover that, and pin the delay that keeps a merely slow webhook out
     * of the worklist. Stripe is faked at the HTTP boundary, so the real job code runs. */
    public class BookingExpiryJobTests : IntegrationTestBase
    {
        public BookingExpiryJobTests(DatabaseFixture fixture) : base(fixture) { }

        /// <summary>A PENDING booking with a card payment in flight, created `minutesAgo` ago.</summary>
        private int ArrangeAbandonedCheckout(int minutesAgo)
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.PENDING);
            booking.StripePaymentIntentId = "pi_test_expiry_job";
            // The job selects on CreatedAt, which defaults to now - so the age has to be set explicitly.
            booking.CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
            db.SaveChanges();
            return booking.Id;
        }

        private async Task RunJob()
        {
            using var db = NewDb();
            await new BookingExpiryJob(db).CancelExpiredBookingsAsync();
        }

        [Fact]
        public async Task A_booking_paid_for_but_never_confirmed_reaches_the_worklist()
        {
            var bookingId = ArrangeAbandonedCheckout(minutesAgo: 90);
            // Stripe refuses the cancel because the charge already succeeded - the customer's money is gone.
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Fails };

            await RunJob();

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);

            // Never binned: somebody has paid for this slot, so the job must leave it standing.
            Assert.Equal(BookingStatus.PENDING, booking.Status);
            Assert.True(booking.NeedsReview);
            // What staff need: that it was paid, that the customer heard nothing, and the id to look up.
            Assert.Contains("paid for but never confirmed", booking.ReviewReason);
            Assert.Contains("pi_test_expiry_job", booking.ReviewReason);
        }

        [Fact]
        public async Task A_webhook_that_is_merely_slow_is_left_to_arrive()
        {
            // Past the 15 minutes that brings it into the job's query, well inside the hour that makes it
            // a problem. Stripe retries for days and almost all of these settle on their own - flagging at
            // 15 minutes would fill the worklist with bookings that were about to fix themselves.
            var bookingId = ArrangeAbandonedCheckout(minutesAgo: 20);
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Fails };

            await RunJob();

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.PENDING, booking.Status);
            Assert.False(booking.NeedsReview);
        }

        [Fact]
        public async Task An_abandoned_checkout_that_was_never_paid_is_cancelled_quietly()
        {
            // The job's ordinary work, and the case that must NOT change: the cancel succeeds, so Stripe
            // guarantees the card can never be charged for this booking. Nothing to reconcile, nobody to
            // tell - just free the slot.
            var bookingId = ArrangeAbandonedCheckout(minutesAgo: 90);
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Succeeds };

            await RunJob();

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.False(booking.NeedsReview);
        }

        [Fact]
        public async Task Repeated_runs_leave_one_note_not_one_per_run()
        {
            // This job runs on a schedule, so a stuck booking meets it again and again - by far the most
            // frequent repeat-flag path in the app. The note must not grow every few minutes.
            var bookingId = ArrangeAbandonedCheckout(minutesAgo: 90);
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Fails };

            await RunJob();
            await RunJob();
            await RunJob();

            using var assertDb = NewDb();
            var reason = (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).ReviewReason;
            Assert.Equal(1, reason!.Split("paid for but never confirmed").Length - 1);
        }
    }
}
