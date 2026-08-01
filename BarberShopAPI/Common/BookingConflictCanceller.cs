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

                    /* Nothing here will retry and there's no interactive admin to re-click, so a booking that
                     * didn't get cancelled would otherwise be silently stuck: the barber's gone / slot's closed
                     * but the customer wasn't refunded or cancelled. Only the outcomes that leave real work
                     * behind belong in the worklist though - flagging the rest sends staff hunting for money
                     * that was never at risk. */
                    switch (outcome)
                    {
                        // Refunded (or withheld by the 24h policy, which is customer-only and so can never
                        // apply to a shop-side cancel), cancelled, emailed. Nothing to do.
                        case BookingCanceller.Outcome.Cancelled:
                        case BookingCanceller.Outcome.CancelledNoRefund:
                            break;

                        /* Something else cancelled this booking in the same moment - two admins, or a closure
                         * and a deactivation landing together. That path already refunded, emailed and stamped
                         * its own reason, so a flag here is a false alarm. NotFound can't happen (nothing in
                         * the codebase deletes bookings) and writing to a row that isn't there would throw out
                         * of a helper that must never throw, aborting the rest of the sweep. */
                        case BookingCanceller.Outcome.AlreadyCancelled:
                        case BookingCanceller.Outcome.NotFound:
                            Console.WriteLine($"Booking {booking.Id}: {reason} cancellation returned {outcome} - nothing left to do.");
                            break;

                        /* The booking was confirmed while this sweep was running, so the snapshot we're working
                         * from still said PENDING when it had just been completed. The money isn't stuck - the
                         * appointment is: it's live on a slot that no longer exists and was never cancelled,
                         * which is a different job for staff than reconciling a charge. It may have been
                         * confirmed by card (the webhook) OR as cash (ConfirmCashBooking), and the cash one has
                         * nothing to give back, so the note must not promise a refund either way. */
                        case BookingCanceller.Outcome.Pending:
                            Console.WriteLine($"Booking {booking.Id}: confirmed mid-{reason} cancellation, still live.");
                            booking.FlagForReview($"This booking was confirmed while the {reason} cancellation was running, "
                                + "so it is still live on a slot that is no longer available - cancel it by hand, "
                                + "refunding the customer if they paid by card.");
                            await context.SaveChangesAsync();
                            break;

                        // RefundFailed, and any outcome added later: the money may still be with us, so flag it.
                        default:
                            Console.WriteLine($"Booking {booking.Id}: {reason} cancellation returned {outcome}, needs manual follow-up.");
                            booking.FlagForReview($"{reason} cancellation returned {outcome} - check Stripe for a charge on this booking and refund/reconcile by hand.");
                            await context.SaveChangesAsync();
                            break;
                    }
                }
            }
        }
    }
}
