using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* Guards the test harness itself. If these fail, every other failure in the suite is suspect: the
     * assertions below are the assumptions the rest of the tests are built on. */
    public class InfrastructureSmokeTests : IntegrationTestBase
    {
        public InfrastructureSmokeTests(DatabaseFixture fixture) : base(fixture) { }

        [Fact]
        public async Task Schema_matches_the_model_and_shop_settings_are_seeded()
        {
            using var db = NewDb();

            var settings = await db.ShopSettings.SingleAsync(s => s.Id == 1);
            Assert.Equal(24, settings.RefundCutoffHours);
            Assert.Equal(0, settings.GraceMinutesAfterClose);

            // The filtered unique indexes are the part of the schema the cancellation/schedule flows lean
            // on hardest, so assert they actually exist rather than trusting EnsureCreated blindly.
            var filteredIndexes = await db.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sys.indexes WHERE has_filter = 1")
                .ToListAsync();
            Assert.Contains("UX_BarberSchedule_CurrentVersion", filteredIndexes);
            Assert.Contains("UX_Service_Name", filteredIndexes);
        }

        [Fact]
        public async Task Respawn_wipes_data_between_tests_but_keeps_settings()
        {
            using (var db = NewDb())
            {
                db.AddBarber();
                Assert.NotEmpty(await db.Barbers.ToListAsync());
            }

            await Fixture.ResetAsync();

            using (var db = NewDb())
            {
                Assert.Empty(await db.Barbers.ToListAsync());
                Assert.Empty(await db.Users.ToListAsync());
                Assert.Equal(24, (await db.ShopSettings.SingleAsync(s => s.Id == 1)).RefundCutoffHours);
            }
        }

        [Fact]
        public void Hangfire_enqueues_are_recorded_and_never_executed()
        {
            BackgroundJob.Enqueue<Services.IEmailService>(s => s.sendBookingConfirmationEmailAsync(4242));

            var enqueued = Factory.EnqueuedEmailJobs();
            Assert.Contains(enqueued, j => j.Method == "sendBookingConfirmationEmailAsync" && j.BookingId == 4242);

            // The Hangfire server is not running, so the job must never actually reach the email service.
            Assert.Empty(Factory.Emails.Sent);
        }

        [Fact]
        public async Task Admin_endpoints_reject_an_unauthenticated_caller()
        {
            var response = await Client.GetAsync("/api/schedules/barber/1");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Admin_jwt_cookie_authenticates_against_the_real_pipeline()
        {
            int adminId;
            int barberId;
            using (var db = NewDb())
            {
                adminId = db.AddUser(Role.ADMIN).Id;
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
            }

            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.GetAsync($"/api/schedules/barber/{barberId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
