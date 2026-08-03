using BarberShopAPI.Data;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

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

        /* shopInitiated: the shop caused this cancellation, so the customer-only 24h penalty must not apply
         * and they're owed the reason-specific notice. Separate from dueToClosure, which ALSO selects the
         * closure wording - an admin cancelling a booking whose barber has left is shop-initiated but must
         * not be told the shop was shut. */
        public static async Task<Outcome> CancelAsync(BarberShopContext context, int bookingId, bool dueToClosure, bool forceRefund = false, CancellationReason reason = CancellationReason.None, bool shopInitiated = false)
        {
            var booking = await context.Bookings
                .Include(b => b.Payment)
                .Include(b => b.User)   // for the phone number on the phone-only worklist note below
                .FirstOrDefaultAsync(b => b.Id == bookingId);

            if (booking == null) return Outcome.NotFound;
            if (booking.Status == BookingStatus.CANCELLED) return Outcome.AlreadyCancelled;
            if (booking.Status == BookingStatus.PENDING) return Outcome.Pending;

            /* Cancel within this window of the appointment and the customer forfeits their refund (the barber
             * has too little time to rebook the slot). Cancelling earlier gets a full refund. This is a customer
             * penalty only: it never applies when the shop cancels (dueToClosure) or when staff choose to refund
             * anyway (forceRefund) - e.g. goodwill, or an ad-hoc shop-side cancel like a barber calling in sick.
             * The cutoff comes from ShopSettings so the shop can tune it (mirrored by the staff booking tables). */
            var refundCutoff = TimeSpan.FromHours(await context.ShopSettings.Select(s => s.RefundCutoffHours).FirstAsync());

            /* StartDateTime is Malta wall-clock and so is ShopClock.Now, so this compares like with like. */
            bool refundAllowed = shopInitiated || forceRefund || (booking.StartDateTime - ShopClock.Now) >= refundCutoff;
            bool refundIssued = false;

            var payment = booking.Payment;
            /* Only a card/online payment that actually completed has money with us to give back.
             * Cash payments (no StripePaymentIntentId) are settled in person - nothing to refund. */
            bool hadRefundablePayment = payment != null
                && payment.Status == PaymentStatus.COMPLETED
                && !string.IsNullOrWhiteSpace(payment.StripePaymentIntentId);

            if (refundAllowed && hadRefundablePayment)
            {
                /* Shared idempotent refund: a retry (SaveChanges below failed last time and the admin re-clicks
                 * cancel) replays the original refund as a success instead of refunding twice. Refunded and
                 * AlreadyRefunded both mean the money is back, so we mark it REFUNDED and carry on. */
                var refundOutcome = await StripeRefunds.RefundIdempotentlyAsync(payment.StripePaymentIntentId);
                if (refundOutcome == StripeRefunds.Outcome.Failed)
                {
                    /* Don't cancel while the money is still with us - surface it so the caller can retry
                     * / follow up manually rather than silently keeping the customer's payment. */
                    return Outcome.RefundFailed;
                }
                payment.Status = PaymentStatus.REFUNDED;
                refundIssued = true;
            }

            if (!string.IsNullOrWhiteSpace(booking.ReminderJobId))
            {
                BackgroundJob.Delete(booking.ReminderJobId);
                booking.ReminderJobId = null;
                booking.ReminderSentAt = null;
            }
            booking.Status = BookingStatus.CANCELLED;
            booking.CancellationReason = reason;
            await context.SaveChangesAsync();

            /* Reason-specific wording where we have it, generic otherwise. BarberUnavailable tells the
             * customer their barber left (this is their only notice for a confirmed booking); the closure
             * email explains a shop closure; everything else is the plain cancellation notice. */

            /* A shop-side cancellation is our initiative - the customer did nothing and gets no warning
             * unless we reach them. Admin-created walk-ins carry only a phone number, so the email jobs
             * below would no-op on the blank ContactEmail and nobody would ever be told. The pre-confirm
             * modal does warn the admin, but only once and only before they click, so flag it for the
             * worklist instead: same list they already work, and it keeps the customer's number with it.
             * A staff cancel is deliberately excluded - an admin is at the screen and is usually cancelling
             * because that customer just phoned in. */
            if (shopInitiated && string.IsNullOrWhiteSpace(booking.ContactEmail))
            {
                booking.FlagForReview(
                    $"No email on file - call {(string.IsNullOrWhiteSpace(booking.User?.Phone) ? "the customer" : booking.User!.Phone)} "
                    + $"to tell them their {booking.StartDateTime:MMM d 'at' h:mm tt} appointment was cancelled"
                    + reason switch
                    {
                        CancellationReason.BarberUnavailable => " because their barber is no longer available.",
                        CancellationReason.ScheduleChange => " because their barber no longer works at that time.",
                        _ => " by a shop closure."
                    }
                    + (refundIssued ? " Their card payment has been refunded in full." : ""));
                await context.SaveChangesAsync();
            }
            /* Keyed off the reason, not dueToClosure: an admin cancelling a booking that has since fallen on
             * a closure passes the reason without that flag, and used to drop through to the generic notice. */
            else if (reason == CancellationReason.BarberUnavailable)
                BackgroundJob.Enqueue<IEmailService>(s => s.sendBookingCancelledBarberUnavailableEmailAsync(bookingId, refundIssued, null));
            else if (reason == CancellationReason.ShopClosure || dueToClosure)
                BackgroundJob.Enqueue<IEmailService>(s => s.sendBookingCancelledDueToClosureEmailAsync(bookingId, null));
            else if (reason == CancellationReason.ScheduleChange)
                BackgroundJob.Enqueue<IEmailService>(s => s.sendBookingCancelledDueToScheduleChangeEmailAsync(bookingId, null));
            else
                BackgroundJob.Enqueue<IEmailService>(s => s.sendBookingCancellationEmailAsync(bookingId, refundIssued));

            /* CancelledNoRefund only when the 24h policy actually withheld a refund that would otherwise have
             * been due (a paid card booking). Cash / unpaid bookings have nothing to refund, so they're a
             * plain Cancelled - not a policy penalty the UI needs to warn about. */
            return (!refundAllowed && hadRefundablePayment) ? Outcome.CancelledNoRefund : Outcome.Cancelled;
        }
    }
}
