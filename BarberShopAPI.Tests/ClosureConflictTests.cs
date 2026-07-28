using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* POST /api/dates - creating a shop closure, and the bookings it clears off the slot
     * (BookingConflictCanceller via CancellationReason.ShopClosure).
     *
     * Two-step by design: the first request reports the conflicts and creates nothing; the admin re-submits
     * with ConfirmCancelBookings to go ahead. The 409 payload's WillBeEmailed flags matter operationally -
     * they tell the admin exactly which customers the system will notify and which they must phone.
     *
     * Cash/unpaid bookings only, so the refund branch is never entered (that's tier 2). */
    public class ClosureConflictTests : IntegrationTestBase
    {
        public ClosureConflictTests(DatabaseFixture fixture) : base(fixture) { }

        private static object ClosureBody(DateOnly startDate, bool confirm, int? barberId = null,
            bool isFullDay = true, TimeOnly? startTime = null, TimeOnly? endTime = null) => new
            {
                barberId,
                startDate,
                endDate = (DateOnly?)null,
                isFullDay,
                startTime,
                endTime,
                reason = "Test closure",
                confirmCancelBookings = confirm
            };

        private int AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
            return admin.Id;
        }

        [Fact]
        public async Task An_overlapping_booking_is_reported_first_and_the_closure_is_not_created()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false)));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            var conflicts = payload.GetProperty("conflicts").EnumerateArray().ToList();
            Assert.Single(conflicts);
            Assert.Equal(bookingId, conflicts[0].GetProperty("id").GetInt32());
            // COMPLETED + has a ContactEmail => the system will email this one automatically.
            Assert.True(conflicts[0].GetProperty("willBeEmailed").GetBoolean());

            using var assertDb = NewDb();
            Assert.Empty(await assertDb.ShopClosures.ToListAsync());
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.Equal(CancellationReason.None, booking2.CancellationReason);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Confirming_creates_the_closure_and_cancels_the_booking_with_a_closure_email()
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

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Single(await assertDb.ShopClosures.ToListAsync());

            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking2.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking2.CancellationReason);
            Assert.False(booking2.NeedsReview); // the cancel succeeded, so nothing to follow up by hand

            // The closure email, specifically - not the generic cancellation notice.
            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendBookingCancelledDueToClosureEmailAsync" && j.BookingId == bookingId);

            // Cash never went through Stripe, so there is nothing to refund - the payment stays COMPLETED
            // rather than being marked REFUNDED. The customer is settled up in person.
            Assert.Equal(PaymentStatus.COMPLETED,
                (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        [Fact]
        public async Task A_pending_conflict_is_cancelled_but_never_emailed()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                // No StripePaymentIntentId, so the PaymentIntent-void branch is skipped entirely.
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.PENDING).Id;
            }

            var warn = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false)));
            Assert.Equal(HttpStatusCode.Conflict, warn.StatusCode);
            // The admin is told up front that this one can't be reached by email.
            var conflicts = (await ReadJson(warn)).GetProperty("conflicts").EnumerateArray().ToList();
            Assert.False(conflicts[0].GetProperty("willBeEmailed").GetBoolean());

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking.CancellationReason);
            // A pending booking never got a confirmation, so there is nothing to walk back.
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_confirmed_booking_with_no_email_on_file_is_flagged_as_phone_only()
        {
            AuthenticateAsAdmin();
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                // Admin-created bookings carry only a phone number.
                db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED, contactEmail: null);
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false)));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await ReadJson(response);
            Assert.False(payload.GetProperty("conflicts")[0].GetProperty("willBeEmailed").GetBoolean());
            Assert.Contains("must be contacted by phone", payload.GetProperty("message").GetString());
        }

        [Fact]
        public async Task A_barber_scoped_closure_leaves_other_barbers_bookings_alone()
        {
            AuthenticateAsAdmin();
            int closedBarberBookingId, otherBarberBookingId, closedBarberId;
            using (var db = NewDb())
            {
                var closed = db.AddBarber();
                var other = db.AddBarber();
                db.AddSchedule(closed.Id, ShopClock.Today.AddDays(-30));
                db.AddSchedule(other.Id, ShopClock.Today.AddDays(-30));
                closedBarberId = closed.Id;
                closedBarberBookingId = db.AddBooking(closed.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
                // Same slot, different chair - untouched by a closure scoped to one barber.
                otherBarberBookingId = db.AddBooking(other.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true, barberId: closedBarberId)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.CANCELLED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == closedBarberBookingId)).Status);
            Assert.Equal(BookingStatus.COMPLETED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == otherBarberBookingId)).Status);
        }

        [Fact]
        public async Task A_booking_that_ends_exactly_when_a_partial_closure_starts_does_not_conflict()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                // 14:00 + 30min ends exactly at 14:30, where the closure begins. The overlap test is
                // half-open (end > closureStart), so this must NOT be swept up.
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 14), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false, isFullDay: false,
                    startTime: new TimeOnly(14, 30), endTime: new TimeOnly(17, 0))));

            // No conflict reported at all - the closure is created straight away.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.COMPLETED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
        }

        [Fact]
        public async Task A_past_booking_is_not_swept_up_by_a_closure_on_a_future_date()
        {
            AuthenticateAsAdmin();
            int pastBookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                pastBookingId = db.AddBooking(barber.Id, TestData.FutureAt(-3, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == pastBookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.Equal(CancellationReason.None, booking.CancellationReason);
        }
    }
}
