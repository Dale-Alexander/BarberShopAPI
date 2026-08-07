using BarberShopAPI.Common;
using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Seed
{
    /* The fixture the browser tests book against: a known admin, two named barbers who work every day, and
     * three services at known prices. Deterministic on purpose - a spec that has to first go and discover
     * what services exist is a spec that fails for a different reason every time the shop's real data
     * changes.
     *
     * Separate from SeedAdmin, which exists to bootstrap a REAL shop and is deliberately additive: it never
     * deletes, never reactivates, never overwrites a barber profile. This one is the opposite - it wipes and
     * rebuilds, because a test run that inherits yesterday's cancelled bookings and deactivated barbers is
     * not a test of anything. Those two jobs want opposite behaviour, so they stay separate.
     *
     * Credentials come from the environment, never from this file - the E2E .env supplies them. */
    public static class SeedE2E
    {
        // Names and prices the specs are allowed to hard-code.
        public const string BarberOneName = "Luke Camilleri";
        public const string BarberTwoName = "Mark Bugeja";

        private static readonly (string Name, decimal Price, int DurationMin)[] Services =
        {
            ("Skin Fade", 25.50m, 30),
            ("Beard Trim", 12.00m, 20),
            ("Cut and Beard", 35.00m, 45)
        };

        public static void Run(BarberShopContext context)
        {
            /* The whole safety story for a command whose first act is to delete every row. The connection
             * string is chosen by whichever .env got loaded, and the failure mode if that goes wrong is
             * losing the development database - so refuse anything that isn't visibly a test database
             * rather than trusting the caller to have pointed us somewhere safe. */
            var database = context.Database.GetDbConnection().Database;
            if (database?.Contains("E2E", StringComparison.OrdinalIgnoreCase) != true)//asks the live connection what database are you actually attached to
            {
                Console.WriteLine(
                    $"--seed-e2e refused: connected to database '{database}', whose name does not contain "
                    + "\"E2E\". This command DELETES every row, so it only ever runs against a database named "
                    + "as a test one. Check BARBERSHOP_ENV_FILE.");
                Environment.ExitCode = 1;
                return;
                /* the 2 lines above mean "I Failed", in a way scripts can detect. 0 means success. This is how global-setup.js knows to abort */
            }

            var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL");
            var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
            if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            {
                Console.WriteLine("--seed-e2e refused: ADMIN_EMAIL and ADMIN_PASSWORD must both be set.");
                Environment.ExitCode = 1;
                return;
            }

            context.Database.EnsureCreated();
            Clear(context);
            /* EnsureCreated() builds all the tables if theyre missing, does nothing if they exist. The wipe */
            var admin = new User
            {
                Name = "E2E",
                Surname = "Admin",
                Email = adminEmail,
                Phone = "+35679000000",
                Password = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                Role = Role.ADMIN,
                TokenVersion = 0
            };
            context.Users.Add(admin);
            context.SaveChanges();

            // Barbers share the admin's password - one secret in the E2E .env rather than several, and
            // nothing here is a credential to anything real.
            AddBarber(context, BarberOneName, "barber1@e2e.test", adminPassword);
            AddBarber(context, BarberTwoName, "barber2@e2e.test", adminPassword);

            foreach (var (name, price, durationMin) in Services)
            {
                context.Services.Add(new Service
                {
                    Name = name,
                    Description = $"{name} - E2E fixture service",
                    ImageUrl = "/uploads/placeholder.png",
                    Price = price,
                    DurationMin = durationMin,
                    IsActive = true
                });
            }
            context.SaveChanges();

            Console.WriteLine(
                $"E2E fixture seeded into '{database}': admin {adminEmail}, barbers "
                + $"\"{BarberOneName}\" and \"{BarberTwoName}\" working 09:00-17:30 every day, "
                + $"{Services.Length} services.");
        }

        /* Deletes rather than drops the database, because dropping it would take the Hangfire tables with
         * it - and Hangfire connects while the app is still starting up, long before this runs, so it would
         * be pulling the floor out from under the process doing the pulling. Children first: BookingServices
         * and Payments reference Bookings, Bookings reference Barbers and Users, Shifts reference Schedules.
         *
         * ShopSettings is left alone. It arrives from HasData when the schema is created, and re-inserting
         * it here would collide on its fixed id. */
        private static void Clear(BarberShopContext context)/* Notice the order of deletion. This is due to Foreign Keys 
                                                             ShopSettings are left alone? It's created automatically with the tables(EF calls this HasData).
            Deleting and readding it would collide on its fixedID*/
        {
            context.BookingServices.RemoveRange(context.BookingServices);
            context.Payments.RemoveRange(context.Payments);
            context.SaveChanges();
            /* RemoveRange marks rows for deletion; SaveChanges actually sends it to the database. */

            context.Bookings.RemoveRange(context.Bookings);
            context.ShopClosures.RemoveRange(context.ShopClosures);
            context.SaveChanges();

            context.BarberScheduleShifts.RemoveRange(context.BarberScheduleShifts);
            context.SaveChanges();
            context.BarberSchedules.RemoveRange(context.BarberSchedules);
            context.SaveChanges();

            context.Barbers.RemoveRange(context.Barbers);
            context.SaveChanges();

            context.Services.RemoveRange(context.Services);
            context.Users.RemoveRange(context.Users);
            context.SaveChanges();

            /* Restored rather than deleted, for the same reason ShopSettings is left alone - the rows come
             * from HasData with fixed ids and re-inserting them would collide. Reset at all because these
             * hours now gate everything downstream: they cap barber shifts and draw the staff slot grid, so
             * a run that leaves them narrowed would make the NEXT run fail in the schedule editor and the
             * picker, nowhere near the spec that changed them. The barbers seeded below take their hours
             * from these, so the two can't disagree. */
            foreach (var row in context.ShopHours)
            {
                row.OpenTime = new TimeOnly(9, 0);
                row.CloseTime = new TimeOnly(17, 30);
                row.IsClosed = false;
            }
            context.SaveChanges();
        }

        private static void AddBarber(BarberShopContext context, string fullName, string email, string password)
        {
            PersonName.TrySplit(fullName, out var firstName, out var lastName);

            var user = new User
            {
                Name = firstName,
                Surname = lastName,
                Email = email,
                Phone = $"+3567900{Math.Abs(email.GetHashCode()) % 10000:0000}",
                Password = BCrypt.Net.BCrypt.HashPassword(password),
                Role = Role.BARBER,
                TokenVersion = 0
            };
            context.Users.Add(user);
            context.SaveChanges();

            var barber = new Barber
            {
                UserId = user.Id,
                isActive = true,
                AcceptsNewBookings = true,
                Bio = $"{fullName} - E2E fixture barber"
            };
            context.Barbers.Add(barber);
            context.SaveChanges();

            // Without a schedule version a barber is invisible to the picker and unbookable, so the row is
            // never created without one. Hours come from the shop's own, same as a real seeded barber -
            // which also keeps the seeded shifts inside the shifts-within-shop-hours rule.
            var schedule = DefaultSchedule.Build(context.ShopHours.ToList());
            schedule.BarberId = barber.Id;
            schedule.EffectiveFrom = ShopClock.Today.AddYears(-1);
            context.BarberSchedules.Add(schedule);
            context.SaveChanges();
        }
    }
}
