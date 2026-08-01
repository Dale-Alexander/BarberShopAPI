using BarberShopAPI.Data;
using Microsoft.EntityFrameworkCore;
using Resend;
using BarberShopAPI.Models.Enums;
using System.Net;
using System.Text.Encodings.Web;
using BarberShopAPI.Common;
using Hangfire;
using Hangfire.Server;

namespace BarberShopAPI.Services
{
    /* Retry policy shared between the closure-cancellation email job's [AutomaticRetry] attribute and the
     * job body, so "how many attempts before we give up" has one source of truth. Attempts is the number of
     * RETRIES after the first run, so total executions = ClosureCancelRetries + 1. */
    public static class EmailJobPolicy
    {
        public const int ClosureCancelRetries = 5;

        /* Same idea as ClosureCancelRetries, for the "we couldn't confirm your booking, here's your refund"
         * email: once exhausted we flag the booking so staff phone the customer instead. */
        public const int RefundNoticeRetries = 5;

        /* Same idea again, for the "your barber is no longer available" email. Like the closure email this
         * is the customer's ONLY notice their confirmed appointment is gone, so once retries are spent we
         * flag the booking for a manual phone call rather than let it vanish into the failed-jobs list. */
        public const int BarberUnavailableRetries = 5;
    }

    public interface IEmailService
    {
        Task sendBookingReminderEmailAsync(int bookingId);
        Task sendBookingConfirmationEmailAsync(int bookingId);
        Task sendBookingCancellationEmailAsync(int bookingId, bool refundIssued);

        /* [AutomaticRetry] + NeedsReview-on-exhaustion for the same reason as the closure email below: a
         * barber-deactivation cancellation is the customer's only notice their appointment is gone, so a
         * permanently-failed email must surface for a manual call, not disappear. context is filled by
         * Hangfire at run time (callers pass null). */
        [AutomaticRetry(Attempts = EmailJobPolicy.BarberUnavailableRetries)]
        Task sendBookingCancelledBarberUnavailableEmailAsync(int bookingId, bool refundIssued, PerformContext? context);

        /* The [AutomaticRetry] lives on the interface method because that's the MethodInfo Hangfire records
         * when the job is enqueued via BackgroundJob.Enqueue<IEmailService>(...), so this is where it reads
         * the filter. context is filled in by Hangfire at run time (callers pass null); the body uses it to
         * tell a transient failure it should retry from the final attempt where it flags for a manual call. */
        [AutomaticRetry(Attempts = EmailJobPolicy.ClosureCancelRetries)]
        Task sendBookingCancelledDueToClosureEmailAsync(int bookingId, PerformContext? context);

        /* Same shape and criticality as the closure email (the customer's only notice, so retry then flag on
         * exhaustion) - reuses the closure retry policy. Sent when a schedule change, not a closure, left a
         * paid card booking outside the barber's hours; distinct wording so we don't claim the shop closed. */
        [AutomaticRetry(Attempts = EmailJobPolicy.ClosureCancelRetries)]
        Task sendBookingCancelledDueToScheduleChangeEmailAsync(int bookingId, PerformContext? context);
        /* [AutomaticRetry] here for the same reason as the closure email above (Hangfire reads the filter off
         * the interface method). Only the "booking couldn't be confirmed" shape flags on final failure - the
         * shapes where the appointment still stands are a harmless courtesy, so they're left to fail quietly. */
        [AutomaticRetry(Attempts = EmailJobPolicy.RefundNoticeRetries)]
        Task sendPaymentRefundedUnconfirmedEmailAsync(int bookingId, PerformContext? context);
        Task sendBookingRescheduledEmailAsync(int bookingId, DateTime oldStartDateTime);
        Task sendPasswordResetEmailAsync(int userId, string rawToken);
    }
    public class EmailService : IEmailService
    {

