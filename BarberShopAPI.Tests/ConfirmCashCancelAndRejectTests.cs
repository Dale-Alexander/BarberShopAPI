using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* POST /api/bookings/confirm-cash - the guest cash-confirmation path (no auth: the customer is holding
     * only their booking's public slug).
     *
     * The branch under test is CancelAndReject: a booking can sit PENDING while an admin creates a closure
     * or edits the barber's hours, so confirmation re-checks BOTH before promoting PENDING -> COMPLETED.
     * It cancels rather than merely 400-ing, because that's what lets the frontend's "no longer pending"
     * redirect land on a real cancelled screen. No money has moved on the cash path, so there is no refund
     * and no email - the customer is present and sees the rejection synchronously. */
    public class ConfirmCashCancelAndRejectTests : IntegrationTestBase
    {
        public ConfirmCashCancelAndRejectTests(DatabaseFixture fixture) : base(fixture) { }

        private static object CashBody(string publicId) => new
        {
            fullName = "Cash Customer",
            phone = "+35679555001",
            bookingId = publicId,
            email = "cash.customer@example.test"
        };

        /// <summary>A PENDING booking at 16:00 two weeks out, with the barber working 09:00-17:30 every day.</summary>
        private (string PublicId, int BookingId, int BarberId) ArrangePendingBooking(
            TimeOnly? shiftEnd = null, int bookingHour = 16)
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30),
                null,
                Enumerable.Range(0, 7)
                    .Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), shiftEnd ?? new TimeOnly(17, 30)))
                    .ToArray());
            var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, bookingHour), BookingStatus.PENDING);
            return (booking.PublicId, booking.Id, barber.Id);
        }

        [Fact]
        public async Task A_closure_created_while_the_booking_was_pending_cancels_and_rejects_it()
        {
            var (publicId, bookingId, barberId) = ArrangePendingBooking();
            using (var db = NewDb())
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: barberId);

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("shop closure", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking.CancellationReason);

            // Cash: nothing was ever charged, so no payment row is created and nothing is emailed.
            Assert.Empty(await assertDb.Payments.Where(p => p.BookingId == bookingId).ToListAsync());
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_schedule_change_that_strands_the_booking_cancels_and_rejects_it()
        {
            // Barber now finishes at 12:00, so the 16:00 slot no longer exists.
            var (publicId, bookingId, _) = ArrangePendingBooking(shiftEnd: new TimeOnly(12, 0));

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("working hours", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            // Distinct from ShopClosure so the cancelled screen doesn't tell the customer the shop was shut.
            Assert.Equal(CancellationReason.ScheduleChange, booking.CancellationReason);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task When_a_closure_and_a_schedule_change_both_apply_the_closure_is_the_recorded_reason()
        {
            var (publicId, bookingId, barberId) = ArrangePendingBooking(shiftEnd: new TimeOnly(12, 0));
            using (var db = NewDb())
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: barberId);

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using var assertDb = NewDb();
            // The closure check runs first and returns, so ShopClosure wins - matching the webhook, which
            // makes the same precedence explicit.
            Assert.Equal(CancellationReason.ShopClosure,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).CancellationReason);
        }

        [Fact]
        public async Task An_unobstructed_booking_confirms_normally()
        {
            var (publicId, bookingId, _) = ArrangePendingBooking();

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            // No cancellation happened, so the reason must stay untouched - the cancelled screen keys off it.
            Assert.Equal(CancellationReason.None, booking.CancellationReason);
            Assert.Equal("cash.customer@example.test", booking.ContactEmail);
            Assert.NotNull(booking.UserId);

            // Amount comes from the booking's own services, never the request body.
            var payment = await assertDb.Payments.SingleAsync(p => p.BookingId == bookingId);
            Assert.Equal(PaymentMethod.CASH, payment.Method);
            Assert.Equal(25m, payment.Amount);

            // A confirmed booking gets its confirmation email and a scheduled reminder.
            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendBookingConfirmationEmailAsync" && j.BookingId == bookingId);
            Assert.NotNull(booking.ReminderJobId);
        }

        [Fact]
        public async Task A_booking_that_is_no_longer_pending_is_rejected_without_being_touched()
        {
            using var arrange = NewDb();
            var barber = arrange.AddBarber();
            arrange.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            var booking = arrange.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(booking.PublicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Only Pending", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            var after = await assertDb.Bookings.SingleAsync(b => b.Id == booking.Id);
            // Critically NOT cancelled: the guard must not walk back an already-confirmed booking.
            Assert.Equal(BookingStatus.COMPLETED, after.Status);
            Assert.Equal(CancellationReason.None, after.CancellationReason);
        }
    }
}
