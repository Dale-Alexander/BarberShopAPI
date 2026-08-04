using BarberShopAPI.Models.Enums;
using BarberShopAPI.Seed;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Tests
{
    /* The --seed-admin CLI path, which is how a shop gets its owner account on day one.
     *
     * Worth testing precisely because it has no UI and no request behind it: it runs once against a
     * client's real database at deploy time and nobody watches it again. The thing being pinned down is
     * that it's safe to run REPEATEDLY, which is how it's actually used - the second run must not
     * duplicate the barber row, re-create the schedule, or undo anything the shop has changed since.
     *
     * ADMIN_IS_BARBER exists because an owner who cuts hair and an owner who only manages are both normal.
     * Role and Barber row are separate ideas: the role says what they may do, the Barber row makes them a
     * chair a customer can book. */
    public class SeedAdminTests : IntegrationTestBase
    {
        public SeedAdminTests(DatabaseFixture fixture) : base(fixture) { }

        /// <summary>
        /// Runs the seed with the given environment, then restores whatever was there before. The seed
        /// reads process-wide environment variables, so leaking one would silently change how a later
        /// test behaves.
        /// </summary>
        private void RunSeed(string? email, string? password, string? name, string? isBarber)
        {
            var previous = new[] { "ADMIN_EMAIL", "ADMIN_PASSWORD", "ADMIN_NAME", "ADMIN_IS_BARBER" }
                .ToDictionary(k => k, Environment.GetEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable("ADMIN_EMAIL", email);
                Environment.SetEnvironmentVariable("ADMIN_PASSWORD", password);
                Environment.SetEnvironmentVariable("ADMIN_NAME", name);
                Environment.SetEnvironmentVariable("ADMIN_IS_BARBER", isBarber);
                using var db = NewDb();
                SeedAdmin.Run(db);
            }
            finally
            {
                foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            }
        }

        [Fact]
        public async Task Without_the_flag_the_admin_is_created_but_is_not_a_bookable_barber()
        {
            RunSeed("owner@example.test", "pw", name: null, isBarber: null);

            using var db = NewDb();
            var admin = await db.Users.SingleAsync(u => u.Role == Role.ADMIN);
            Assert.Equal("owner@example.test", admin.Email);
            // The existing behaviour for every shop that doesn't set the new variables: no phantom barber
            // appears in the customer picker.
            Assert.Empty(await db.Barbers.ToListAsync());
        }

        [Fact]
        public async Task With_the_flag_the_admin_becomes_a_bookable_barber_with_a_schedule()
        {
            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "true");

            using var db = NewDb();
            var admin = await db.Users.SingleAsync(u => u.Role == Role.ADMIN);
            // The name matters: the customer picker renders the card from Name/Surname, so a barber
            // without one shows up to customers as a blank card.
            Assert.Equal("Joe", admin.Name);
            Assert.Equal("Borg", admin.Surname);

            var barber = await db.Barbers.SingleAsync();
            Assert.Equal(admin.Id, barber.UserId);
            Assert.True(barber.isActive);
            Assert.True(barber.AcceptsNewBookings);

            // Role is untouched - they are still an ADMIN, so every admin-only endpoint still lets them in.
            Assert.Equal(Role.ADMIN, admin.Role);

            // A barber with no schedule is invisible to the picker and unbookable.
            var schedule = await db.BarberSchedules.Include(s => s.Shifts).SingleAsync(s => s.BarberId == barber.Id);
            Assert.Null(schedule.EffectiveTo);
            Assert.Equal(7, schedule.Shifts.Count);
        }

        /* The one the whole design rests on: the client's deploy runs this every time, not once. */
        [Fact]
        public async Task Running_it_again_does_not_duplicate_the_barber_or_the_schedule()
        {
            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "true");
            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "true");
            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "true");

            using var db = NewDb();
            Assert.Single(await db.Users.Where(u => u.Role == Role.ADMIN).ToListAsync());
            var barber = Assert.Single(await db.Barbers.ToListAsync());
            Assert.Single(await db.BarberSchedules.Where(s => s.BarberId == barber.Id).ToListAsync());
        }

        /* Turning the flag off must NOT deactivate the barber: that would cancel their future bookings and
         * bump TokenVersion. A config flag has no business doing that to a live shop. Hiding from the
         * customer picker is the AcceptsNewBookings toggle's job, which the seed also leaves alone. */
        [Fact]
        public async Task Turning_the_flag_off_later_leaves_an_existing_barber_profile_alone()
        {
            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "true");
            using (var db = NewDb())
            {
                // Stand in for the owner having since hidden themselves from customers by hand.
                var b = await db.Barbers.SingleAsync();
                b.AcceptsNewBookings = false;
                await db.SaveChangesAsync();
            }

            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "false");

            using var assertDb = NewDb();
            var barber = await assertDb.Barbers.SingleAsync();
            Assert.True(barber.isActive);              // not deactivated
            Assert.False(barber.AcceptsNewBookings);   // their own choice not overwritten
        }

        /* A blank name would reach real customers as an unnamed card on the booking page. The whole run is
         * refused rather than half-applied: an earlier version created the admin first and only then
         * reported it had skipped the barber profile, which buries the failure under a line saying "Admin
         * user created" - and the natural read of that is that everything worked. Nothing is written, so
         * you fix ADMIN_NAME, run it again, and get both. */
        [Fact]
        public async Task A_missing_or_invalid_name_refuses_the_whole_run_and_changes_nothing()
        {
            RunSeed("owner@example.test", "pw", name: null, isBarber: "true");
            using (var db = NewDb())
            {
                Assert.Empty(await db.Barbers.ToListAsync());
                Assert.Empty(await db.Users.Where(u => u.Role == Role.ADMIN).ToListAsync());
            }

            RunSeed("owner@example.test", "pw", name: "Joe 123", isBarber: "true");
            using (var db = NewDb())
            {
                Assert.Empty(await db.Barbers.ToListAsync());
                Assert.Empty(await db.Users.Where(u => u.Role == Role.ADMIN).ToListAsync());
            }

            // Fixing the name and re-running gets you everything, with nothing left over from the refusals.
            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "true");
            using var assertDb = NewDb();
            Assert.Single(await assertDb.Users.Where(u => u.Role == Role.ADMIN).ToListAsync());
            Assert.Single(await assertDb.Barbers.ToListAsync());
        }

        /* The refusal is scoped to the barber profile being requested. A shop that doesn't set
         * ADMIN_IS_BARBER has no reason to supply a name, and must still get its admin account. */
        [Fact]
        public async Task A_missing_name_is_fine_when_the_admin_is_not_a_barber()
        {
            RunSeed("owner@example.test", "pw", name: null, isBarber: "false");

            using var db = NewDb();
            Assert.Single(await db.Users.Where(u => u.Role == Role.ADMIN).ToListAsync());
            Assert.Empty(await db.Barbers.ToListAsync());
        }

        /* The point of keying on ADMIN_EMAIL: a shop with an owner AND a manager runs the seed once per
         * person. The old lookup ("find any admin") would have found the owner on the second run and
         * renamed them instead. */
        [Fact]
        public async Task Running_it_with_a_different_email_creates_a_second_admin()
        {
            RunSeed("owner@example.test", "pw1", name: "Joe Borg", isBarber: "true");
            RunSeed("manager@example.test", "pw2", name: null, isBarber: "false");

            using var db = NewDb();
            var admins = await db.Users.Where(u => u.Role == Role.ADMIN).OrderBy(u => u.Email).ToListAsync();
            Assert.Equal(2, admins.Count);
            Assert.Equal("manager@example.test", admins[0].Email);
            Assert.Equal("owner@example.test", admins[1].Email);

            // The flag is read per run, so the two admins can differ: the owner cuts hair, the manager
            // doesn't. Only one chair exists, and it belongs to the owner.
            var barber = Assert.Single(await db.Barbers.ToListAsync());
            Assert.Equal(admins[1].Id, barber.UserId);
        }

        /* Each admin stays independently idempotent - re-running for one must not disturb the other. */
        [Fact]
        public async Task Re_running_for_one_admin_leaves_the_other_alone()
        {
            RunSeed("owner@example.test", "pw1", name: "Joe Borg", isBarber: "true");
            RunSeed("manager@example.test", "pw2", name: null, isBarber: "false");
            string ownerHash;
            using (var db = NewDb())
                ownerHash = (await db.Users.SingleAsync(u => u.Email == "owner@example.test")).Password!;

            // Change only the manager's password.
            RunSeed("manager@example.test", "pw2-changed", name: null, isBarber: "false");

            using var assertDb = NewDb();
            Assert.Equal(2, await assertDb.Users.CountAsync(u => u.Role == Role.ADMIN));
            var owner = await assertDb.Users.SingleAsync(u => u.Email == "owner@example.test");
            Assert.Equal(ownerHash, owner.Password);          // untouched
            Assert.Equal(0, owner.TokenVersion ?? 0);         // and not signed out

            var manager = await assertDb.Users.SingleAsync(u => u.Email == "manager@example.test");
            Assert.True(BCrypt.Net.BCrypt.Verify("pw2-changed", manager.Password));
            Assert.Equal(1, manager.TokenVersion ?? 0);       // their old session is dead
        }

        /* Email is uniquely indexed on User. Filtering the lookup to admins only would miss a non-admin
         * holding that address, fall through to the insert, and die on the index with a stack trace. */
        [Fact]
        public async Task An_email_belonging_to_a_non_admin_is_refused_cleanly()
        {
            int barberUserId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                var u = await db.Users.SingleAsync(x => x.Id == barber.UserId);
                u.Email = "taken@example.test";
                await db.SaveChangesAsync();
                barberUserId = u.Id;
            }

            RunSeed("taken@example.test", "pw", name: "Joe Borg", isBarber: "true");

            using var assertDb = NewDb();
            Assert.Empty(await assertDb.Users.Where(u => u.Role == Role.ADMIN).ToListAsync());
            // The existing account is left exactly as it was - not promoted, not renamed.
            Assert.Equal(Role.BARBER, (await assertDb.Users.SingleAsync(u => u.Id == barberUserId)).Role);
        }

        /* An install that upgrades: the admin already exists from an earlier deploy, and the new variables
         * are added later. The barber profile has to appear on the NEXT run, not only on a fresh database.
         */
        [Fact]
        public async Task An_existing_admin_gains_a_barber_profile_when_the_flag_is_added_later()
        {
            RunSeed("owner@example.test", "pw", name: null, isBarber: null);
            using (var db = NewDb()) Assert.Empty(await db.Barbers.ToListAsync());

            RunSeed("owner@example.test", "pw", name: "Joe Borg", isBarber: "true");

            using var assertDb = NewDb();
            var admin = await assertDb.Users.SingleAsync(u => u.Role == Role.ADMIN);
            var barber = await assertDb.Barbers.SingleAsync();
            Assert.Equal(admin.Id, barber.UserId);
        }
    }
}
