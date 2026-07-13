using BarberShopAPI.Data;
using Microsoft.EntityFrameworkCore;
using Resend;
using BarberShopAPI.Models.Enums;
using System.Net;
using System.Text.Encodings.Web;
using BarberShopAPI.Common;

namespace BarberShopAPI.Services
{

    public interface IEmailService
    {
        Task sendBookingReminderEmailAsync(int bookingId);
        Task sendBookingConfirmationEmailAsync(int bookingId);
        Task sendBookingCancellationEmailAsync(int bookingId, bool refundIssued);
        Task sendBookingCancelledDueToClosureEmailAsync(int bookingId);
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

        /* Like sendBookingCancellationEmailAsync, but for the case where WE cancelled the customer's
         * booking because a shop closure landed on their slot - so the customer gets no warning unless
         * we tell them. The generic cancellation email doesn't say why, which for an unprompted
         * cancellation reads as us dropping their appointment for no reason.
         *
         * We include the Payment so we only promise a refund when one actually happened: CancelBooking
         * sets the payment to REFUNDED after Stripe confirms it, so REFUNDED here means the money is
         * genuinely on its way back. A cash/unpaid booking has nothing to refund, so we omit that line.*/
        public async Task sendBookingCancelledDueToClosureEmailAsync(int bookingId)
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

            await _resend.EmailSendAsync(message);
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
