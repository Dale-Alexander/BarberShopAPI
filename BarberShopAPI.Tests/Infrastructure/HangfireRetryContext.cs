using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using BarberShopAPI.Services;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* Builds a real PerformContext carrying a chosen RetryCount.
     *
     * The email jobs decide between "throw so Hangfire retries" and "give up and flag the booking for a
     * phone call" by reading context.GetJobParameter<int>("RetryCount"). That reads the parameter off the
     * job's STORAGE CONNECTION, not off anything we can pass in directly - so the only honest way to reach
     * the give-up branch is to create a genuine job in a storage connection and set the parameter on it.
     *
     * The alternative would have been to run the job through a real Hangfire server until its retries
     * exhausted, but Hangfire's retry backoff is measured in minutes - unusable in a test suite. */
    public static class HangfireRetryContext
    {
        /* This one is a test helper for simulating Hangfire retry behaviour
         * It exists because your email jobs probably have logic like:
         "If sending an email fails, let Hangfire retry it. But after the final retry, stop throwing and mark the booking as needing review*/
        /// <summary>A context reporting `retryCount` previous attempts. Retries are counted AFTER the first run, so retryCount == the policy constant means the attempts are spent.</summary>
        public static PerformContext WithRetryCount(int retryCount)
            /* PerformContext is the object Hangfire gives your job while it runs
             * A job might look like: 
             public async Task Sendemail(int bookingId, PerformContext context){
            var retries = context.GetJobParameter<int>("RetryCount");
            ...
             }
            The context contains information like the jobId, current retry count,
            job metadata, cancellation token
            My tests need a fake one*/
        {
            var storage = new InMemoryStorage();
            /* this is like fake storage because normally Hangfire stores jobs in SQL Server or another persistent store
             * but this one is saving the in memory so nothing is saved permanently*/
            var connection = storage.GetConnection();

            // The job's identity is irrelevant to the branch under test - only the parameter is read - but
            // it has to be a real, resolvable job for the connection to accept parameters against it.
            var job = Job.FromExpression<IEmailService>(x => x.sendBookingCancelledDueToClosureEmailAsync(0, null));
            /* The above is basically saying, pretend this email method is the job being executed */
            var jobId = connection.CreateExpiredJob(job, new Dictionary<string, string>(), DateTime.UtcNow, TimeSpan.FromHours(1));
            /* the above reates the fake job in storage. This registers the job with hangfire.
             * The job must exist because Hangfire wont let you attach parameters to a nonexistent job*/

            // Stored as JSON, which is how Hangfire's own retry filter writes it.
            connection.SetJobParameter(jobId, "RetryCount", retryCount.ToString());
            /* This is the important part: You are manually inserting retryCount*/

            var backgroundJob = new Hangfire.BackgroundJob(jobId, job, DateTime.UtcNow);
            /* This represents the currently executing job */
            // A real token instance: PerformContext rejects null here (JobCancellationToken.Null is itself
            // a null reference, not a no-op token).
            return new PerformContext(storage, connection, backgroundJob, new JobCancellationToken(false));

            /* Now you have something that looks exactly like what Hangfire passes to your email method.
             Your production code cannot tell the difference*/
        }

        /// <summary>Retries exhausted: the job should stop throwing and flag the booking instead.</summary>
        public static PerformContext Exhausted => WithRetryCount(EmailJobPolicy.ClosureCancelRetries);
        /* Imagine your policy is cancel retrues after 3 tries
         * Then HangfireRetryContext.Exhausted creates RetryCount = 3
         so it would stop after 3 retries*/

        /// <summary>Attempts still remaining: the job must rethrow so Hangfire tries again.</summary>
        public static PerformContext RetriesRemaining => WithRetryCount(EmailJobPolicy.ClosureCancelRetries - 1);
        /* This creates retryCount = 2  so it would be allowed to retry again*/
    }
}