        private readonly IResend _resend;
        private readonly BarberShopContext _context;
        public EmailService(IResend resend, BarberShopContext context)
        {
            _resend = resend;
            _context = context;
        }
        public async Task sendBookingConfirmationEmailAsync(int bookingId)
        {
            var booking = await _context.Bookings.Include(b => b.User).FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return;
            if (booking.Status == BookingStatus.CANCELLED) return;
            if (booking.ConfirmationSentAt != null) return;

            var safeName = WebUtility.HtmlEncode(booking.User.Name);
            var message = new EmailMessage();
            message.From = "Dale's Barbershop <onboarding@resend.dev>";
            message.To.Add(booking.ContactEmail);
            message.Subject = "Booking Confirmed";
            message.HtmlBody = $@"
                <h2>Hi {safeName},</h2>
                <p>Your appointment is booked for
                <strong>{booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}</strong>.</p>";

            message.TextBody = $"Hi {booking.User.Name},\n\n" +
                           $"Your appointment is booked for {booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}.\n\n" +
                           $"See you soon!";

            await _resend.EmailSendAsync(message);
            booking.ConfirmationSentAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        /* What a Needs-Review worklist entry says about the money, shared by the three cancellation emails
         * that flag a booking once their retries are spent. Deliberately says something in EVERY case:
         * staff are about to phone this customer, and staying silent when no refund happened leaves "cash
         * booking, nothing owed" indistinguishable from "card payment we are still holding". Read off the
         * Payment row rather than passed in by the caller, so it describes what actually happened by the
         * time the final attempt gave up. A REFUNDED row is always a card payment - BookingCanceller only
         * sets that status after Stripe confirms, which needs a PaymentIntent. */
        private static string RefundNoteForWorklist(Models.Payment? payment) => payment switch
        {
            null => " No payment was recorded for this booking.",
            { Status: PaymentStatus.REFUNDED } => " Their card payment has been refunded in full.",
            { Method: PaymentMethod.CASH } => " This was a cash booking - there is nothing to refund.",
            /* Not reachable from today's callers - BookingCanceller returns RefundFailed, and the webhook
             * returns 500, both BEFORE enqueuing any email - but spelled out so a future caller that does
             * reach it tells staff the money is still with us instead of saying nothing at all. */
            _ => " Their card payment has NOT been refunded - check Stripe and refund it by hand."
        };

        public async Task sendBookingReminderEmailAsync(int bookingId)
        {
            var booking = await _context.Bookings.Include(b => b.User).FirstOrDefaultAsync(b => b.Id == bookingId);
            if(booking == null)
            {
                return;
            }
            if (booking.Status == BookingStatus.CANCELLED)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(booking.ContactEmail))
            {
                return;
            }
            if(booking.StartDateTime <= ShopClock.Now)
            {
                return;
            }
            if (booking.ReminderSentAt != null) return;
            var safeName = WebUtility.HtmlEncode(booking.User.Name);
            var message = new EmailMessage();
            message.From = "Dale's Barbershop <onboarding@resend.dev>";
            message.To.Add(booking.ContactEmail);
            message.Subject = "Reminder: Your Appointment in 2 hours";
            message.HtmlBody = $@"
                <h2>Hi {safeName},</h2>
                <p>Just a reminder that your appointment is at
                <strong>{booking.StartDateTime:h:mm tt}</strong> today.</p>
                <p>See you soon!</p>";
            message.TextBody = $"Hi {booking.User.Name},\n\n" +
                           $"Just a reminder that your appointment is at {booking.StartDateTime:h:mm tt} today.\n\n" +
                           $"See you soon!";
            await _resend.EmailSendAsync(message);
            booking.ReminderSentAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

        }

