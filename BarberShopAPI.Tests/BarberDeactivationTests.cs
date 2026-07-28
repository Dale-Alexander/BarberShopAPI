using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* DELETE /api/barbers/delete/{id} - deactivating a barber, and the upcoming bookings it clears
     * (BookingConflictCanceller via CancellationReason.BarberUnavailable).
     *
     * Same two-step shape as a closure, but two things differ and both matter:
     *   - the refund is FORCED (forceRefund, not dueToClosure), so the 24h customer penalty never applies
     *     to a shop-side cancellation;
     *   - the email is the barber-unavailable one, which is the customer's only notice that the person
     *     they booked has gone, so they know to rebook with someone else.
     *
     * Cash/unpaid bookings only - the refund branch itself is tier 2. */
    public class BarberDeactivationTests : IntegrationTestBase
    {
        public BarberDeactivationTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        [Fact]
        public async Task Upcoming_bookings_are_reported_first_and_the_barber_stays_active()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            var conflicts = payload.GetProperty("conflicts").EnumerateArray().ToList();
            Assert.Single(conflicts);
            Assert.Equal(bookingId, conflicts[0].GetProperty("id").GetInt32());
            Assert.True(conflicts[0].GetProperty("willBeEmailed").GetBoolean());

            using var assertDb = NewDb();
            // Nothing happened yet - the admin can still back out.
            Assert.True((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);
            Assert.Equal(BookingStatus.COMPLETED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Confirming_deactivates_the_barber_revokes_their_session_and_cancels_the_booking()
        {
            AuthenticateAsAdmin();
            int barberId, barberUserId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                barberUserId = barber.UserId;
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirmCancelBookings=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);

            // TokenVersion bump locks the deactivated barber out of their dashboard immediately, rather
            // than leaving their still-valid JWT working until it expires.
            Assert.Equal(1, (await assertDb.Users.SingleAsync(u => u.Id == barberUserId)).TokenVersion);

            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking2.Status);
            Assert.Equal(CancellationReason.BarberUnavailable, booking2.CancellationReason);
            Assert.False(booking2.NeedsReview);

            // The barber-unavailable wording, not the closure or generic one.
            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendBookingCancelledBarberUnavailableEmailAsync" && j.BookingId == bookingId);
        }

        [Fact]
        public async Task A_pending_booking_is_cancelled_as_barber_unavailable_without_an_email()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.PENDING).Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirmCancelBookings=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.BarberUnavailable, booking.CancellationReason);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Past_bookings_are_left_as_history()
        {
            AuthenticateAsAdmin();
            int barberId, pastBookingId, futureBookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                pastBookingId = db.AddBooking(barber.Id, TestData.FutureAt(-3, 16), BookingStatus.COMPLETED).Id;
                futureBookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirmCancelBookings=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            // Asserting both in one run: the future one proves the cancellation ran at all, so the past
            // one being untouched is a real exclusion rather than a no-op.
            Assert.Equal(BookingStatus.CANCELLED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == futureBookingId)).Status);
            var past = await assertDb.Bookings.SingleAsync(b => b.Id == pastBookingId);
            Assert.Equal(BookingStatus.COMPLETED, past.Status);
            Assert.Equal(CancellationReason.None, past.CancellationReason);
        }

        [Fact]
        public async Task An_already_inactive_barber_cannot_be_deactivated_again()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
                barberId = db.AddBarber(isActive: false).Id;

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirmCancelBookings=true");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("already inactive", (await ReadJson(response)).GetProperty("message").GetString());
        }

        [Fact]
        public async Task Deactivating_a_barber_with_no_upcoming_bookings_needs_no_confirmation()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);
        }
    }
}
