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
     * The branch under test is CancelAndReject: a booking can sit PENDING while an admin creates a closure,
     * edits the barber's hours, or deactivates the barber outright, so confirmation re-checks ALL THREE
     * before promoting PENDING -> COMPLETED. It cancels rather than merely 400-ing, because that's what lets
     * the frontend's "no longer pending" redirect land on a real cancelled screen. No money has moved on the
     * cash path, so there is no refund and no email - the customer is present and sees the rejection
     * synchronously.
     *
     * The last section is the exception to that: a customer who started a card payment and then switched to
     * cash leaves a live PaymentIntent behind, so the request does reach Stripe to void it - on the way to a
     * confirmation AND on the way to a rejection, since a cancelled booking must not leave a chargeable
     * intent behind either. Those use FakeStripe. Everything above them is pure DB. */
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
        public async Task A_barber_deactivated_while_the_booking_was_pending_cancels_and_rejects_it()
        {
            // Deactivation normally sweeps the barber's pending bookings away, but BookingConflictCanceller
            // leaves one behind when its PaymentIntent can't be voided, and a booking created in the instant
            // before isActive flipped is never swept at all. This is that leftover: no closure, and the
            // schedule still fits (deactivation doesn't touch it), so only the isActive check can catch it.
            var (publicId, bookingId, barberId) = ArrangePendingBooking();
            using (var db = NewDb())
            {
                db.Barbers.Single(b => b.Id == barberId).isActive = false;
                db.SaveChanges();
            }

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("no longer available", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            // Drives the cancelled screen's "book with another barber" copy - not "we were closed".
            Assert.Equal(CancellationReason.BarberUnavailable, booking.CancellationReason);

            Assert.Empty(await assertDb.Payments.Where(p => p.BookingId == bookingId).ToListAsync());
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_deactivated_barber_outranks_a_closure_as_the_recorded_reason()
        {
            var (publicId, bookingId, barberId) = ArrangePendingBooking(shiftEnd: new TimeOnly(12, 0));
            using (var db = NewDb())
            {
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: barberId);
                db.Barbers.Single(b => b.Id == barberId).isActive = false;
                db.SaveChanges();
            }

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using var assertDb = NewDb();
            // All three reasons apply at once. BarberUnavailable wins because it's the only one whose
            // recovery is "rebook with someone else"; telling them the shop was shut would send them
            // straight back to a barber who has gone. The webhook resolves it the same way.
            Assert.Equal(CancellationReason.BarberUnavailable,
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

        // ---------------------------------------------------------------------------------------------
        // Abandoned card attempt - the customer clicked Pay Online, then switched to cash
        // ---------------------------------------------------------------------------------------------

        private const string AbandonedIntentId = "pi_test_abandoned_card_attempt";

        /// <summary>A pending booking carrying the live PaymentIntent an unfinished card attempt left behind.</summary>
        private string ArrangePendingBookingWithCardAttempt(out int bookingId)
        {
            var (publicId, id, _) = ArrangePendingBooking();
            using var db = NewDb();
            db.Bookings.Single(b => b.Id == id).StripePaymentIntentId = AbandonedIntentId;
            db.SaveChanges();
            bookingId = id;
            return publicId;
        }

        [Fact]
        public async Task An_abandoned_card_attempt_is_voided_when_the_customer_switches_to_cash()
        {
            var publicId = ArrangePendingBookingWithCardAttempt(out var bookingId);
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Succeeds };

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Switching to cash does NOT void the intent by itself. If we complete as cash and leave it
            // live, a slow 3-D Secure could still succeed afterwards and charge a customer who has already
            // paid in person - so the void has to actually reach Stripe, not merely be intended.
            Assert.Contains($"POST /v1/payment_intents/{AbandonedIntentId}/cancel", stripe.Requests);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.Equal(PaymentMethod.CASH,
                (await assertDb.Payments.SingleAsync(p => p.BookingId == bookingId)).Method);
        }

        [Fact]
        public async Task A_card_attempt_that_cannot_be_voided_still_lets_the_cash_confirmation_through()
        {
            var publicId = ArrangePendingBookingWithCardAttempt(out var bookingId);
            // What Stripe returns when the intent is already succeeding and can no longer be cancelled.
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Fails };

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            // The customer is standing at the counter: a Stripe refusal must not fail their confirmation.
            // Letting the exception escape would hit the outer catch, roll the transaction back and 500 them
            // over a charge the webhook's orphaned-charge guard is already designed to refund.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains($"POST /v1/payment_intents/{AbandonedIntentId}/cancel", stripe.Requests);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.Equal(CancellationReason.None, booking.CancellationReason);

            var payment = await assertDb.Payments.SingleAsync(p => p.BookingId == bookingId);
            Assert.Equal(PaymentMethod.CASH, payment.Method);
            Assert.Equal(25m, payment.Amount);

            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendBookingConfirmationEmailAsync" && j.BookingId == bookingId);
        }

        [Fact]
        public async Task A_rejected_booking_voids_the_abandoned_card_attempt_too()
        {
            /* The reject paths kill the booking just as surely as the success path completes it, so they owe
             * the customer the same void. Without it the abandoned attempt can still succeed against a
             * booking we've already cancelled, and the money only comes back days later through the
             * webhook's orphaned-charge refund - a round trip that never needed to happen. */
            var publicId = ArrangePendingBookingWithCardAttempt(out var bookingId);
            using (var db = NewDb())
            {
                var barberId = db.Bookings.Single(b => b.Id == bookingId).BarberId;
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: barberId);
            }
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Succeeds };

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"POST /v1/payment_intents/{AbandonedIntentId}/cancel", stripe.Requests);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking.CancellationReason);
            // Voiding is not charging: there is still no payment row and nothing to email about.
            Assert.Empty(await assertDb.Payments.Where(p => p.BookingId == bookingId).ToListAsync());
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_rejection_still_goes_through_when_the_card_attempt_cannot_be_voided()
        {
            // Stripe refuses because the intent is already succeeding. The customer is at the counter being
            // told their slot is gone, so that refusal must not turn into a 500 - the webhook's
            // orphaned-charge guard refunds the charge when it lands, which is exactly the fallback.
            var publicId = ArrangePendingBookingWithCardAttempt(out var bookingId);
            using (var db = NewDb())
            {
                var barberId = db.Bookings.Single(b => b.Id == bookingId).BarberId;
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: barberId);
            }
            using var stripe = new FakeStripe { PaymentIntentCancels = FakeStripe.CancelOutcome.Fails };

            var response = await Client.PostAsync("/api/bookings/confirm-cash", Body(CashBody(publicId)));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("shop closure", (await ReadJson(response)).GetProperty("message").GetString());
            Assert.Contains($"POST /v1/payment_intents/{AbandonedIntentId}/cancel", stripe.Requests);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // The cancellation must still be committed - leaving it PENDING would strand the customer on a
            // checkout screen for a slot that no longer exists.
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking.CancellationReason);
        }
    }
}
