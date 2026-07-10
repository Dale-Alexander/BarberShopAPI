using BarberShopAPI.Data;
using BarberShopAPI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BarberShopAPI.Models.Enums;
using Stripe;


namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class WebHookController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public WebHookController(BarberShopContext context, IConfiguration configuration)
        {
            _context = context;
        }
        [HttpPost]
        public async Task<IActionResult> StripeWebhook()
        {
            //read raw body required for signature verification
            string json;
            using (var reader = new StreamReader(HttpContext.Request.Body))
            {
                json = await reader.ReadToEndAsync();
            }
            /* HttpContext.Request.Body is a stream containing raw body of the
             * incoming HTTP request.
             new StreamReader(...) wraps the request tream with a StreamReader so it 
            can be read as text instead of raw bytes
            using() ensures the StreamReader is disposed automatically after use (releases resources)
            await reader.ReadToEndAsync() asynchronously reads the entire stream until the end
            
             Basically stripe sends json as a raw byte stream. The Stream Reader converts
            those bytes into a C# string. The string still looks exactly like the JSON 
            Stripe sent. Then ConstructEvent() takes that string and deserializes it into
            a proper Stripe.Event*/


            //verify webhook signature
            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(json,
                    Request.Headers["Stripe-Signature"],
                    Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET"));
                Console.WriteLine($"Event type:{stripeEvent.Type}");
                /* .ConstructEvent() is a stripe helper method that does 2 things at one:
                 * it verifies the webhook is genuinely from Stripe and parses the JSON into
                 a usable Stripe.Event object. It takes 3 arguments: 1) json - the raw request body
                you read earlier. Stripe uses exact bytes to calculate the signature. 2)
                Request.Headers["Stripe-Signature"] - Stripe attaches a signature header to every webhook it sends.
                It looks something like this t=1492774577,v1=5257a869e7ecebeda32affa62cdca3fa. v1 is
                the actual signature hash
                3) Environment.GetEnvironmentVariables("STRIPE_WEBHOOK_SECRET") - uour webhook
                secret starting with whsec... Stripe uses this secret to generate the signature
                on their end, and ConstructEvent() uses it to regenerate the signature locally and compare the 2. If they match,
                the webhook is genuine. If they dont, it throws a StripeException which my catch block handles
                
                 So the full picture is:
                Stripe sends webhook -> attaches signature header using whsec secret
                -> ConstructEvent recomputes the signature locally
                -> if they match, you get back a Stripe.Event object*/
            }
            catch (StripeException ex)
            {
                Console.WriteLine($"Webhook signature verification failed:{ex.Message}");
                return BadRequest($"Webhook error, {ex.Message}");
            }
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent == null) return BadRequest(new { message = "Invalid event data" });
            //you can change the above to Ok("Event ignored"); That is for when a charge object gets received and not a payment intent. PaymentIntent and charge objects gets
            //sent every time you make a payment. You can do Ok(...) so you stop seeing it as an error everytime you recieve an object(which is every time).
            var metadata = paymentIntent.Metadata ?? new Dictionary<string, string>();
            if (!metadata.TryGetValue("BookingId", out var bookingIdStr) ||
                !metadata.TryGetValue("Phone", out var phone) ||
                !metadata.TryGetValue("FullName", out var fullName) ||
                !int.TryParse(bookingIdStr, out var bookingId))

            /* stripeEvent.Data.Object is typed as a generic object because a Stripe Event could
             * contain many different things - PaymentIntent, a Customer a Refund etc. The as PaymentIntent
             attempts to cast it to a PaymentIntent. IF the cast fails it returns null instead of throwing
            "if(payment == null) ...: This means that if the cast failed, the event didnt contain a PaymentIntent so we bail out early
            TryGetValue attempts to find a key in the dictionary. Instead of throwing if the key doesnt
            exist, it returns true or false and puts the value into the out variable.
            So:metadata.TryGetValue("bookingId", out var bookingIdStr) → tries to find "bookingId"
            in metadata, stores the value in bookingIdStr, returns true if found. The ! flips it,
            so the whole if fires if any of the keys are missing because if(!bool) means if (!true)
            which means if the key wasnt found.
            
             !int.TryParse(bookingIdStr, out var bookingID) -> tries to convert the string "123" to the integer 123.
            Returns false if it cant which would mean the bookingId in the metadata was corrupted or not a valid "number".
            Remember that the metadata were all sent as string in a string dictionary in startbookingcardflow. */
            {
                Console.WriteLine("Missing required metadata");
                return BadRequest(new { message = "Missing metadata" });
                /* so the whole block is saying - if any of these four things are missing or invalid, return
                 * a 400 and stop processing*/
            }
            try
            {
                var booking = await _context.Bookings.FindAsync(bookingId);
                if (booking == null) return BadRequest(new { message = "Booking not found" });
                if (booking.UserId == null)
                /* this if statement might seem unnecessary but one case
                 * worth keeping in mind is Stripe Retries. If your server
                 returns a 500, Stripe will resend the webhook and hit this code
                again. By that point the user may already exist(found by phone) so 
                FirstOrDefaultAsync() handles that safely. The booking however might also
                already be linked and that is where this if statement comes in because if 
                thats the case, then you avoid having to write the following again
                "booking.UserId = user.Id;
                await _context.SaveChangesAsync();"
                */
                {
                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == phone);
                    if (user == null)
                    {
                        var nameParts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if(nameParts.Length == 0)
                        {
                            Console.WriteLine("Invalid fullName in metadata");
                            return BadRequest(new { message = "Invalid metadata" });
                        }
                        var firstName = nameParts[0];
                        var lastName = nameParts.Length > 1 ? string.Join(" ", nameParts.Skip(1)) : null;
                        user = new User
                        {
                            Name = firstName,
                            Surname = lastName,
                            Phone = phone
                        };
                        _context.Users.Add(user);
                        await _context.SaveChangesAsync();
                        Console.WriteLine($"Created new user: {user.Id}");
                    }
                    booking.UserId = user.Id;
                    await _context.SaveChangesAsync();
                    Console.WriteLine($"Linked booking {bookingId} to user {user.Id}");
                }
                else
                {
                    Console.WriteLine($"Booking already linked to user, skipping: {bookingId}");
                }



                switch (stripeEvent.Type)
                {
                    case "payment_intent.succeeded":
                        try
                        {
                            var amountReceived = paymentIntent.AmountReceived;
                            // Guard against Stripe retrying the same webhook event (same PaymentIntent) 
                            // after a previous delivery already succeeded. Cheap early exit before hitting the transaction
                            var existingPayment = await _context.Payments.FirstOrDefaultAsync(p => p.StripePaymentIntentId == paymentIntent.Id);
                            if (existingPayment != null)
                            {
                                Console.WriteLine($"Payment already recorded, skipped {paymentIntent.Id}");
                                return Ok(new { message = "Payment already recorded" });
                            }

                            var bookingAlreadyPaid = await _context.Payments.AnyAsync(p =>
                            p.BookingId == bookingId && p.Status == PaymentStatus.COMPLETED);
                            // Guard against multiple PaymentIntents succeeding for the same booking.
                            // This can happen if the frontend created more than one PaymentIntent (e.g. after a retry)
                            // and both somehow succeeded. Ensures one booking maps to exactly one completed payment.
                            if (bookingAlreadyPaid)
                            {
                                Console.WriteLine($"Booking {bookingId} already has a completed payment, skipping");
                                return Ok(new { message = "Booking already paid" });
                            }


                            await using var transaction = await _context.Database.BeginTransactionAsync();
                            try
                            {
                                _context.Payments.Add(new Payment
                                {
                                    BookingId = bookingId,
                                    StripePaymentIntentId = paymentIntent.Id,//this is a long string
                                    Amount = amountReceived / 100m,//the m suffix makies it a decimal division instead of integer division meaning it doesnt get truncated
                                    Status = PaymentStatus.COMPLETED,
                                    Method = Models.Enums.PaymentMethod.CARD,//PaymentMethod conflicted with Stripe's PaymentMethod
                                    PaidAt = DateTime.Now
                                });
                                booking.Status = BookingStatus.COMPLETED;
                                await _context.SaveChangesAsync();
                                await transaction.CommitAsync();
                                Console.WriteLine($"Payment succeeded, booking updated:{bookingId}");
                                return Ok("Payment intent succeeded");
                            }
                            catch (DbUpdateException ex)
                            {
                                await transaction.RollbackAsync();
                                Console.WriteLine($"Database error during payment processing:{ex.Message}");
                                return StatusCode(500, "Server error");
                            }
                            catch (Exception ex)
                            {
                                await transaction.RollbackAsync();
                                Console.WriteLine($"Unexpected error during payment processing:{ex.Message}");
                                return StatusCode(500, "Server error");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to process payment:{ex.Message}");
                            return StatusCode(500, "Server error");
                        }
                    default:
                        Console.WriteLine($"Unhandled event type:{stripeEvent.Type}");
                        return Ok("Ignored irrelevant event");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to link booking to user:{ex.Message}");
                return StatusCode(500, "Server error");
            }
        }
    }
}