        /* refundIssued is passed by BookingCanceller, which knows whether a Stripe refund actually went out
         * (a card booking cancelled outside the 24h window). We only promise a refund when one really
         * happened - cancelling inside the window, or a cash booking, refunds nothing, so we omit that line
         * rather than imply money is on its way back. */
        public async Task sendBookingCancellationEmailAsync(int bookingId, bool refundIssued)
        {
            var booking = await _context.Bookings.Include(b => b.User).FirstOrDefaultAsync(b => b.Id == bookingId);
            if(booking == null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(booking.ContactEmail))
            {
                return;
            }

            var refundHtml = refundIssued
                ? "<p>A full refund has been issued to your original payment method and should appear within a few business days.</p>"
                : "";
            var refundText = refundIssued
                ? "A full refund has been issued to your original payment method and should appear within a few business days.\n\n"
                : "";

            var safeName = WebUtility.HtmlEncode(booking.User.Name);
            var emailMessage = new EmailMessage
            {
                From = "Dale's Barbershop <onboarding@resend.dev>",
                Subject = "Appointment has been cancelled"
            };
            emailMessage.To.Add(booking.ContactEmail);
            emailMessage.HtmlBody = $@"
            <h2>Hi {safeName},</h2>
            <p>Your appointment scheduled for
            <strong>{booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}</strong>
            has been cancelled.</p>
            {refundHtml}
            <p>If you'd like to book again, feel free to visit our website.</p>";

            emailMessage.TextBody = $"Hi {booking.User.Name},\n\n" +
                       $"Your appointment scheduled for {booking.StartDateTime:dddd, MMMM d 'at' h:mm tt} has been cancelled.\n\n" +
                       refundText +
                       $"If you'd like to book again, feel free to visit our website.";
            await _resend.EmailSendAsync(emailMessage);

        }

        /* Sent when we cancel a customer's confirmed booking because the barber they booked was
         * deactivated (left the shop). Like the closure cancellation, this is an unprompted shop-side
         * cancellation, so the customer gets no warning unless we tell them - and the generic "your
         * appointment has been cancelled" email doesn't say why, which reads as us dropping their
         * appointment for no reason. We name the barber and point them back to rebook with someone else.
         * refundIssued is passed by BookingCanceller (true only when a card payment was actually
         * refunded), so we only promise a refund when one really happened. */
        public async Task sendBookingCancelledBarberUnavailableEmailAsync(int bookingId, bool refundIssued, PerformContext? context)
        {
            var booking = await _context.Bookings
                .Include(b => b.User)
                .Include(b => b.Barber).ThenInclude(b => b.User)
                // For the worklist note below only - the email body's refund promise still comes from the
                // refundIssued the caller passed, which is already what the tests pin.
                .Include(b => b.Payment)
                .FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return;
            if (string.IsNullOrWhiteSpace(booking.ContactEmail)) return;

            var safeName = WebUtility.HtmlEncode(booking.User?.Name);
            var barberName = WebUtility.HtmlEncode(booking.Barber?.User?.Name ?? "Your barber");

            var refundHtml = refundIssued
                ? "<p>A full refund has been issued to your original payment method and should appear within a few business days.</p>"
                : "";
            var refundText = refundIssued
                ? "A full refund has been issued to your original payment method and should appear within a few business days.\n\n"
                : "";

            var message = new EmailMessage
            {
                From = "Dale's Barbershop <onboarding@resend.dev>",
                Subject = "Your appointment has been cancelled"
            };
            message.To.Add(booking.ContactEmail);
            message.HtmlBody = $@"
            <h2>Hi {safeName},</h2>
            <p>{barberName} is no longer available, so your appointment scheduled for
            <strong>{booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}</strong> has been cancelled.</p>
            {refundHtml}
            <p>We're sorry for the inconvenience. Please visit our website to book again with another barber.</p>";

            message.TextBody = $"Hi {booking.User?.Name},\n\n" +
                       $"{(booking.Barber?.User?.Name ?? "Your barber")} is no longer available, so your appointment scheduled for " +
                       $"{booking.StartDateTime:dddd, MMMM d 'at' h:mm tt} has been cancelled.\n\n" +
                       refundText +
                       $"We're sorry for the inconvenience. Please visit our website to book again with another barber.";

            try
            {
                await _resend.EmailSendAsync(message);
            }
            catch (Exception ex)
            {
                /* Same policy as the closure email: this is the customer's only notice their confirmed
                 * appointment is gone, so on a transient failure let Hangfire keep retrying; only once the
                 * retries are exhausted do we give up and flag the booking for a manual call, so it surfaces
                 * in the admin's Needs-Review worklist instead of vanishing into the failed-jobs list. */
                var retryCount = context?.GetJobParameter<int>("RetryCount") ?? 0;
                if (retryCount < EmailJobPolicy.BarberUnavailableRetries) throw;

                var refundNote = RefundNoteForWorklist(booking.Payment);
                booking.FlagForReview(
                    $"Barber-unavailable email to {booking.ContactEmail} failed after {EmailJobPolicy.BarberUnavailableRetries + 1} attempts ({ex.Message}). "
                    + $"Call the customer to tell them {(booking.Barber?.User?.Name ?? "their barber")} is no longer available and their {booking.StartDateTime:MMM d 'at' h:mm tt} appointment was cancelled.{refundNote}");
                await _context.SaveChangesAsync();
            }
        }

