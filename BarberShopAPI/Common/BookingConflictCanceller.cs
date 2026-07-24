using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using Stripe;

namespace BarberShopAPI.Common
{
    /* Cancels the bookings a shop-side event just invalidated - a closure landing on the slot, or the
     * assigned barber being deactivated. Extracted from the closure flow so barber-deactivation reuses the
     * exact same handling instead of duplicating it.
     *
     * COMPLETED (paid) bookings go through BookingCanceller (refund + reason-specific email). PENDING
     * bookings differ: their payment is still in flight, so we void the in-flight PaymentIntent rather than
     * refund; if Stripe won't let us (payment already succeeding) we leave it PENDING and rely on the
     * webhook's closure guard to refund + cancel when the payment lands. Failures are logged/flagged, never
     * thrown, so one bad booking doesn't undo the closure/deactivation or block the others. `reason` is
     * stamped on each cancelled booking and drives the customer-facing wording (see CancellationReason). */
    public static class BookingConflictCanceller
    {
        public static async Task CancelConflictingBookingsAsync(BarberShopContext context, List<Booking> conflicts, CancellationReason reason)
        {
            foreach (var booking in conflicts)
            {
                if (booking.Status == BookingStatus.PENDING)
                {
                    if (!string.IsNullOrWhiteSpace(booking.StripePaymentIntentId))
                    {
                        try
                        {
                            await new PaymentIntentService().CancelAsync(booking.StripePaymentIntentId);
                        }
                        catch (StripeException ex)
                        {
                            Console.WriteLine($"Booking {booking.Id}: couldn't cancel PaymentIntent for a pending {reason} conflict, leaving it for the webhook guard: {ex.Message}");
                            continue;
                        }
                    }
                    // No reminder job to delete: those are only scheduled at confirmation (COMPLETED),
                    // so a PENDING booking never has one. No email: a pending booking never got a
                    // confirmation, so there's nothing to walk back - the customer recovers via the
                    // cancelled screen if they're still at checkout.
                    booking.Status = BookingStatus.CANCELLED;
                    booking.CancellationReason = reason;
                    await context.SaveChangesAsync();
                }
                else
                {
                    // Shop-side cancellations refund in full regardless of the 24h customer penalty:
                    // dueToClosure covers closures, forceRefund covers barber-unavailable. The reason also
                    // selects which email BookingCanceller enqueues.
                    var dueToClosure = reason == CancellationReason.ShopClosure;
                    var outcome = await BookingCanceller.CancelAsync(context, booking.Id, dueToClosure: dueToClosure, forceRefund: !dueToClosure, reason: reason);
                    if (outcome != BookingCanceller.Outcome.Cancelled)
                    {
                        // Nothing here will retry and there's no interactive admin to re-click, so this
                        // booking would otherwise be silently stuck: the barber's gone / slot's closed but
                        // the customer wasn't refunded/cancelled. Flag it for the review worklist.
                        Console.WriteLine($"Booking {booking.Id}: {reason} cancellation returned {outcome}, needs manual follow-up.");
                        booking.NeedsReview = true;
                        booking.ReviewReason = $"{reason} cancellation returned {outcome} - check Stripe for a charge on this booking and refund/reconcile by hand.";
                        await context.SaveChangesAsync();
                    }
                }
            }
        }
    }
}
