using BarberShopAPI.Data;
using BarberShopAPI.Models.Enums;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Stripe;

namespace BarberShopAPI.CronJob
{
    public class BookingExpiryJob
    {
        private readonly BarberShopContext _context;

        public BookingExpiryJob(BarberShopContext context)
        {
            _context = context;
        }

        public async Task CancelExpiredBookingsAsync()
        {
            var expiredBookings = await _context.Bookings
                .Where(b => b.Status == BookingStatus.PENDING &&
                            b.CreatedAt <= DateTime.UtcNow.AddMinutes(-15))
                .ToListAsync();

            if (!expiredBookings.Any()) return;

            /* Why this exists: without it, a customer paying by card could have their card
             * successfully charged by Stripe *after* we've already given up on their booking
             * and freed the slot back up. The webhook that records the payment runs completely
             * independently of this cron job - there's no way for it to know a booking is about
             * to expire, and no way for this job to know a payment is about to succeed. Cancelling
             * the PaymentIntent here is what actually closes that gap: it forces one of these two
             * outcomes, never a middle ground where the booking is dead but the money moved anyway.
             */
            var paymentIntentService = new PaymentIntentService();
            int cancelledCount = 0;

            foreach (var booking in expiredBookings)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(booking.StripePaymentIntentId))
                    {
                        try
                        {
                            // Cancel succeeded: Stripe guarantees this PaymentIntent can never succeed
                            // from here on, so the card was never (and now can never be) charged for
                            // this booking. Safe to cancel the booking below - no money is at risk.
                            await paymentIntentService.CancelAsync(booking.StripePaymentIntentId);
                        }
                        catch (StripeException ex)
                        {
                            // Cancel failed: almost always because the PaymentIntent already reached
                            // "succeeded" (or is mid-processing and Stripe won't let us cancel it out
                            // from under that). The card has already been - or is about to be - charged,
                            // so we deliberately do NOT cancel this booking. It's left PENDING so the
                            // webhook's normal payment_intent.succeeded flow completes it as if nothing
                            // happened. If that webhook never arrives, this booking will show up again
                            // on the next run and we'll retry the cancel then.
                            Console.WriteLine($"Skipping expiry for booking {booking.Id}: could not cancel PaymentIntent {booking.StripePaymentIntentId} ({ex.Message})");
                            continue;
                        }
                    }

                    booking.Status = BookingStatus.CANCELLED;
                    cancelledCount++;
                }
                catch (Exception ex)
                {
                    // Anything unexpected here (network blip, bug, etc.) - don't let one bad
                    // booking take down the whole batch. SaveChangesAsync runs once after this
                    // loop, so if this propagated out uncancelled, every booking already processed
                    // in this run would be lost too. Skip this one, it'll be retried next run.
                    Console.WriteLine($"Unexpected error expiring booking {booking.Id}, will retry next run: {ex.Message}");
                }
            }

            await _context.SaveChangesAsync();
            Console.WriteLine($"Cancelled {cancelledCount} expired bookings");
        }
    }
}