        /* Like sendBookingCancellationEmailAsync, but for the case where WE cancelled the customer's
         * booking because a shop closure landed on their slot - so the customer gets no warning unless
         * we tell them. The generic cancellation email doesn't say why, which for an unprompted
         * cancellation reads as us dropping their appointment for no reason.
         *
         * We include the Payment so we only promise a refund when one actually happened: CancelBooking
         * sets the payment to REFUNDED after Stripe confirms it, so REFUNDED here means the money is
         * genuinely on its way back. A cash/unpaid booking has nothing to refund, so we omit that line.*/
        public async Task sendBookingCancelledDueToClosureEmailAsync(int bookingId, PerformContext? context)
        {
            var booking = await _context.Bookings
                .Include(b => b.User)
                .Include(b => b.Payment)
                .FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return;
            if (string.IsNullOrWhiteSpace(booking.ContactEmail)) return;

            var wasRefunded = booking.Payment?.Status == PaymentStatus.REFUNDED;
            var safeName = WebUtility.HtmlEncode(booking.User.Name);

            var refundHtml = wasRefunded
                ? "<p>A full refund has been issued to your original payment method and should appear within a few business days.</p>"
                : "";
            var refundText = wasRefunded
                ? "A full refund has been issued to your original payment method and should appear within a few business days.\n\n"
                : "";

            var message = new EmailMessage
            {
                From = "Dale's Barbershop <onboarding@resend.dev>",
                Subject = "Your appointment has been cancelled"
            };
            message.To.Add(booking.ContactEmail);
            message.HtmlBody = $@"
            <h2>Hi {safeName},</h2>
            <p>We're sorry, but we've had to close the shop on
            <strong>{booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}</strong>,
            so your appointment for that time has been cancelled.</p>
            {refundHtml}
            <p>We apologise for the inconvenience. Please visit our website to book another time.</p>";

            message.TextBody = $"Hi {booking.User.Name},\n\n" +
                       $"We're sorry, but we've had to close the shop on {booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}, " +
                       $"so your appointment for that time has been cancelled.\n\n" +
                       refundText +
                       $"We apologise for the inconvenience. Please visit our website to book another time.";

            try
            {
                await _resend.EmailSendAsync(message);
            }
            catch (Exception ex)
            {
                /* This email is the customer's ONLY notice that we cancelled their slot - unlike an admin
                 * cancel, no one is standing in front of them to tell them. On a transient failure let
                 * Hangfire keep retrying; only once the retries are exhausted do we give up and flag the
                 * booking for manual follow-up, so it surfaces in the admin's Needs-Review worklist with an
                 * instruction to phone the customer instead of vanishing into the failed-jobs list. */
                var retryCount = context?.GetJobParameter<int>("RetryCount") ?? 0;
                if (retryCount < EmailJobPolicy.ClosureCancelRetries) throw;

                booking.FlagForReview(
                    $"Cancellation email to {booking.ContactEmail} failed after {EmailJobPolicy.ClosureCancelRetries + 1} attempts ({ex.Message}). "
                    + $"Call the customer to tell them their {booking.StartDateTime:MMM d 'at' h:mm tt} appointment was cancelled by the shop closure."
                    + RefundNoteForWorklist(booking.Payment));
                await _context.SaveChangesAsync();
            }
        }

