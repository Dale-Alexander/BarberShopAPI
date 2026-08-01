using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Services;
using BarberShopAPI.Tests.Infrastructure;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* PATCH /api/bookings/cancel/{id} - the staff cancel button, and with it BookingCanceller's policy
     * rules (CancellationReason.AdminCancelled).
     *
     * This is the one path where the 24h refund cutoff actually bites: it's a customer-facing cancellation,
     * not a shop-side one, so unlike closures and barber-deactivation the refund is NOT forced.
     *
     * Everything here stays clear of Stripe. That includes the no-refund case, which is genuinely testable:
     * BookingCanceller evaluates refundAllowed BEFORE it calls Stripe, so a card booking inside the cutoff
     * returns CancelledNoRefund without a network call. The refund-succeeds and refund-fails branches do
     * call Stripe and belong to tier 2. */
    public class AdminCancelTests : IntegrationTestBase
    {
        public AdminCancelTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        [Fact]
        public async Task Cancelling_a_confirmed_cash_booking_records_the_reason_and_emails_the_customer()
        {
            AuthenticateAsAdmin();
            int bookingId, paymentId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                bookingId = booking.Id;
                paymentId = db.AddCashPayment(booking.Id).Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("cancelled successfully", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking2.Status);
            Assert.Equal(CancellationReason.AdminCancelled, booking2.CancellationReason);

            // The plain cancellation notice - not the closure or barber-unavailable wording.
            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendBookingCancellationEmailAsync" && j.BookingId == bookingId);

            // Nothing to refund on cash, so the payment is left as settled.
            Assert.Equal(PaymentStatus.COMPLETED,
                (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        [Fact]
        public async Task A_cash_booking_inside_the_refund_cutoff_is_a_plain_cancel_not_a_policy_penalty()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                // Three hours away, well inside the 24h cutoff.
                var booking = db.AddBooking(barber.Id, ShopClock.Now.AddHours(3), BookingStatus.COMPLETED);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // CancelledNoRefund is only for a refund the policy actually WITHHELD. Cash had nothing to
            // refund, so warning the admin about a forfeited refund would be misleading.
            var message = (await ReadJson(response)).GetProperty("message").GetString();
            Assert.Contains("cancelled successfully", message);
            Assert.DoesNotContain("No refund", message);
        }

        [Fact]
        public async Task A_phone_only_booking_cancelled_by_staff_is_not_flagged_for_review()
        {
            /* The mirror of the closure/deactivation behaviour, and the reason that flag is keyed to who
             * initiated the cancellation rather than to the missing email. A staff cancel happens with an
             * admin at the screen, usually because this customer just phoned in - they already know, so
             * putting them in the worklist would be busywork. */
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED,
                    contactEmail: null);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking2.Status);
            Assert.False(booking2.NeedsReview);
            Assert.Null(booking2.ReviewReason);
        }

        [Fact]
        public async Task A_card_booking_inside_the_refund_cutoff_is_cancelled_with_the_refund_withheld()
        {
            AuthenticateAsAdmin();
            int bookingId, paymentId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, ShopClock.Now.AddHours(3), BookingStatus.COMPLETED);
                bookingId = booking.Id;
                // refundAllowed is false, so BookingCanceller short-circuits before any Stripe call.
                paymentId = db.AddCardPayment(booking.Id).Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("No refund was issued", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.CANCELLED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
            // No refund was attempted, so the payment must stay COMPLETED rather than REFUNDED.
            Assert.Equal(PaymentStatus.COMPLETED,
                (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        [Fact]
        public async Task The_refund_cutoff_comes_from_shop_settings()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                // Raise the cutoff so a booking 30 hours out - refundable under the 24h default - now falls
                // inside the no-refund window. Proves the value is read from settings, not hard-coded.
                db.SetShopSetting(s => s.RefundCutoffHours = 48);
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, ShopClock.Now.AddHours(30), BookingStatus.COMPLETED);
                bookingId = booking.Id;
                db.AddCardPayment(booking.Id);
            }

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("No refund was issued", (await ReadJson(response)).GetProperty("message").GetString());
        }

        [Fact]
        public async Task Cancelling_deletes_the_scheduled_reminder_job()
        {
            AuthenticateAsAdmin();
            int bookingId;
            string reminderJobId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                bookingId = booking.Id;

                // A real scheduled job, so the deletion is genuinely exercised rather than mocked away.
                reminderJobId = BackgroundJob.Schedule<IEmailService>(
                    s => s.sendBookingReminderEmailAsync(booking.Id), TimeSpan.FromDays(13));
                booking.ReminderJobId = reminderJobId;
                db.SaveChanges();
            }

            Assert.Contains(JobStorage.Current.GetMonitoringApi().ScheduledJobs(0, 100),
                j => j.Key == reminderJobId);

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Otherwise the customer gets a "see you in 2 hours" reminder for an appointment that's gone.
            Assert.DoesNotContain(JobStorage.Current.GetMonitoringApi().ScheduledJobs(0, 100),
                j => j.Key == reminderJobId);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Null(booking2.ReminderJobId);
            Assert.Null(booking2.ReminderSentAt);
        }

        [Theory]
        [InlineData(BookingStatus.PENDING, "Cannot cancel a pending booking")]
        [InlineData(BookingStatus.CANCELLED, "already cancelled")]
        public async Task A_booking_that_is_not_confirmed_cannot_be_cancelled_here(
            BookingStatus status, string expectedMessage)
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), status).Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expectedMessage, (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            // A PENDING booking's payment may still be in flight, so this path must leave it exactly as is
            // - it needs the PaymentIntent handling the closure flow and expiry job do, not a refund.
            Assert.Equal(status, (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Cancelling_a_booking_that_does_not_exist_is_a_404()
        {
            AuthenticateAsAdmin();

            var response = await Client.PatchAsync("/api/bookings/cancel/999999", null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task A_barber_may_cancel_their_own_booking()
        {
            int barberUserId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberUserId = barber.UserId;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }
            Client.Authenticate(barberUserId, Role.BARBER, tokenVersion: 0);

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.CANCELLED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
        }

        [Fact]
        public async Task A_barber_may_not_cancel_another_barbers_booking()
        {
            int callerUserId, otherBarbersBookingId;
            using (var db = NewDb())
            {
                var caller = db.AddBarber();
                var other = db.AddBarber();
                db.AddSchedule(caller.Id, ShopClock.Today.AddDays(-30));
                db.AddSchedule(other.Id, ShopClock.Today.AddDays(-30));
                callerUserId = caller.UserId;
                otherBarbersBookingId = db.AddBooking(other.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }
            Client.Authenticate(callerUserId, Role.BARBER, tokenVersion: 0);

            var response = await Client.PatchAsync($"/api/bookings/cancel/{otherBarbersBookingId}", null);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.COMPLETED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == otherBarbersBookingId)).Status);
        }

        [Fact]
        public async Task An_unauthenticated_caller_cannot_cancel_a_booking()
        {
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.COMPLETED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
        }
    }
}
