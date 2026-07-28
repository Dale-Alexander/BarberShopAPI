using Stripe;
using System.Net;
using System.Text;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* Substitutes Stripe at the HTTP boundary so refund and PaymentIntent failures can be driven on demand.
     *
     * The production code calls `new RefundService()` / `new PaymentIntentService()` with no arguments,
     * which resolves StripeConfiguration.StripeClient. Replacing that client means the REAL StripeRefunds
     * and the REAL controller code run - including StripeRefunds' charge_already_refunded special case,
     * which a hand-rolled seam around it would have skipped straight past.
     *
     * Faking at HTTP rather than behind a new interface is also why tier 2 needed no production changes:
     * there is nothing to inject, and no seam to keep in step with the code it wraps. */
    public sealed class FakeStripe : IDisposable
        /* This is a fake Stripe Server ised for testing. Its purpose is to let you
         * your tests simulate Stripe behaviour without actually connecting Stripe. the important idea is that 
         production code still thinks it is talking to Stripe. The test secretly redirects those 
        Stripe HTTP calls to this fake.
        Sealed means nobody can inherit from this class. IDisposable means when the test is finished, clean up after yourself*/
    {
        public enum RefundOutcome { Succeeds, AlreadyRefunded, Fails }
        public enum CancelOutcome { Succeeds, Fails }

        public RefundOutcome Refunds { get; set; } = RefundOutcome.Succeeds;
        public CancelOutcome PaymentIntentCancels { get; set; } = CancelOutcome.Succeeds;

        /// <summary>Every request the production code made, as "POST /v1/refunds" style entries.</summary>
        public List<string> Requests { get; } = new();
        // Requests reords every fake Stripe Reqeust
        //Example: POST /v1/refunds
        //POST /v1/payment_intents/pi_123/cancel
        public int RefundAttempts => Requests.Count(r => r.Contains("/v1/refunds"));
        /* They my test can assert: Assert.Equal(1, fakeStripe.RefundAttempts) */
        private readonly IStripeClient _previousClient;

        public FakeStripe()
        {
            _previousClient = StripeConfiguration.StripeClient;
            StripeConfiguration.StripeClient = new StripeClient(
                "sk_test_fake_stripe",
                httpClient: new SystemNetHttpClient(new HttpClient(new Handler(this))));
        }
        /* This constructor is the clever part:
         * Stripe.NET normally uses StripeConfiguration.StripeClient as its HttpClient
         This code replaces it
        Before: 
        StripeConfiguration.StripeClient -> Real Stripe HTTP Client
        After: StripeConfiguration.StripeClient -> Fake HttpClient -> Handler
        So when your code does "new RefundService()", the Stripe SDK unknowingly uses my fake*/

        // Restores the previous client, so a tier-1 test running afterwards still fails loudly if it
        // strays onto a Stripe path instead of quietly getting a canned success.
        public void Dispose() => StripeConfiguration.StripeClient = _previousClient;
        /* Dispose restores Stripe
         * why? Because StripeConfiguration.StripeClient is a global state
         Without restoring it:
        Test 1: Replace Stripe with fake
        Test 2: Still using fake accidentally -> That could hide bugs
        Restoring it means Test starts -> Fake Stripe -> Test ends -> Original Stripe client restored*/
        private sealed class Handler : HttpMessageHandler
        /* This is the fake server
         * HttpMessageHandler is the low level thing that receives HTTP requests.
         Every time Stripe SDK tries "POST https://api.stripe.com/v1/refunds"
        this method SendAsync below catches it*/
        {
            private readonly FakeStripe _fake;
            public Handler(FakeStripe fake) => _fake = fake;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                _fake.Requests.Add($"{request.Method} {path}");

                if (path.StartsWith("/v1/refunds"))//this means stripe is trying to create a refund
                    return Task.FromResult(_fake.Refunds switch // this decides the response
                    {
                        RefundOutcome.Succeeds => Json(HttpStatusCode.OK,/* this returns: {
                                                                         "id":"re_fake",
                                                                         "status":"succeeded"
                                                                        } */
                            @"{""id"":""re_fake"",""object"":""refund"",""status"":""succeeded""}"),
                        // The one Stripe error StripeRefunds treats as success: the money is already back.
                        RefundOutcome.AlreadyRefunded => Json(HttpStatusCode.BadRequest,
                            @"{""error"":{""type"":""invalid_request_error"",""code"":""charge_already_refunded"",""message"":""Charge already refunded (FakeStripe)""}}"),
                        _ => Json(HttpStatusCode.PaymentRequired,
                            @"{""error"":{""type"":""card_error"",""code"":""refund_failed"",""message"":""Refund failed (FakeStripe)""}}")
                    });

                if (path.Contains("/cancel"))//stripe is trying to cancel a payment intent
                    return Task.FromResult(_fake.PaymentIntentCancels == CancelOutcome.Succeeds
                        ? Json(HttpStatusCode.OK, PaymentIntent("canceled"))/* {Status": "canceled"} */
                        // What Stripe returns when the intent is already succeeding and can't be voided.
                        : Json(HttpStatusCode.BadRequest,
                            @"{""error"":{""type"":""invalid_request_error"",""code"":""payment_intent_unexpected_state"",""message"":""Cannot cancel (FakeStripe)""}}"));

                if (path.StartsWith("/v1/payment_intents"))
                    return Task.FromResult(Json(HttpStatusCode.OK, PaymentIntent("requires_payment_method")));

                // Anything else is a path this fake was never taught about - fail loudly rather than
                // letting a test quietly believe Stripe agreed with it.
                /* This means that if the application suddenly starts calling another Stripe endpoint, dont silently
                 * pretend everything worked*/
                return Task.FromResult(Json(HttpStatusCode.NotImplemented,
                    $@"{{""error"":{{""type"":""api_error"",""message"":""FakeStripe has no route for {path}""}}}}"));
            }

            private static string PaymentIntent(string status) =>
                $@"{{""id"":""pi_fake"",""object"":""payment_intent"",""status"":""{status}"",""amount"":2500,""currency"":""eur""}}";

            private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
                new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