        /* Like sendBookingCancelledDueToClosureEmailAsync, but for when a schedule change (not a closure)
         * left the customer's paid card slot outside the barber's working hours. Same "only notice"
         * criticality and refund-aware body; only the reason wording differs, so we don't claim the shop
         * was closed when it wasn't. */
        public async Task sendBookingCancelledDueToScheduleChangeEmailAsync(int bookingId, PerformContext? context)
        {
            var booking = await _context.Bookings
                .Include(b => b.User)
                .Include(b => b.Payment)
                .FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return;
            if (string.IsNullOrWhiteSpace(booking.ContactEmail)) return;

            var wasRefunded = booking.Payment?.Status == PaymentStatus.REFUNDED;
            var safeName = WebUtility.HtmlEncode(booking.User.Name);

            var refundHtml = wasRefunded
                ? "<p>A full refund has been issued to your original payment method and should appear within a few business days.</p>"
                : "";
            var refundText = wasRefunded
                ? "A full refund has been issued to your original payment method and should appear within a few business days.\n\n"
                : "";

            var message = new EmailMessage
            {
                From = "Dale's Barbershop <onboarding@resend.dev>",
                Subject = "Your appointment has been cancelled"
            };
            message.To.Add(booking.ContactEmail);
            message.HtmlBody = $@"
            <h2>Hi {safeName},</h2>
            <p>We're sorry, but your barber's working hours have changed for
            <strong>{booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}</strong>,
            so your appointment for that time has been cancelled.</p>
            {refundHtml}
            <p>We apologise for the inconvenience. Please visit our website to book another time.</p>";

            message.TextBody = $"Hi {booking.User.Name},\n\n" +
                       $"We're sorry, but your barber's working hours have changed for {booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}, " +
                       $"so your appointment for that time has been cancelled.\n\n" +
                       refundText +
                       $"We apologise for the inconvenience. Please visit our website to book another time.";

            try
            {
                await _resend.EmailSendAsync(message);
            }
            catch (Exception ex)
            {
                /* Same reasoning as the closure email: this is the customer's only notice, so retry on
                 * transient failure and only flag for a manual call once the retries are exhausted. */
                var retryCount = context?.GetJobParameter<int>("RetryCount") ?? 0;
                if (retryCount < EmailJobPolicy.ClosureCancelRetries) throw;

                booking.FlagForReview(
                    $"Cancellation email to {booking.ContactEmail} failed after {EmailJobPolicy.ClosureCancelRetries + 1} attempts ({ex.Message}). "
                    + $"Call the customer to tell them their {booking.StartDateTime:MMM d 'at' h:mm tt} appointment was cancelled by a schedule change."
                    + RefundNoteForWorklist(booking.Payment));
                await _context.SaveChangesAsync();
            }
        }

