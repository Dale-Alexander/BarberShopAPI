using BarberShopAPI.Services;
using Hangfire.Server;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* Records calls instead of sending. In tier 1 nothing should ever reach this - the Hangfire server is
     * stopped, so email jobs are only ever ENQUEUED (asserted via ApiFactory.EnqueuedEmailJobs). It exists
     * so a stray in-process call can be asserted on, and so no test can ever hit Resend for real.
     *
     * Tier 2 will make these throw on demand to drive the email-failed-after-retries -> NeedsReview branches. */
    public class FakeEmailService : IEmailService
        /* Actual production code does this: public async Task sendBookingConfirmationemailAsync(int bookingId){
         *... 
         }
        Then it does, await _emailService.sendBookingconfirmationEmailAsync(42); So a real email is sent
        *You dont want this, this means it has to provide all the same methods as the real email service.
        *To the rest of your application, nothing changes. Your controller still writes:
        *await _emailService.sendbookingReminderEmailAsync(42); It has no idea it is talking to a fakse*/
    {
        public List<(string Method, int BookingId)> Sent { get; } = new();
        /* This is just an in memory list.
         * Initially its empty: []
         * When emails are sent the list becomes [("sendBookingReminderEmailAsync", 15), ("sendBookingConfrimationEmailAsync", 18)]
         */

        private Task Record(string method, int bookingId)
            /* Instead of every method repeatign Sent.Add(...)
             * return Task.CompletedTask;, they put it in one helper so every email method just says Record(...)*/
        {
            Sent.Add((method, bookingId));
            return Task.CompletedTask;
        }

        public Task sendBookingReminderEmailAsync(int bookingId) => Record(nameof(sendBookingReminderEmailAsync), bookingId);
        public Task sendBookingConfirmationEmailAsync(int bookingId) => Record(nameof(sendBookingConfirmationEmailAsync), bookingId);
        public Task sendBookingCancellationEmailAsync(int bookingId, bool refundIssued) => Record(nameof(sendBookingCancellationEmailAsync), bookingId);
        /* why ignore the parameters: Take the above method. It is public Task sendBookingCancellationEmailAsync(int bookingId, bool refundIssued) Notice it only records Record(..., bookingId)
         * It ignores refundIssued. Why? Because the fake only cares that the method was called, not what email content would have been generated*/
        public Task sendBookingCancelledBarberUnavailableEmailAsync(int bookingId, bool refundIssued, PerformContext? context) => Record(nameof(sendBookingCancelledBarberUnavailableEmailAsync), bookingId);
        public Task sendBookingCancelledDueToClosureEmailAsync(int bookingId, PerformContext? context) => Record(nameof(sendBookingCancelledDueToClosureEmailAsync), bookingId);
        public Task sendBookingCancelledDueToScheduleChangeEmailAsync(int bookingId, PerformContext? context) => Record(nameof(sendBookingCancelledDueToScheduleChangeEmailAsync), bookingId);
        public Task sendPaymentRefundedUnconfirmedEmailAsync(int bookingId, PerformContext? context) => Record(nameof(sendPaymentRefundedUnconfirmedEmailAsync), bookingId);
        public Task sendBookingRescheduledEmailAsync(int bookingId, DateTime oldStartDateTime) => Record(nameof(sendBookingRescheduledEmailAsync), bookingId);
        public Task sendPasswordResetEmailAsync(int userId, string rawToken) => Record(nameof(sendPasswordResetEmailAsync), userId);
    }
}
