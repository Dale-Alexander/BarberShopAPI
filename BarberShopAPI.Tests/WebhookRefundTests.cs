using BarberShopAPI.Common;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* Tier 2: the webhook's two NeedsReview sites and the refund-and-cancel gate around them.
     *
     * This is the point that actually resolves the race. A customer can be mid-payment when an admin closes
     * the slot or edits the barber's hours; the card gets charged regardless, and this is where the money
     * and the booking are reconciled. Two things can go wrong, and both are covered here:
     *
     *   - the charge is orphaned (the booking is no longer PENDING - expired, cancelled, or already paid
     *     in cash), so it must be refunded rather than left as a charge with no appointment behind it;
     *   - the slot closed or fell outside the schedule after payment, so the booking must be refunded and
     *     cancelled instead of confirmed.
     *
     * If the refund fails in either case the response is deliberately non-2xx, so Stripe RETRIES the event
     * - the idempotency key makes a later attempt replay safely - and the booking is flagged meanwhile so
     * it surfaces in the worklist if Stripe eventually gives up. Requests are properly signed, so the
     * controller's real signature verification runs. */
    public class WebhookRefundTests : IntegrationTestBase
    {
        public WebhookRefundTests(DatabaseFixture fixture) : base(fixture) { }

        private const string PaymentIntentId = "pi_test_webhook_flow";

        private int ArrangeBooking(BookingStatus status, int daysAhead = 14, bool withSchedule = true,
            TimeOnly? shiftEnd = null, string? contactEmail = "customer@example.test", bool withCustomer = true)
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            if (withSchedule)
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), null,
                    Enumerable.Range(0, 7)
                        .Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), shiftEnd ?? new TimeOnly(17, 30)))
                        .ToArray());

            var booking = db.AddBooking(barber.Id, TestData.FutureAt(daysAhead, 16), status,
                customer: withCustomer ? null : db.AddUser(), contactEmail: contactEmail);
            if (!withCustomer)
            {
                // A booking cancelled while still PENDING never had contact details attached.
                booking.UserId = null;
                booking.ContactEmail = null;
                db.SaveChanges();
            }
            booking.StripePaymentIntentId = PaymentIntentId;
            db.SaveChanges();
            return booking.Id;
        }

        // ---------------------------------------------------------------------------------------------
        // Orphaned charge - the booking is no longer PENDING
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task An_orphaned_charge_is_refunded_recorded_and_the_customer_told()
        {
            // No user and no email on file - the shape a booking really has when it was cancelled while
            // still PENDING, which is what makes the metadata fallback below matter.
            var bookingId = ArrangeBooking(BookingStatus.CANCELLED, withCustomer: false);
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            var response = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, stripe.RefundAttempts);

            using var assertDb = NewDb();
            // A financial trail for money that came in and went straight back out.
            var payment = await assertDb.Payments.SingleAsync(p => p.BookingId == bookingId);
            Assert.Equal(PaymentStatus.REFUNDED, payment.Status);
            Assert.Equal(PaymentMethod.CARD, payment.Method);
            Assert.Equal(25.00m, payment.Amount);

            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.False(booking.NeedsReview);
            // Contact details come off the event metadata, because a booking cancelled while PENDING never
            // had a user attached - without this the refund email would have nowhere to go.
            Assert.Equal("card.customer@example.test", booking.ContactEmail);
            Assert.NotNull(booking.UserId);

            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendPaymentRefundedUnconfirmedEmailAsync" && j.BookingId == bookingId);
        }

        [Fact]
        public async Task An_orphaned_charge_whose_refund_fails_is_flagged_and_asks_stripe_to_retry()
        {
            var bookingId = ArrangeBooking(BookingStatus.CANCELLED);
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            // Non-2xx on purpose: Stripe keeps retrying, and the idempotent refund may succeed later.
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // Flagged BEFORE the non-2xx, so if Stripe gives up after ~3 days the charge is still visible
            // to staff rather than silently kept.
            Assert.True(booking.NeedsReview);
            Assert.Contains("orphaned charge", booking.ReviewReason);
            Assert.Contains(PaymentIntentId, booking.ReviewReason);

            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        // ---------------------------------------------------------------------------------------------
        // Slot closed or rescheduled after payment
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_closure_landing_after_payment_refunds_and_cancels_with_the_closure_reason()
        {
            var bookingId = ArrangeBooking(BookingStatus.PENDING);
            using (var db = NewDb())
            {
                var booking = db.Bookings.Single(b => b.Id == bookingId);
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: booking.BarberId);
            }
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            var response = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // Cancelled rather than confirmed: PENDING -> COMPLETED must never happen on a closed slot.
            Assert.Equal(BookingStatus.CANCELLED, booking2.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking2.CancellationReason);
            Assert.False(booking2.NeedsReview);

            Assert.Equal(PaymentStatus.REFUNDED, (await assertDb.Payments.SingleAsync(p => p.BookingId == bookingId)).Status);
            Assert.Contains(Factory.EnqueuedEmailJobs(),
                j => j.Method == "sendBookingCancelledDueToClosureEmailAsync" && j.BookingId == bookingId);
        }

        [Fact]
        public async Task A_schedule_change_after_payment_uses_the_schedule_reason_and_its_own_email()
        {
            // Barber now finishes at 12:00, so the 16:00 slot the customer just paid for is gone.
            var bookingId = ArrangeBooking(BookingStatus.PENDING, shiftEnd: new TimeOnly(12, 0));
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            var response = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            // Not ShopClosure - the customer must not be told the shop was shut when it wasn't.
            Assert.Equal(CancellationReason.ScheduleChange, booking.CancellationReason);

            var emails = Factory.EnqueuedEmailJobs();
            Assert.Contains(emails, j => j.Method == "sendBookingCancelledDueToScheduleChangeEmailAsync" && j.BookingId == bookingId);
            Assert.DoesNotContain(emails, j => j.Method == "sendBookingCancelledDueToClosureEmailAsync");
        }

        [Fact]
        public async Task A_closure_takes_precedence_over_a_schedule_change_when_both_apply()
        {
            var bookingId = ArrangeBooking(BookingStatus.PENDING, shiftEnd: new TimeOnly(12, 0));
            using (var db = NewDb())
            {
                var booking = db.Bookings.Single(b => b.Id == bookingId);
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: booking.BarberId);
            }
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            var response = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            // Matches ConfirmCashBooking's precedence, so the two paths tell the customer the same story.
            Assert.Equal(CancellationReason.ShopClosure,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).CancellationReason);
        }

        [Fact]
        public async Task A_closure_whose_refund_fails_is_flagged_and_left_pending_for_a_retry()
        {
            var bookingId = ArrangeBooking(BookingStatus.PENDING);
            using (var db = NewDb())
            {
                var booking = db.Bookings.Single(b => b.Id == bookingId);
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: booking.BarberId);
            }
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // Still PENDING - the refund comes first, so a failure leaves the booking alone rather than
            // cancelling it while the money is still with us.
            Assert.Equal(BookingStatus.PENDING, booking2.Status);
            Assert.True(booking2.NeedsReview);
            Assert.Contains("Slot was closed after payment", booking2.ReviewReason);
            Assert.Empty(await assertDb.Payments.Where(p => p.BookingId == bookingId).ToListAsync());
        }

        [Fact]
        public async Task A_retry_after_a_failed_refund_can_still_complete_the_cancellation()
        {
            var bookingId = ArrangeBooking(BookingStatus.PENDING);
            using (var db = NewDb())
            {
                var booking = db.Bookings.Single(b => b.Id == bookingId);
                db.AddClosure(ShopClock.Today.AddDays(14), barberId: booking.BarberId);
            }

            using (var failing = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails })
            {
                var first = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));
                Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
            }

            // Stripe redelivers the same event once the refund can go through. The whole point of the
            // idempotency key is that this second attempt finishes the work the first couldn't.
            using var succeeding = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };
            var second = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking2.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking2.CancellationReason);
            // The flag the failed attempt set is cleared atomically with the cancellation it was standing in for.
            Assert.False(booking2.NeedsReview);
            Assert.Null(booking2.ReviewReason);
        }

        // ---------------------------------------------------------------------------------------------
        // Guards
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_redelivered_event_for_an_already_recorded_payment_is_ignored()
        {
            var bookingId = ArrangeBooking(BookingStatus.COMPLETED);
            using (var db = NewDb())
            {
                db.Payments.Add(new Payment
                {
                    BookingId = bookingId,
                    Amount = 25m,
                    Method = PaymentMethod.CARD,
                    Status = PaymentStatus.COMPLETED,
                    StripePaymentIntentId = PaymentIntentId,
                    PaidAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Fails };

            var response = await Client.SendAsync(StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // Short-circuits before doing anything - a Stripe retry after a successful delivery must not
            // refund a second time or duplicate the payment row.
            Assert.Equal(0, stripe.RefundAttempts);

            using var assertDb = NewDb();
            Assert.Single(await assertDb.Payments.Where(p => p.BookingId == bookingId).ToListAsync());
        }

        [Fact]
        public async Task An_unsigned_request_is_rejected_before_any_processing()
        {
            var bookingId = ArrangeBooking(BookingStatus.CANCELLED);
            using var stripe = new FakeStripe { Refunds = FakeStripe.RefundOutcome.Succeeds };

            var request = StripeWebhookRequest.PaymentIntentSucceeded(bookingId, PaymentIntentId);
            request.Headers.Remove("Stripe-Signature");
            request.Headers.Add("Stripe-Signature", "t=1,v1=forged");

            var response = await Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, stripe.RefundAttempts);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }
    }
}