        /* Sent when a card payment succeeded but there was no confirmable booking to attach it to, so the
         * webhook refunded it (see WebHookController's orphaned-charge guard). Three shapes, because the same
         * refund means different things to the customer:
         *   - booking COMPLETED as CASH: they switched to paying at the shop and this card payment landed
         *     late (ConfirmCashBooking couldn't void the in-flight intent). Their cash is still uncollected,
         *     so we must NOT call this a duplicate - that would read as "you've already paid".
         *   - booking COMPLETED as CARD: an earlier PaymentIntent confirmed it, so this genuinely is a
         *     second card charge we clawed back. The appointment is unaffected either way.
         *   - otherwise (CANCELLED): the booking couldn't be confirmed at all, and this is their only notice
         *     that they have no appointment and the money is on its way back. */
        public async Task sendPaymentRefundedUnconfirmedEmailAsync(int bookingId, PerformContext? context)
        {
            var booking = await _context.Bookings
                .Include(b => b.User)
                .Include(b => b.Payment)   // drives the cash-vs-duplicate wording below
                .FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return;
            if (string.IsNullOrWhiteSpace(booking.ContactEmail)) return;

            var safeName = WebUtility.HtmlEncode(booking.User?.Name);
            var whenText = booking.StartDateTime.ToString("dddd, MMMM d 'at' h:mm tt");
            var bookingStillStands = booking.Status == BookingStatus.COMPLETED;
            var settledInCash = booking.Payment?.Method == PaymentMethod.CASH;
            var isDuplicateCard = booking.Payment?.Method == PaymentMethod.CARD;

            var message = new EmailMessage
            {
                From = "Dale's Barbershop <onboarding@resend.dev>",
                Subject = !bookingStillStands ? "Your payment has been refunded"
                        : settledInCash ? "Your online card payment has been refunded"
                        : isDuplicateCard ? "A duplicate payment has been refunded"
                        : "An extra payment has been refunded"
            };
            message.To.Add(booking.ContactEmail);

            if (bookingStillStands)
            {
                /* One paragraph differs between the three shapes; the "it's on its way back, your appointment
                 * is fine" half is the same, so only the explanation is branched. The last arm is a safety net
                 * for a COMPLETED booking with no Payment row - not reachable today (both confirmation paths
                 * write one) - so it stays vague rather than promising cash or claiming a double charge. */
                var explanationHtml = settledInCash
                    ? $@"You confirmed your appointment on <strong>{whenText}</strong> as a cash payment, but an
                        online card payment for it came through afterwards, so we've refunded that card payment
                        in full."
                    : isDuplicateCard
                    ? $@"We noticed a duplicate card payment for your appointment on <strong>{whenText}</strong>
                        and have refunded it in full."
                    : $@"We received an extra card payment for your appointment on <strong>{whenText}</strong>
                        and have refunded it in full.";

                var explanationText = settledInCash
                    ? $"You confirmed your appointment on {whenText} as a cash payment, but an online card payment " +
                      $"for it came through afterwards, so we've refunded that card payment in full."
                    : isDuplicateCard
                    ? $"We noticed a duplicate card payment for your appointment on {whenText} and have refunded it in full."
                    : $"We received an extra card payment for your appointment on {whenText} and have refunded it in full.";

                // Cash customers still owe at the shop, so their closing line must not imply they're settled.
                var closingLine = settledInCash
                    ? "Your appointment is unaffected - you can settle up at the shop as planned."
                    : "Your appointment is unaffected - we'll see you then!";

                message.HtmlBody = $@"
                <h2>Hi {safeName},</h2>
                <p>{explanationHtml} It should appear on your original payment method within a few business days.</p>
                <p>{closingLine}</p>";
                message.TextBody = $"Hi {booking.User?.Name},\n\n" +
                    $"{explanationText} It should appear on your original payment method within a few business days.\n\n" +
                    $"{closingLine}";
            }
            else
            {
                message.HtmlBody = $@"
                <h2>Hi {safeName},</h2>
                <p>We're sorry - we weren't able to confirm your booking for
                <strong>{whenText}</strong>, so your card payment has been refunded in full. It should appear
                on your original payment method within a few business days.</p>
                <p>Please visit our website to book another time.</p>";
                message.TextBody = $"Hi {booking.User?.Name},\n\n" +
                    $"We're sorry - we weren't able to confirm your booking for {whenText}, so your card payment " +
                    $"has been refunded in full. It should appear on your original payment method within a few business days.\n\n" +
                    $"Please visit our website to book another time.";
            }

            try
            {
                await _resend.EmailSendAsync(message);
            }
            catch (Exception ex)
            {
                /* Two outcomes, on the one axis that matters here. If the booking still stands (whichever of
                 * the two COMPLETED shapes it is), a failed email is a harmless courtesy - the money is back
                 * regardless and their appointment is fine, so we let it fail quietly. But if it couldn't be confirmed,
                 * this is the customer's only notice that they have no booking and a refund is coming; once
                 * Hangfire's retries are spent, flag it so staff phone them instead of leaving them guessing
                 * about a charge on their statement. The refund itself already went out before this email. */
                if (bookingStillStands) throw;

                var retryCount = context?.GetJobParameter<int>("RetryCount") ?? 0;
                if (retryCount < EmailJobPolicy.RefundNoticeRetries) throw;

                booking.FlagForReview(
                    $"Refund notice to {booking.ContactEmail} failed after {EmailJobPolicy.RefundNoticeRetries + 1} attempts ({ex.Message}). "
                    + $"Call the customer to tell them their {booking.StartDateTime:MMM d 'at' h:mm tt} booking couldn't be confirmed and their card payment has been refunded in full.");
                await _context.SaveChangesAsync();
            }
        }

