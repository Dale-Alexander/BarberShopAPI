using Stripe;

namespace BarberShopAPI.Common
{
    /* One place for "refund this PaymentIntent, and never double-refund if we're retried". The idempotency
     * key is keyed to the PaymentIntent, so a Stripe webhook retry (or an admin re-clicking cancel) replays
     * the original refund as a success instead of issuing a second one. Used by BookingCanceller (staff /
     * closure cancels of a completed booking) and the webhook (closure-after-payment, and the orphaned-charge
     * guard). Callers map the Outcome to whatever they need - record a REFUNDED payment, cancel a booking, etc. */
    public static class StripeRefunds
    {
        public enum Outcome { Refunded, AlreadyRefunded, Failed }

        public static async Task<Outcome> RefundIdempotentlyAsync(string paymentIntentId)
        {
            try
            {
                var refundService = new RefundService();
                await refundService.CreateAsync(
                    new RefundCreateOptions { PaymentIntent = paymentIntentId },
                    new RequestOptions { IdempotencyKey = $"refund-{paymentIntentId}" });
                return Outcome.Refunded;
            }
            catch (StripeException ex) when (ex.StripeError?.Code == "charge_already_refunded")
            {
                /* Money's already back with the customer - a previous attempt whose DB write failed, or the
                 * 24h idempotency window lapsed. Nothing left to refund, so treat it as success and let the
                 * caller carry on rather than looping on a refund that can't happen again. */
                Console.WriteLine($"PaymentIntent {paymentIntentId}: already refunded, treating as success.");
                return Outcome.AlreadyRefunded;
            }
            catch (StripeException ex)
            {
                Console.WriteLine($"PaymentIntent {paymentIntentId}: refund failed: {ex.Message}");
                return Outcome.Failed;
            }
        }
    }
}
