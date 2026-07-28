using BarberShopAPI.Data;
using BarberShopAPI.Services;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* Boots the REAL application host (Program.cs: same middleware, same DI, same controllers) against a
     * throwaway SQL Server database, with the two outbound side effects neutralised:
     *
     *   - Hangfire: the production code enqueues emails through the STATIC BackgroundJob API, which reads
     *     JobStorage.Current. We point that at in-memory storage and stop the Hangfire *server* from
     *     starting, so enqueued jobs are recorded but never executed - which doubles as our assertion
     *     surface for "which cancellation email was sent" (see EnqueuedEmailJobs).
     *   - IEmailService: swapped for a recording fake. Belt-and-braces - with the server stopped nothing
     *     should invoke it, and if anything ever does, the test fails loudly instead of emailing a real
     *     person through Resend.
     *
     * Stripe is NOT stubbed. Tier-1 tests are built only from cash/unpaid bookings, where the production
     * code never reaches a Stripe call (it guards on StripePaymentIntentId / a COMPLETED card Payment).
     * The Stripe key below is a dummy, so any test that accidentally strays onto a Stripe path fails
     * rather than talking to a live account. */
    public class ApiFactory : WebApplicationFactory<Program>
        /* WebApplicationFactory<Program> is Mcrosoft's testinc class thats starts your entire
         * ASP.NET core application in memory*/
    {
        // A dedicated database - never the BarberShop dev DB. Respawn wipes this between tests.
        public const string ConnectionString =
            @"Server=DESKTOP-VIPM5IB\SQLEXPRESS;Database=BarberShop_Test;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
        /* Instead of using my normal database, it is using a seperate database used for tests so that my actual database is never touched */

        public FakeEmailService Emails { get; } = new();
        /* I obviosuly dont want my tests emailing customers.
         * Instead the fake might do something like: Sent.Add(email).
         Now my test can simply check:Assert.Single(factory.Emails.Sent);)*/

        static ApiFactory()/* A static constructor runs once before the first APIFactory is created.
                            * these values must eexist before Program.cs starts, so the test sets them first*/
        {
            /* Program.cs reads these straight off the environment (DotNetEnv can't help us here - it looks
             * for a .env in the working directory, which for a test run is the test bin folder). They must
             * be set before the host is built, hence the static constructor. */
            Environment.SetEnvironmentVariable("DefaultConnection", ConnectionString);
            Environment.SetEnvironmentVariable("JWT_SECRET", TestJwt.Secret);
            Environment.SetEnvironmentVariable("STRIPE_SECRET_KEY", "sk_test_dummy_key_never_used_by_tier1");
            Environment.SetEnvironmentVariable("RESEND_API_KEY", "re_dummy_key_never_used");
            // The webhook verifies every delivery against this; the tier-2 tests sign their payloads with
            // the same value so the real signature check runs rather than being bypassed.
            Environment.SetEnvironmentVariable("STRIPE_WEBHOOK_SECRET", StripeWebhookRequest.Secret);
            Environment.SetEnvironmentVariable("FRONTEND_BASE_URL", "http://localhost:5173");

            /* BookingCanceller calls the static BackgroundJob.Enqueue/Delete. With the Hangfire server
             * removed below, nothing else initialises JobStorage.Current and those calls would throw
             * "JobStorage instance has not been initialized yet". Setting it explicitly keeps the
             * production code path untouched AND makes the enqueued jobs inspectable. */
            JobStorage.Current = new InMemoryStorage();
            /* This line changes Hangfire from SQLServer to Memory.
             * Imagine writing on a whiteboard instead of a filing cabinet
             Jobs still get queued
            Nothing gets permanently stored*/
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");

            builder.ConfigureTestServices(services =>
            /* Normally app does this: program.cs -> Register services -> run
             * Testing inserts itself here does: Program.cs -> Register Services -> ConfigureTestServices -> ReplaceServices -> Run
             It's microsoft's official way of overriding dependencies only for tests*/
            {
                /* Drop Hangfire's BackgroundJobServer hosted service. Left running it would (a) process the
                 * jobs we want to assert on and (b) race Respawn by writing to its own tables mid-test.
                 * The type is internal to Hangfire, so it's matched by name. */
                var hangfireHostedServices = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                                && (d.ImplementationType?.FullName?.Contains("Hangfire") == true
                                    || d.ImplementationFactory?.Method.DeclaringType?.FullName?.Contains("Hangfire") == true))
                    .ToList();
                foreach (var descriptor in hangfireHostedServices)
                    services.Remove(descriptor);
                /* Normally the app does this: controller -> enqueue email -> Hangfire Server immediately notices -> Runs EmailService
                 * That makes testing dofficult because the job disappears almost immediately
                 Instead the tests do Controller -> Enqueue Email -> Queue sits there. Now you can inspect it*/ 

                services.RemoveAll<IEmailService>();
                services.AddSingleton<IEmailService>(Emails);
                /* Why Replace IEmailService? 
                 * Dependency injection originally contains IEmailService -> Real EmailService
                 The test changes it to IEmailService -> FakeEmailService
                So even if something accidentally tries to send mail, it cant*/
            });
        }

        /// <summary>A DbContext on the test database, independent of any request scope, for arranging and asserting.</summary>
        public BarberShopContext CreateDbContext()
            /* Instead of POST /CreateBarber,
             * the test can directly do
             *using var db = factory.CreateDbContetx()
             *db.Barbers.Add(...)
             *db.SaveChanges()
             */
        {
            var options = new DbContextOptionsBuilder<BarberShopContext>()
                .UseSqlServer(ConnectionString)
                .Options;
            return new BarberShopContext(options);
        }

        /// <summary>
        /// The email jobs the production code enqueued, as (method name, booking id) pairs. Reading
        /// Hangfire's own storage means we assert on exactly what BookingCanceller/the controllers did,
        /// with no seam or refactor in the application code.
        /// </summary>
        public IReadOnlyList<(string Method, int BookingId)> EnqueuedEmailJobs()
            /* Production code does this: BackgroundJob.Enqueue(() => SendClosureEmail(42));
             * This helper turns that into [("SendClosureEmail", 42)]#Now your test can say Assert.Contains(factory.EnqueuedEmailJobs(), x=> x.Method == "SendClosureEmail")*/
        {
            var monitoring = JobStorage.Current.GetMonitoringApi();
            return monitoring.EnqueuedJobs("default", 0, 500)
                .Select(j => (
                    Method: j.Value.Job?.Method.Name ?? "",
                    BookingId: j.Value.Job?.Args.FirstOrDefault() is int id ? id : -1))
                .ToList();
        }

        /// <summary>Clears recorded jobs/emails so each test starts from a clean slate.</summary>
        public void ResetSideEffects()
        {
            JobStorage.Current = new InMemoryStorage();
            Emails.Sent.Clear();
            /* Each test starts with an empty queue of emails*/
        }
    }

    internal static class ServiceCollectionExtensions
    {
        public static void RemoveAll<T>(this IServiceCollection services)
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
                services.Remove(descriptor);
        }
    }
}