        public async Task sendBookingRescheduledEmailAsync(int bookingId, DateTime oldStartDateTime)
        {
            var booking = await _context.Bookings.Include(b => b.User).FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return;
            if (string.IsNullOrWhiteSpace(booking.ContactEmail)) return;

            var safeName = WebUtility.HtmlEncode(booking.User.Name);

            var message = new EmailMessage
            {
                From = "Dale's Barbershop <onboarding@resend.dev>",
                Subject = "Your appointment has been rescheduled"
            };
            message.To.Add(booking.ContactEmail);

            message.HtmlBody = $@"
        <h2>Hi {safeName},</h2>
        <p>Your appointment has been rescheduled:</p>
        <ul>
            <li>Previous time: <strong>{oldStartDateTime:dddd, MMMM d 'at' h:mm tt}</strong></li>
            <li>New time: <strong>{booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}</strong></li>
        </ul>
        <p>See you then!</p>";

            message.TextBody = $"Hi {booking.User.Name},\n\n" +
                               $"Your appointment has been rescheduled.\n" +
                               $"Previous time: {oldStartDateTime:dddd, MMMM d 'at' h:mm tt}\n" +
                               $"New time: {booking.StartDateTime:dddd, MMMM d 'at' h:mm tt}\n\n" +
                               $"See you then!";

            await _resend.EmailSendAsync(message);
        }

        /* Called via BackgroundJob.Enqueue from authController's RequestPasswordReset,
         * same fire-and-forget pattern as the booking confirmation/reminder/cancellation
         * emails above - the HTTP response to the reset request doesn't wait on this.
         *
         * rawToken is the only place the un-hashed token exists outside of the moment it
         * was generated - it's never written to the database (only its SHA-256 hash is,
         * on User.PasswordResetTokenHash), so this email is the one and only way anyone
         * can obtain a token that will actually pass ResetPassword's lookup. */
        public async Task sendPasswordResetEmailAsync(int userId, string rawToken)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return;
            if (string.IsNullOrWhiteSpace(user.Email)) return;

            var frontendBaseUrl = Environment.GetEnvironmentVariable("FRONTEND_BASE_URL");
            var resetLink = $"{frontendBaseUrl}/reset-password?token={rawToken}";

            var safeName = WebUtility.HtmlEncode(user.Name ?? "there");
            var message = new EmailMessage
            {
                From = "Dale's Barbershop <onboarding@resend.dev>",
                Subject = "Reset your password"
            };
            message.To.Add(user.Email);
            message.HtmlBody = $@"
                <h2>Hi {safeName},</h2>
                <p>We received a request to reset your password. Click the link below to choose a new one:</p>
                <p><a href=""{resetLink}"">{resetLink}</a></p>
                <p>This link expires in 30 minutes. If you didn't request this, you can safely ignore this email.</p>";
            message.TextBody = $"Hi {user.Name ?? "there"},\n\n" +
                           $"We received a request to reset your password. Use this link to choose a new one:\n" +
                           $"{resetLink}\n\n" +
                           $"This link expires in 30 minutes. If you didn't request this, you can safely ignore this email.";

            await _resend.EmailSendAsync(message);
        }
    }
}
