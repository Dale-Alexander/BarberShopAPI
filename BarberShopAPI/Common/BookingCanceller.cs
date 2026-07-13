using BarberShopAPI.Data;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace BarberShopAPI.Common
{
    /* Shared cancellation path for an already-confirmed (COMPLETED) booking, used by both the admin
     * CancelBooking endpoint and the shop-closure flow so the refund/cancel/email logic lives in one
     * place. It refunds a completed card payment before cancelling - never the other way round - so we
     * can't end up having cancelled a booking while still holding the customer's money. Callers map the
     * returned Outcome to whatever response makes sense for them.
     *
     * PENDING bookings are intentionally NOT handled here (returns Pending): their payment is still in
     * flight, so they need the PaymentIntent-cancel handling the closure flow / expiry job do, not a refund. */
    public static class BookingCanceller
    {
        public enum Outcome { Cancelled, CancelledNoRefund, NotFound, AlreadyCancelled, Pending, RefundFailed }

        /* Cancel within this window of the appointment and the customer forfeits their refund (the barber
         * has too little time to rebook the slot). Cancelling earlier gets a full refund. This is a customer
         * penalty only: it never applies when the shop cancels (dueToClosure) or when staff choose to refund
         * anyway (forceRefund) - e.g. goodwill, or an ad-hoc shop-side cancel like a barber calling in sick. */
        private static readonly TimeSpan RefundCutoff = TimeSpan.FromHours(24);

        public static async Task<Outcome> CancelAsync(BarberShopContext context, int bookingId, bool dueToClosure, bool forceRefund = false)
        {
            var booking = await context.Bookings
                .Include(b => b.Payment)
                .FirstOrDefaultAsync(b => b.Id == bookingId);

            if (booking == null) return Outcome.NotFound;
            if (booking.Status == BookingStatus.CANCELLED) return Outcome.AlreadyCancelled;
            if (booking.Status == BookingStatus.PENDING) return Outcome.Pending;

            /* StartDateTime is Malta wall-clock and so is ShopClock.Now, so this compares like with like. */
            bool refundAllowed = dueToClosure || forceRefund || (booking.StartDateTime - ShopClock.Now) >= RefundCutoff;
            bool refundIssued = false;

            var payment = booking.Payment;
            /* Only a card/online payment that actually completed has money with us to give back.
             * Cash payments (no StripePaymentIntentId) are settled in person - nothing to refund. */
            bool hadRefundablePayment = payment != null
                && payment.Status == PaymentStatus.COMPLETED
                && !string.IsNullOrWhiteSpace(payment.StripePaymentIntentId);

            if (refundAllowed && hadRefundablePayment)
            {
                try
                {
                    var refundService = new RefundService();
                    /* Idempotency key keyed to the PaymentIntent being refunded: if this call has to be
                     * retried (e.g. the SaveChanges below failed last time and the admin re-clicks cancel),
                     * Stripe replays the original refund as a success instead of refunding a second time.
                     * That lets the retry get past this line and finish the cancellation it couldn't complete. */
                    await refundService.CreateAsync(new RefundCreateOptions
                    {
                        PaymentIntent = payment.StripePaymentIntentId
                    }, new RequestOptions { IdempotencyKey = $"refund-{payment.StripePaymentIntentId}" });
                    payment.Status = PaymentStatus.REFUNDED;
                    refundIssued = true;
                }
                catch (StripeException ex) when (ex.StripeError?.Code == "charge_already_refunded")
                {
                    /* The money is already back with the customer - e.g. a refund from a previous attempt
                     * whose DB write failed, or an idempotency key that expired (Stripe only keeps them 24h).
                     * There's nothing left to refund, so treat this as success and carry on with the
                     * cancellation rather than getting stuck re-refunding something already refunded. */
                    Console.WriteLine($"Booking {bookingId}: charge already refunded on Stripe, proceeding with cancellation.");
                    payment.Status = PaymentStatus.REFUNDED;
                    refundIssued = true;
                }
                catch (StripeException ex)
                {
                    /* Don't cancel while the money is still with us - surface it so the caller can retry
                     * / follow up manually rather than silently keeping the customer's payment. */
                    Console.WriteLine($"Refund failed for booking {bookingId}, not cancelling: {ex.Message}");
                    return Outcome.RefundFailed;
                }
            }

            if (!string.IsNullOrWhiteSpace(booking.ReminderJobId))
            {
                BackgroundJob.Delete(booking.ReminderJobId);
                booking.ReminderJobId = null;
            }
            booking.Status = BookingStatus.CANCELLED;
            await context.SaveChangesAsync();

            if (dueToClosure)
                BackgroundJob.Enqueue<IEmailService>(s => s.sendBookingCancelledDueToClosureEmailAsync(bookingId));
            else
                BackgroundJob.Enqueue<IEmailService>(s => s.sendBookingCancellationEmailAsync(bookingId, refundIssued));

            /* CancelledNoRefund only when the 24h policy actually withheld a refund that would otherwise have
             * been due (a paid card booking). Cash / unpaid bookings have nothing to refund, so they're a
             * plain Cancelled - not a policy penalty the UI needs to warn about. */
            return (!refundAllowed && hadRefundablePayment) ? Outcome.CancelledNoRefund : Outcome.Cancelled;
        }
    }
}
