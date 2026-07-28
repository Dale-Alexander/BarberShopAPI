using BarberShopAPI.Data;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Tests
{
    /* Guards the deployment path: can a brand-new database actually be built from the migrations, and does
     * what comes out match the model the application runs against?
     *
     * This exists because it silently stopped being true. 20260315151019_addedISActiveToShopClosure had its
     * RenameTable ShopClosure -> ShopClosures commented out and applied to the dev database by hand, so
     * every later migration targeted a table that a fresh database never had. Nothing caught it, because
     * the only database anyone ran against already had the rename. The first person to find out would have
     * been whoever deployed to production or set up a new machine.
     *
     * Both halves matter. Migrating proves the chain runs; diffing against EnsureCreated proves it arrives
     * at the CURRENT model rather than some historical shape - which is what catches the next migration
     * that gets hand-patched into the dev database and left half-written here. */
    [Collection(DatabaseCollection.Name)]
    public class MigrationChainTests
    {
        // Its own scratch databases, so this never disturbs the suite's BarberShop_Test.
        private const string MigratedDb = "BarberShop_MigrationCheck_Migrated";
        private const string ModelDb = "BarberShop_MigrationCheck_Model";

        // Unused, but taking the fixture puts this class in the shared collection so it runs sequentially
        // with the rest - two databases being built at once on SQL Express is needless contention.
        public MigrationChainTests(DatabaseFixture fixture) { }

        private static BarberShopContext ContextFor(string database)
        {
            var connectionString = ApiFactory.ConnectionString.Replace("Database=BarberShop_Test", $"Database={database}");
            return new BarberShopContext(new DbContextOptionsBuilder<BarberShopContext>()
                .UseSqlServer(connectionString)
                .Options);
        }

        /* Every table, column (type / nullability / identity), index (uniqueness + filter) and foreign key.
         * Lower-cased before comparison: SQL Server identifiers are case-insensitive under the default
         * collation, and the two paths genuinely disagree on the casing of Barbers.isActive - an old
         * migration created it as IsActive while the model property is isActive. Harmless here; it would
         * only ever matter on a case-sensitive collation. */
        private const string FingerprintSql = @"
            SELECT t.name+'.'+c.name+' '+ty.name+' null='+CAST(c.is_nullable AS varchar)+' identity='+CAST(c.is_identity AS varchar) AS Value
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.name <> '__EFMigrationsHistory' AND SCHEMA_NAME(t.schema_id) = 'dbo'
            UNION ALL
            SELECT 'INDEX '+t.name+'.'+i.name+' unique='+CAST(i.is_unique AS varchar)+' filter='+ISNULL(i.filter_definition,'-')
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE i.name IS NOT NULL AND t.name <> '__EFMigrationsHistory' AND SCHEMA_NAME(t.schema_id) = 'dbo'
            UNION ALL
            SELECT 'FK '+t.name+'.'+fk.name
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo'";

        private static async Task<List<string>> FingerprintAsync(BarberShopContext db)
        {
            var rows = await db.Database.SqlQueryRaw<string>(FingerprintSql).ToListAsync();
            return rows.Select(r => r.ToLowerInvariant()).OrderBy(r => r, StringComparer.Ordinal).ToList();
        }

        [Fact]
        public async Task The_migration_chain_builds_a_fresh_database_that_matches_the_model()
        {
            using var migrated = ContextFor(MigratedDb);
            using var model = ContextFor(ModelDb);
            try
            {
                await migrated.Database.EnsureDeletedAsync();
                await model.Database.EnsureDeletedAsync();

                // The deployment path: an empty database brought up purely by replaying every migration.
                // Throws if any migration in the chain can't apply.
                await migrated.Database.MigrateAsync();

                // The reference shape, built straight from the current model.
                await model.Database.EnsureCreatedAsync();

                var migratedSchema = await FingerprintAsync(migrated);
                var modelSchema = await FingerprintAsync(model);

                // Reported as set differences so a failure names the drifting objects instead of dumping
                // both schemas and leaving you to spot it.
                var missing = modelSchema.Except(migratedSchema).ToList();
                var extra = migratedSchema.Except(modelSchema).ToList();

                Assert.True(missing.Count == 0,
                    "The model expects these, but migrating a fresh database did not produce them:\n  "
                    + string.Join("\n  ", missing));
                Assert.True(extra.Count == 0,
                    "Migrating a fresh database produced these, but the model does not expect them:\n  "
                    + string.Join("\n  ", extra));
            }
            finally
            {
                await migrated.Database.EnsureDeletedAsync();
                await model.Database.EnsureDeletedAsync();
            }
        }
    }
}
