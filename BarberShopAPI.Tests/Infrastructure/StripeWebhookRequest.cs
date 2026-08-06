using System.Security.Cryptography;
using System.Text;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* Builds genuinely signed Stripe webhook requests.
     *
     * WebHookController verifies every delivery with EventUtility.ConstructEvent against
     * STRIPE_WEBHOOK_SECRET, so a test can't just POST JSON at it - the signature check would reject it
     * before any of the branches under test ran. Signing here with the same secret ApiFactory puts in the
     * environment means the controller's verification runs for real and the tests go through the front
     * door, exactly as Stripe does.
     *
     * api_version is taken from StripeConfiguration.ApiVersion rather than hard-coded. Stripe.net's event
     * deserializer needs it present, and ConstructEvent throws on a version MISMATCH by default (the
     * production call doesn't opt out) - so reading the value out of the library is the only form that
     * survives a Stripe.net upgrade. */
    public static class StripeWebhookRequest
        /* This one answers the question "What happens when Stripe calls my application" unlike
         * FakeStripe which answers "What happens when my application calls stripe"*/
    {
        public const string Secret = "whsec_test_secret_for_integration_tests";

        public static HttpRequestMessage PaymentIntentSucceeded(
            int bookingId,
            string paymentIntentId,
            long amountReceived = 2500,
            string fullName = "Card Customer",
            string phone = "+35679555002",
            string email = "card.customer@example.test",
            string eventType = "payment_intent.succeeded")
        {
            /* Shaped like a genuine Stripe delivery, not a minimal stub: Stripe.net's EventConverter reads
             * several of these envelope fields unconditionally and throws a NullReferenceException if they
             * are absent, so a trimmed-down payload fails to parse before any controller logic runs. */
            var json = $@"{{
                ""id"": ""evt_test_{Guid.NewGuid():N}"",
                ""object"": ""event"",
                ""api_version"": ""{Stripe.StripeConfiguration.ApiVersion}"",
                ""created"": {DateTimeOffset.UtcNow.ToUnixTimeSeconds()},
                ""livemode"": false,
                ""pending_webhooks"": 1,
                ""request"": {{ ""id"": null, ""idempotency_key"": null }},
                ""type"": ""{eventType}"",
                ""data"": {{
                    ""object"": {{
                        ""id"": ""{paymentIntentId}"",
                        ""object"": ""payment_intent"",
                        ""amount"": {amountReceived},
                        ""amount_received"": {amountReceived},
                        ""currency"": ""eur"",
                        ""status"": ""succeeded"",
                        ""metadata"": {{
                            ""BookingId"": ""{bookingId}"",
                            ""FullName"": ""{fullName}"",
                            ""Phone"": ""{phone}"",
                            ""Email"": ""{email}""
                        }}
                    }}
                }}
            }}";

            return Signed(json);
        }

        /* A refund reporting its outcome. Defaults to the one that matters - a refund Stripe accepted and
         * then FAILED, which arrives hours after the refund call already returned "succeeded" and is the
         * only notice the shop ever gets that the money never left.
         *
         * The event type defaults to the legacy `charge.refund.updated` because that is what this account
         * actually sends; `refund.failed` is the newer name for the same delivery. The controller dispatches
         * on the Refund object rather than the name, so both land in the same branch - which is what the
         * eventType parameter exists to prove. */
        public static HttpRequestMessage RefundOutcome(
            string paymentIntentId,
            string status = "failed",
            long amount = 2500,
            string refundId = "re_test_refund",
            string? failureReason = "expired_or_canceled_card",
            string eventType = "charge.refund.updated")
        {
            var json = $@"{{
                ""id"": ""evt_test_{Guid.NewGuid():N}"",
                ""object"": ""event"",
                ""api_version"": ""{Stripe.StripeConfiguration.ApiVersion}"",
                ""created"": {DateTimeOffset.UtcNow.ToUnixTimeSeconds()},
                ""livemode"": false,
                ""pending_webhooks"": 1,
                ""request"": {{ ""id"": null, ""idempotency_key"": null }},
                ""type"": ""{eventType}"",
                ""data"": {{
                    ""object"": {{
                        ""id"": ""{refundId}"",
                        ""object"": ""refund"",
                        ""amount"": {amount},
                        ""currency"": ""eur"",
                        ""payment_intent"": ""{paymentIntentId}"",
                        ""status"": ""{status}"",
                        ""failure_reason"": {(failureReason == null ? "null" : $"\"{failureReason}\"")}
                    }}
                }}
            }}";

            return Signed(json);
        }

        /// <summary>Signs a payload the way Stripe does: HMAC-SHA256 over "{timestamp}.{payload}".</summary>
        public static HttpRequestMessage Signed(string json)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            var signature = Convert.ToHexString(/* Stripe signs webhook requests using HMAC SHA-256 */
                hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{json}"))).ToLowerInvariant();

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhook")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");
            return request;
        }
    }
}
