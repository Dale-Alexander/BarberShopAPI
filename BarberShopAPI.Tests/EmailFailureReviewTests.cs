using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Services;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Tests
{
    /* Tier 2: the four places EmailService sets NeedsReview when an email can't be delivered.
     *
     * All four guard the same risk. A shop-side cancellation email is the customer's ONLY notice that their
     * confirmed appointment is gone - nobody is standing in front of them to say so. If it just failed, the
     * booking would vanish into Hangfire's failed-jobs list and the customer would turn up to a closed shop
     * or an absent barber. So each job retries while attempts remain, and only once they are spent does it
     * give up and flag the booking for a manual phone call.
     *
     * Both halves of that decision are tested at each site: still-retrying must RETHROW (so Hangfire tries
     * again), exhausted must FLAG and swallow. A site that flagged too early would stop retrying a merely
     * transient blip; one that never flagged would lose the customer entirely. */
    public class EmailFailureReviewTests : IntegrationTestBase
    {
        public EmailFailureReviewTests(DatabaseFixture fixture) : base(fixture) { }

        // The real EmailService, wired to a Resend that always fails. Everything else is genuine.
        private static EmailService FailingEmailService(BarberShopAPI.Data.BarberShopContext db) =>
            new(ThrowingResend.Create(), db);

        private int ArrangeCancelledBooking(BookingStatus status = BookingStatus.CANCELLED, string? contactEmail = "customer@example.test")
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            return db.AddBooking(barber.Id, TestData.FutureAt(14, 16), status, contactEmail: contactEmail).Id;
        }

        // ---------------------------------------------------------------------------------------------
        // Barber-unavailable email
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Barber_unavailable_email_flags_the_booking_once_its_retries_are_spent()
        {
            var bookingId = ArrangeCancelledBooking();

            using var db = NewDb();
            await FailingEmailService(db).sendBookingCancelledBarberUnavailableEmailAsync(
                bookingId, refundIssued: true, HangfireRetryContext.WithRetryCount(EmailJobPolicy.BarberUnavailableRetries));

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.True(booking.NeedsReview);
            // The worklist entry has to tell staff what to say, not just that something failed.
            Assert.Contains("Call the customer", booking.ReviewReason);
            Assert.Contains("no longer available", booking.ReviewReason);
            Assert.Contains(ThrowingResend.FailureMessage, booking.ReviewReason);
            // refundIssued: true, so the note tells staff the money is already back.
            Assert.Contains("refunded in full", booking.ReviewReason);
        }

        [Fact]
        public async Task Barber_unavailable_email_rethrows_while_retries_remain()
        {
            var bookingId = ArrangeCancelledBooking();

            using var db = NewDb();
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                FailingEmailService(db).sendBookingCancelledBarberUnavailableEmailAsync(
                    bookingId, refundIssued: false, HangfireRetryContext.WithRetryCount(EmailJobPolicy.BarberUnavailableRetries - 1)));

            using var assertDb = NewDb();
            // Not flagged yet - a transient blip must not land in the worklist while Hangfire is still trying.
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        // ---------------------------------------------------------------------------------------------
        // Closure-cancellation email
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Closure_cancellation_email_flags_the_booking_once_its_retries_are_spent()
        {
            var bookingId = ArrangeCancelledBooking();

            using var db = NewDb();
            await FailingEmailService(db).sendBookingCancelledDueToClosureEmailAsync(bookingId, HangfireRetryContext.Exhausted);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.True(booking.NeedsReview);
            Assert.Contains("shop closure", booking.ReviewReason);
            Assert.Contains(ThrowingResend.FailureMessage, booking.ReviewReason);
        }

        [Fact]
        public async Task Closure_cancellation_email_rethrows_while_retries_remain()
        {
            var bookingId = ArrangeCancelledBooking();

            using var db = NewDb();
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                FailingEmailService(db).sendBookingCancelledDueToClosureEmailAsync(bookingId, HangfireRetryContext.RetriesRemaining));

            using var assertDb = NewDb();
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        // ---------------------------------------------------------------------------------------------
        // Schedule-change cancellation email
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Schedule_change_email_flags_the_booking_with_its_own_wording()
        {
            var bookingId = ArrangeCancelledBooking();

            using var db = NewDb();
            await FailingEmailService(db).sendBookingCancelledDueToScheduleChangeEmailAsync(bookingId, HangfireRetryContext.Exhausted);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.True(booking.NeedsReview);
            // Must not tell staff to say the shop was closed when it was a schedule change.
            Assert.Contains("schedule change", booking.ReviewReason);
            Assert.DoesNotContain("shop closure", booking.ReviewReason);
        }

        [Fact]
        public async Task Schedule_change_email_rethrows_while_retries_remain()
        {
            var bookingId = ArrangeCancelledBooking();

            using var db = NewDb();
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                FailingEmailService(db).sendBookingCancelledDueToScheduleChangeEmailAsync(bookingId, HangfireRetryContext.RetriesRemaining));

            using var assertDb = NewDb();
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        // ---------------------------------------------------------------------------------------------
        // Refund-notice email - the one site with two outcomes
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Refund_notice_flags_the_booking_when_the_booking_could_not_be_confirmed()
        {
            // CANCELLED: the customer has no appointment AND a charge on their statement, so this email is
            // their only notice of either.
            var bookingId = ArrangeCancelledBooking(BookingStatus.CANCELLED);

            using var db = NewDb();
            await FailingEmailService(db).sendPaymentRefundedUnconfirmedEmailAsync(
                bookingId, HangfireRetryContext.WithRetryCount(EmailJobPolicy.RefundNoticeRetries));

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.True(booking.NeedsReview);
            Assert.Contains("couldn't be confirmed", booking.ReviewReason);
        }

        [Fact]
        public async Task Refund_notice_for_a_booking_that_still_stands_fails_quietly_without_flagging()
        {
            // COMPLETED: the appointment is fine and we only clawed back a duplicate card charge. The email
            // is a courtesy, so it is left to fail rather than pulling staff onto a phone call about nothing.
            var bookingId = ArrangeCancelledBooking(BookingStatus.COMPLETED);

            using var db = NewDb();
            // Throws even with retries exhausted - the bookingStillStands branch returns before the
            // retry check, so it never reaches the flagging path at any attempt count.
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                FailingEmailService(db).sendPaymentRefundedUnconfirmedEmailAsync(
                    bookingId, HangfireRetryContext.WithRetryCount(EmailJobPolicy.RefundNoticeRetries)));

            using var assertDb = NewDb();
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        // ---------------------------------------------------------------------------------------------
        // Shared guards
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_booking_with_no_email_on_file_is_skipped_rather_than_flagged()
        {
            // Admin-created bookings carry only a phone number. There was never an email to fail, so this
            // is not a delivery failure and must not land in the worklist - staff were always going to phone.
            var bookingId = ArrangeCancelledBooking(contactEmail: null);

            using var db = NewDb();
            await FailingEmailService(db).sendBookingCancelledDueToClosureEmailAsync(bookingId, HangfireRetryContext.Exhausted);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        [Fact]
        public async Task A_missing_booking_is_ignored_rather_than_throwing()
        {
            // A job can outlive its booking. Throwing here would put a permanently un-retryable job in the
            // failed list forever.
            using var db = NewDb();
            await FailingEmailService(db).sendBookingCancelledDueToClosureEmailAsync(999999, HangfireRetryContext.Exhausted);
        }
    }
}
