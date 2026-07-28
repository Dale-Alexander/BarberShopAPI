using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* One test database and one application host for the whole run, reset between tests by Respawn.
     *
     * Built with EnsureCreated, NOT Migrate: the migration chain does not replay onto an empty database
     * (20260315151019_addedISActiveToShopClosure has its RenameTable ShopClosure -> ShopClosures and the
     * matching PK/FK/index work commented out, so every later migration targets a table that was never
     * created). That is a deployment bug in its own right - a fresh production/CI database cannot be built
     * from the migrations today - and it should be fixed there, not papered over here.
     *
     * EnsureCreated builds the schema from the model snapshot, so everything the tests depend on is still
     * faithful: the filtered unique indexes (UX_BarberSchedule_CurrentVersion, the shop-closure guards),
     * the GETUTCDATE()/enum-string defaults, and the HasData ShopSettings row all come from
     * OnModelCreating. The only thing skipped is the raw-SQL SeedBarberSchedules back-fill, which only
     * targets barbers that already existed - the tests create their own barbers and schedules explicitly.
     *
     * Still a real SQL Server database, not SQLite: filtered indexes and GETUTCDATE() have no SQLite
     * equivalent, and the production code depends on both.
     *
     * Dropped and rebuilt on every run so a model change can never leave the tests on a stale schema. */
    public class DatabaseFixture : IAsyncLifetime
    {
        /* The entire job of a fixture is to make sure every test starts with
         * a completely clean database. A fixture is something xUnit creates once and shares between tests.
         Instead of every test doing Create database, Run migrations, Reset database, Dispose database over and over,
        xUnit creates one DatabaseFixture that all tests can use.
        IAsyncLifetime simple means xUnit will automatically call InitializeAsync
        before any tests run and DisposeAsync after they're all finished*/
        public ApiFactory Factory { get; } = new();
        private Respawner _respawner = null!;
        /*  Respawner is a library whose job is: 
         "Delete all the application data so the database looks brand new".
        Instead of manually writing DELETE FROM Payments, DELETE FROM Bookings etc,
        REspawn figures out the correct order automatically*/

        public async Task InitializeAsync()
        {
            using (var db = Factory.CreateDbContext())
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
            }
            /* the above means delete barbershop_test and create it again so every test runs from an empty database */

            await using var connection = new SqlConnection(ApiFactory.ConnectionString);
            await connection.OpenAsync();
            _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.SqlServer,
                // Leave Hangfire's own schema alone; only application tables are reset.
                SchemasToInclude = new[] { "dbo" },/* application tables are: dbo.Payments, dbo.Bookings etc,
                                                    * Hangfire creates its own schema like Hanfgire.Job, Hangfire.state etc. You dont want respawn deleting hangfire's internal tables*/
                TablesToIgnore = new Respawn.Graph.Table[]
                {
                    // Kept rather than deleted+reseeded: the row is seeded with an explicit Id via HasData,
                    // so re-inserting it would need IDENTITY_INSERT. ResetAsync restores its columns instead.
                    "ShopSettings"
                }
            });
        }

        /// <summary>Wipes all application data and returns shop settings to their seeded defaults.</summary>
        public async Task ResetAsync()/* this is the method every test calls before it starts */ 
        {
            await using (var connection = new SqlConnection(ApiFactory.ConnectionString))
            {
                await connection.OpenAsync();
                await _respawner.ResetAsync(connection);
                /* shopSettings are then reset back to their original value */
            }

            // Mirrors the HasData seed in BarberShopContext.OnModelCreating, so every test starts from the
            // same policy defaults regardless of what a previous test set.
            using var db = Factory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync(@"
                UPDATE ShopSettings
                SET BufferMin = 0,
                    DefaultAdminBookingDurationMin = 30,
                    GraceMinutesAfterClose = 0,
                    MinAdvanceBookingMinutes = 90,
                    MaxAdvanceBookingDays = 60,
                    RefundCutoffHours = 24
                WHERE Id = 1;");

            Factory.ResetSideEffects();/* Clears queued Hangfire Jobs and fake emails */
        }

        public async Task DisposeAsync()
        {
            await Factory.DisposeAsync();
        }
    }

    [CollectionDefinition(Name)]
    /* This is an xUnit feature
     * Suppose you have ScheduleTests, ClosureTests, BookingTests.
     Without a collection, xUnit might do
    Thread1: Deletes database, Thread 2: Inserts Booking, Thread 3: Deletes database, Thread 2: Test fails.
    The tests destroy each other's data. By putting every test in the same collection
    [Collection(DatabaseCollection.Name)]
    xUnit runs the tests sequentially rather than all at once*/
    public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
    {
        // Every test class joins this collection, which makes xUnit run them sequentially - they share one
        // database, so parallel execution would have them wiping each other's data mid-test.
        public const string Name = "Database collection";
    }
}
