using BarberShopAPI.Data;
using BarberShopAPI.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BarberShopAPI.Models.Enums;
using Stripe;
using Hangfire;
using BarberShopAPI.Services;
using BarberShopAPI.Common;


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
            if (paymentIntent == null) return Ok(new { message = "Invalid event data" });
            //you can change the above to Ok("Event ignored"); That is for when a charge object gets received and not a payment intent. PaymentIntent and charge objects gets
            //sent every time you make a payment. You can do Ok(...) so you stop seeing it as an error everytime you recieve an object(which is every time).
            var metadata = paymentIntent.Metadata ?? new Dictionary<string, string>();
            if (!metadata.TryGetValue("BookingId", out var bookingIdStr) ||
                !metadata.TryGetValue("Phone", out var phone) ||
                !metadata.TryGetValue("FullName", out var fullName) ||
                !metadata.TryGetValue("Email", out var email) ||
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
                return Ok(new { message = "Missing metadata" });
                /* so the whole block is saying - if any of these five things are missing or invalid, return
                 * a 400 and stop processing*/
            }
            try
            {
                var booking = await _context.Bookings.FindAsync(bookingId);
                if (booking == null) return Ok(new { message = "Booking not found" });

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

                            // Only reachable once we know this specific PaymentIntent was never recorded before
                            // and this booking has no completed payment yet. If the booking still isn't PENDING
                            // at this point, it means it expired, was cancelled by a closure, or was completed
                            // some other way (e.g. cash) while this PaymentIntent was still alive - a stray charge
                            // with no confirmable booking to attach it to. The card was genuinely charged, so we
                            // refund it automatically and tell the customer instead of leaving a stuck charge.
                            if (booking.Status != BookingStatus.PENDING)
                            {
                                Console.WriteLine($"Booking {bookingId} is no longer pending (status: {booking.Status}); refunding orphaned PaymentIntent {paymentIntent.Id}.");

                                if (amountReceived > 0)
                                {
                                    var orphanRefund = await StripeRefunds.RefundIdempotentlyAsync(paymentIntent.Id);
                                    if (orphanRefund == StripeRefunds.Outcome.Failed)
                                    {
                                        // Persist the review flag before returning non-2xx. Stripe retries this event and
                                        // the idempotent refund may succeed on a later attempt (which clears the flag below);
                                        // if it never does, the booking stays in the admin's review worklist instead of a
                                        // charge being silently kept once Stripe gives up retrying after ~3 days.
                                        booking.NeedsReview = true;
                                        booking.ReviewReason = $"Automatic refund of an orphaned charge failed (PaymentIntent {paymentIntent.Id}) - refund it in Stripe by hand.";
                                        await _context.SaveChangesAsync();
                                        return StatusCode(500, "Refund failed"); // non-2xx => Stripe retries later
                                    }
                                }

                                // Record the refund for a financial trail - but only when this booking has no
                                // Payment row yet (UX_Payment_BookingId is unique). The cash-completed case already
                                // has a row, so we skip it there and lean on the refund idempotency key for safety.
                                var hasPayment = await _context.Payments.AnyAsync(p => p.BookingId == bookingId);
                                if (!hasPayment)
                                {
                                    _context.Payments.Add(new Payment
                                    {
                                        BookingId = bookingId,
                                        StripePaymentIntentId = paymentIntent.Id,
                                        Amount = amountReceived / 100m,
                                        Status = PaymentStatus.REFUNDED,
                                        Method = Models.Enums.PaymentMethod.CARD,
                                        PaidAt = DateTime.UtcNow
                                    });

                                    // A booking cancelled from PENDING never had a user/email linked, so pull the
                                    // contact details from the metadata to give the refund email somewhere to go.
                                    if (booking.UserId == null)
                                    {
                                        var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == phone);
                                        if (user == null)
                                        {
                                            var nameParts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                            if (nameParts.Length > 0)
                                            {
                                                user = new User
                                                {
                                                    Name = nameParts[0],
                                                    Surname = nameParts.Length > 1 ? string.Join(" ", nameParts.Skip(1)) : null,
                                                    Phone = phone
                                                };
                                                _context.Users.Add(user);
                                            }
                                        }
                                        if (user != null) booking.User = user;
                                    }
                                    if (string.IsNullOrWhiteSpace(booking.ContactEmail)) booking.ContactEmail = email;
                                    await _context.SaveChangesAsync();
                                }

                                // Refund went through - clear any review flag a previous failed attempt left set.
                                if (booking.NeedsReview)
                                {
                                    booking.NeedsReview = false;
                                    booking.ReviewReason = null;
                                    await _context.SaveChangesAsync();
                                }

                                // We return Ok below, so Stripe won't retry this event - one refund, one email.
                                BackgroundJob.Enqueue<IEmailService>(service => service.sendPaymentRefundedUnconfirmedEmailAsync(bookingId, null));
                                return Ok(new { message = "Payment for an unconfirmable booking was refunded" });
                            }

                            //This slot is for when the admin creates a closure between the PENDING -> COMPLETED stage of a booking
                            // A shop closure can be created for this slot after the customer started paying.
                            // PENDING -> COMPLETED must never happen on a closed slot, so we re-check closures
                            // here at the moment of confirmation - this is the point that actually resolves the
                            // race, no matter which happened first. The status guards above run first so this only
                            // ever fires on a genuine, first-time, still-pending confirmation (never a Stripe retry).
                            // The card has already been charged, so we refund it, record the money in-and-out, and
                            // cancel instead of confirming. Same query the booking-create paths use to block new bookings.
                            var appointmentDate = DateOnly.FromDateTime(booking.StartDateTime);
                            var appointmentTime = TimeOnly.FromDateTime(booking.StartDateTime);
                            var endTime = TimeOnly.FromDateTime(booking.StartDateTime.AddMinutes(booking.DurationMin));
                            var closure = await _context.ShopClosures.FirstOrDefaultAsync(s =>
                                s.IsActive == true && (s.BarberId == null || s.BarberId == booking.BarberId) &&
                                ((s.EndDate == null && s.StartDate == appointmentDate) ||
                                 (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) &&
                                (s.IsFullDay || (s.StartTime < endTime && s.EndTime > appointmentTime)));
                            if (closure != null)
                            {
                                // Refund first, outside the DB transaction: cancelling before the money is
                                // confirmed back would risk the "booking dead but money kept" state. The
                                // idempotency key (keyed to the PaymentIntent) means a Stripe retry after a
                                // failed DB write below replays the same refund as a success instead of
                                // refunding twice - so the retry can get past this and finish recording the
                                // cancellation it couldn't complete last time.
                                var closureRefund = await StripeRefunds.RefundIdempotentlyAsync(paymentIntent.Id);
                                if (closureRefund == StripeRefunds.Outcome.Failed)
                                {
                                    Console.WriteLine($"Booking {bookingId} fell on a closure but the refund failed for PaymentIntent {paymentIntent.Id}. Needs manual refund/follow-up.");
                                    // Flag before the non-2xx so this surfaces in the review worklist if Stripe's retries
                                    // never get the refund through. Cleared inside the transaction below once one succeeds.
                                    booking.NeedsReview = true;
                                    booking.ReviewReason = $"Slot was closed after payment but the automatic refund failed (PaymentIntent {paymentIntent.Id}) - refund it in Stripe by hand.";
                                    await _context.SaveChangesAsync();
                                    return StatusCode(500, "Refund failed");
                                }

                                await using var closureTx = await _context.Database.BeginTransactionAsync();
                                try
                                {
                                    // Record the charge-and-refund so there's a financial trail, the closure email
                                    // knows a refund happened (it checks Payment.Status == REFUNDED), and Stripe's
                                    // retry short-circuits on the existingPayment guard above instead of re-refunding.
                                    _context.Payments.Add(new Payment
                                    {
                                        BookingId = bookingId,
                                        StripePaymentIntentId = paymentIntent.Id,
                                        Amount = amountReceived / 100m,
                                        Status = PaymentStatus.REFUNDED,
                                        Method = Models.Enums.PaymentMethod.CARD,
                                        PaidAt = DateTime.UtcNow
                                    });

                                    // Link the user and set ContactEmail from the metadata just like the
                                    // success path does - without this the cancellation email below silently
                                    // no-ops (sendBookingCancelledDueToClosureEmailAsync bails when ContactEmail
                                    // is blank, and would NPE on booking.User.Name). This customer was charged
                                    // and refunded, so they must actually be told.
                                    if (booking.UserId == null)
                                    {
                                        var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == phone);
                                        if (user == null)
                                        {
                                            var nameParts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                            if (nameParts.Length == 0)
                                            {
                                                Console.WriteLine("Invalid fullName in metadata");
                                                await closureTx.RollbackAsync();
                                                return Ok(new { message = "Invalid metadata" });
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
                                        }
                                        booking.User = user;
                                    }
                                    booking.ContactEmail = email;
                                    booking.Status = BookingStatus.CANCELLED;
                                    booking.CancellationReason = CancellationReason.ShopClosure;
                                    // Refund succeeded and we're committing the cancellation - clear any review flag a
                                    // previous failed attempt set, atomically with the state change.
                                    booking.NeedsReview = false;
                                    booking.ReviewReason = null;
                                    await _context.SaveChangesAsync();
                                    await closureTx.CommitAsync();
                                }
                                catch (Exception ex)
                                {
                                    await closureTx.RollbackAsync();
                                    Console.WriteLine($"Error cancelling closure-conflicting booking {bookingId} after refund: {ex.Message}");
                                    return StatusCode(500, "Server error");
                                }

                                BackgroundJob.Enqueue<IEmailService>(service => service.sendBookingCancelledDueToClosureEmailAsync(bookingId, null));
                                /* The reason we send an email is because Stripe is the caller of this endpoint not the customer, meaning that our 
                                 * responses get seen by Stripe not the customer, therefore the only way we can notify the person is through email. 
                                 On the other hand in /confirm-cash, we just send a response because that is sufficient enough to inform the user what 
                                happened.*/
                                return Ok(new { message = "Slot was closed after payment - refunded and cancelled" });
                            }

                            await using var transaction = await _context.Database.BeginTransactionAsync();
                            try
                            {
                                if (booking.UserId == null)
                                /* this if statement might seem unnecessary but one case
                                 * worth keeping in mind is Stripe Retries. If your server
                                 returns a 500, Stripe will resend the webhook and hit this code
                                again. By that point the user may already exist(found by phone) so 
                                FirstOrDefaultAsync() handles that safely. The booking however might also
                                already be linked and that is where this if statement comes in because if 
                                thats the case, then you avoid having to write the following again
                                "booking.User = user;
                                await _context.SaveChangesAsync();"
                                */
                                {
                                    var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == phone);
                                    if (user == null)
                                    {
                                        var nameParts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                        if (nameParts.Length == 0)
                                        {
                                            Console.WriteLine("Invalid fullName in metadata");
                                            await transaction.RollbackAsync();
                                            return Ok(new { message = "Invalid metadata" });
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
                                        Console.WriteLine($"Created new user");
                                    }
                                    booking.User = user;
                                    Console.WriteLine($"Linked booking {bookingId} to user ");
                                }
                                booking.ContactEmail = email;
                                _context.Payments.Add(new Payment
                                {
                                    BookingId = bookingId,
                                    StripePaymentIntentId = paymentIntent.Id,//this is a long string
                                    Amount = amountReceived / 100m,//the m suffix makies it a decimal division instead of integer division meaning it doesnt get truncated
                                    Status = PaymentStatus.COMPLETED,
                                    Method = Models.Enums.PaymentMethod.CARD,//PaymentMethod conflicted with Stripe's PaymentMethod
                                    PaidAt = DateTime.UtcNow
                                });
                                booking.Status = BookingStatus.COMPLETED;

                                var reminderTime = booking.StartDateTime.AddHours(-2);
                                var delay = ShopClock.ToUtc(reminderTime) - DateTime.UtcNow;
                                if(delay > TimeSpan.Zero)
                                {
                                    var jobId = BackgroundJob.Schedule<IEmailService>(service => service.sendBookingReminderEmailAsync(bookingId), delay);
                                    booking.ReminderJobId = jobId;
                                }
                                // A successful confirmation is a terminal, resolved state - clear any review flag a
                                // prior failed attempt set (e.g. the closure that caused it was since removed).
                                booking.NeedsReview = false;
                                booking.ReviewReason = null;
                                await _context.SaveChangesAsync();
                                await transaction.CommitAsync();
                                Console.WriteLine($"Payment succeeded, booking updated:{bookingId}");
                                
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
                            BackgroundJob.Enqueue<IEmailService>(service => service.sendBookingConfirmationEmailAsync(bookingId));
                            return Ok("Payment intent succeeded");
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

            /* The reason Schedule is inside the transaction and Enqueue is not is because
             * if you want to delete the booking later on, you need to jobID to delete it. So the jobID and
             * the booking must be consistent. Lets say Schedule is moved outide the transaction
             var jobId = BackgroundJob.Schedule<IEmailService>(..., delay);
booking.ReminderJobId = jobId;
await _context.SaveChangesAsync(); 
            Now consider failure modes: 
            Transaction commits -> Schedule runs, creates job -> Second SaveChangesAsync fails
            Result: Hangire has a job, but the booking doesnt know about it. Orphan job. Cant cancel it later
            Or:
            Transaction commits -> Schedule throws
            Result: Booking is confirmed but no reminder scheduled and customer may miss appointment

            But wait, Hangire's job cant actually be rolled back - it's in its own storage. So if the transaction rolls back after Schedule succeeded, you'd have
            an orphan job.
            This is a real edge case, but it's much smaller than the alternative.
            The window where the job would be created but the transaction fails is tiny(milliseconds), whereas the
            "second SaveChanges fails" window in the alternative is larger. Plus my defensive check in the 
            email service(if booking == null -> return) handles the orphan job, no booking found, silent skip.

            Enqueue: This jobId isnt stored anywhere in the booking. You dont care what it is. 
            You'll never need to cancel it, never need to reference it. It's fire and forget
            So there's no reason to include this in the transaction. Doing so would only make the transaction longer 
            for no benefit. What if Enqueue throws:
            Transaction committed -> Payment recorded, booking confirmed, enqueue fauls
            Result: Booking confirmed, no confirmation email.
            But this is fine. It is a minor inconvenience
             */
        }
    }
}