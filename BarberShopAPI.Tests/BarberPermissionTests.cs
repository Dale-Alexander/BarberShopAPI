using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* What a BARBER may and may not do to closures and schedules.
     *
     * The rule is that setting the shop's time is the manager's job. A barber reads the two things that
     * describe his own working life - the closures that affect him, and the shifts he's been given - and
     * changes neither. He can't book his own time off any more; he asks.
     *
     * These are worth pinning down in tests rather than trusting to the [Authorize] attributes because the
     * frontend hides every one of these controls from a barber, so a regression here is invisible in the UI
     * and only shows up as a barber discovering they can curl their way into another chair's hours.
     *
     * The IDOR cases (reading a COLLEAGUE's data) matter most: the barberId comes straight off the URL, so
     * without the ownership check any barber could read any other barber's schedule by editing a number. */
    public class BarberPermissionTests : IntegrationTestBase
    {
        public BarberPermissionTests(DatabaseFixture fixture) : base(fixture) { }

        private static object ClosureBody(DateOnly startDate, int? barberId = null) => new
        {
            barberId,
            startDate,
            endDate = (DateOnly?)null,
            isFullDay = true,
            startTime = (TimeOnly?)null,
            endTime = (TimeOnly?)null,
            reason = "Test closure",
            confirmCancelBookings = false
        };

        private static object ShiftsBody() => new
        {
            shifts = new[] { new { dayOfWeek = 1, startTime = "09:00:00", endTime = "17:00:00" } },
            confirmOrphaned = false
        };

        /// <summary>A barber with a schedule, authenticated as themselves. Returns their Barber.Id.</summary>
        private int AuthenticateAsBarber()
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            Client.Authenticate(barber.UserId, Role.BARBER, tokenVersion: 0);
            return barber.Id;
        }

        // ── Closures: read yes, write no ────────────────────────────────────────────────────────────

        [Fact]
        public async Task A_barber_cannot_create_a_closure()
        {
            var barberId = AuthenticateAsBarber();

            var response = await Client.PostAsync("/api/dates", Body(ClosureBody(ShopClock.Today.AddDays(7), barberId)));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var db = NewDb();
            Assert.Empty(await db.ShopClosures.ToListAsync());
        }

        /* The one that would be easiest to get wrong. The old code let a barber POST and force-set the
         * closure to their own id, so "creating a closure for myself" was the SUPPORTED path, not an
         * attack. Leaving barberId null now doesn't smuggle it through as a shop-wide closure either -
         * the role is refused before the body is ever looked at. */
        [Fact]
        public async Task A_barber_cannot_create_a_shop_wide_closure_by_omitting_the_barber_id()
        {
            AuthenticateAsBarber();

            var response = await Client.PostAsync("/api/dates", Body(ClosureBody(ShopClock.Today.AddDays(7))));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var db = NewDb();
            Assert.Empty(await db.ShopClosures.ToListAsync());
        }

        [Fact]
        public async Task A_barber_cannot_delete_even_their_own_closure()
        {
            var barberId = AuthenticateAsBarber();
            int closureId;
            using (var db = NewDb())
            {
                closureId = db.AddClosure(ShopClock.Today.AddDays(7), barberId: barberId).Id;
            }

            var response = await Client.PatchAsync($"/api/dates/delete/{closureId}", null);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var assertDb = NewDb();
            Assert.True((await assertDb.ShopClosures.SingleAsync(c => c.Id == closureId)).IsActive);
        }

        /* The barber's calendar answers "am I working that day", so it has to include the closures that
         * shut the whole shop around him - not just his own time off. IsShopWide is what lets the client
         * render the two differently. */
        [Fact]
        public async Task A_barbers_closure_read_includes_shop_wide_closures_and_marks_them()
        {
            var barberId = AuthenticateAsBarber();
            using (var db = NewDb())
            {
                db.AddClosure(ShopClock.Today.AddDays(7), barberId: barberId);   // his own day off
                db.AddClosure(ShopClock.Today.AddDays(8), barberId: null);        // shop-wide holiday
            }

            var response = await Client.GetAsync($"/api/dates/barber/{barberId}/closures");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var closures = (await ReadJson(response)).EnumerateArray().ToList();
            Assert.Equal(2, closures.Count);
            Assert.Single(closures, c => c.GetProperty("isShopWide").GetBoolean());
            Assert.Single(closures, c => !c.GetProperty("isShopWide").GetBoolean());
        }

        [Fact]
        public async Task A_barber_cannot_read_another_barbers_closures()
        {
            AuthenticateAsBarber();
            int otherBarberId;
            using (var db = NewDb()) { otherBarberId = db.AddBarber().Id; }

            var response = await Client.GetAsync($"/api/dates/barber/{otherBarberId}/closures");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // ── Schedules: read your own, write nothing ─────────────────────────────────────────────────

        [Fact]
        public async Task A_barber_can_read_their_own_schedule()
        {
            var barberId = AuthenticateAsBarber();

            var response = await Client.GetAsync($"/api/schedules/barber/{barberId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var versions = (await ReadJson(response)).EnumerateArray().ToList();
            Assert.Single(versions);
            Assert.NotEmpty(versions[0].GetProperty("shifts").EnumerateArray());
        }

        [Fact]
        public async Task A_barber_cannot_read_another_barbers_schedule()
        {
            AuthenticateAsBarber();
            int otherBarberId;
            using (var db = NewDb())
            {
                var other = db.AddBarber();
                db.AddSchedule(other.Id, ShopClock.Today.AddDays(-30));
                otherBarberId = other.Id;
            }

            var response = await Client.GetAsync($"/api/schedules/barber/{otherBarberId}");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task A_barber_cannot_edit_the_shifts_on_their_own_schedule()
        {
            var barberId = AuthenticateAsBarber();
            int versionId;
            using (var db = NewDb())
            {
                versionId = await db.BarberSchedules.Where(s => s.BarberId == barberId).Select(s => s.Id).SingleAsync();
            }

            var response = await Client.PutAsync($"/api/schedules/version/{versionId}", Body(ShiftsBody()));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            // The seeded 7-day week is untouched: nothing was replaced by the single Monday shift above.
            using var assertDb = NewDb();
            var shifts = await assertDb.BarberScheduleShifts.Where(s => s.BarberScheduleId == versionId).ToListAsync();
            Assert.Equal(7, shifts.Count);
        }

        [Fact]
        public async Task A_barber_cannot_create_a_schedule_version()
        {
            var barberId = AuthenticateAsBarber();

            var response = await Client.PostAsync($"/api/schedules/barber/{barberId}", Body(new
            {
                effectiveFrom = ShopClock.Today.AddDays(30),
                shifts = new[] { new { dayOfWeek = 1, startTime = "09:00:00", endTime = "17:00:00" } },
                confirmOrphaned = false
            }));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var db = NewDb();
            Assert.Single(await db.BarberSchedules.Where(s => s.BarberId == barberId).ToListAsync());
        }

        [Fact]
        public async Task A_barber_cannot_remove_a_schedule_version()
        {
            var barberId = AuthenticateAsBarber();
            int versionId;
            using (var db = NewDb())
            {
                // A second version, so the "never the only one" rule isn't what's doing the refusing.
                db.BarberSchedules.Single(s => s.BarberId == barberId).EffectiveTo = ShopClock.Today.AddDays(9);
                db.SaveChanges();
                versionId = db.AddSchedule(barberId, ShopClock.Today.AddDays(10)).Id;
            }

            var response = await Client.DeleteAsync($"/api/schedules/version/{versionId}");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var assertDb = NewDb();
            Assert.Equal(2, await assertDb.BarberSchedules.CountAsync(s => s.BarberId == barberId));
        }

        // ── The admin keeps all of it ───────────────────────────────────────────────────────────────

        /* The counterweight to every test above: this lockdown must not have cost the admin anything. If
         * an over-broad attribute change ever locks the manager out too, the tests above would all still
         * pass and the app would simply stop working. */
        [Fact]
        public async Task An_admin_can_still_create_a_closure_for_any_barber_and_read_any_schedule()
        {
            int barberId;
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
            }

            var created = await Client.PostAsync("/api/dates", Body(ClosureBody(ShopClock.Today.AddDays(7), barberId)));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            var schedule = await Client.GetAsync($"/api/schedules/barber/{barberId}");
            Assert.Equal(HttpStatusCode.OK, schedule.StatusCode);

            var closureId = (await ReadJson(created)).GetProperty("id").GetInt32();
            var deleted = await Client.PatchAsync($"/api/dates/delete/{closureId}", null);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }
    }
}
