// Seed/SeedAdmin.cs
using System;
using System.Linq;
using BCrypt.Net;
using BarberShopAPI.Common;
using BarberShopAPI.Data;
using BarberShopAPI.Models;
using Microsoft.EntityFrameworkCore;
using BarberShopAPI.Models.Enums;

namespace BarberShopAPI.Seed
{
    public static class SeedAdmin
    {
        public static void Run(BarberShopContext context)
        {
            var adminEmail = Environment.GetEnvironmentVariable("ADMIN_EMAIL");
            var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
            var adminName = Environment.GetEnvironmentVariable("ADMIN_NAME");
            /* Opt-in, and off unless it literally says "true". Plenty of shops are run by someone who
             * doesn't cut hair, and making the admin bookable by default would drop a phantom barber into
             * their customer picker on day one. Existing installs, which set neither new variable, are
             * completely unaffected. */
            var adminIsBarber = string.Equals(
                Environment.GetEnvironmentVariable("ADMIN_IS_BARBER"), "true", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            {
                Console.WriteLine("ADMIN_EMAIL/ADMIN_PASSWORD not set in environment - skipping admin seed.");
                return;
            }

            /* Every config check happens BEFORE anything is written, so a bad .env leaves the database
             * exactly as it was. The earlier version created the admin first and only then reported that it
             * had skipped the barber profile - which buries the failure under a line saying "Admin user
             * created", and the natural read of that is that everything worked. A half-applied config is
             * worth less than a refused one: fix the name, run it again, get both. */
            string firstName = "", lastName = "";
            if (adminIsBarber && !PersonName.TrySplit(adminName, out firstName, out lastName))
            {
                Console.WriteLine(
                    "ADMIN_IS_BARBER is 'true' but ADMIN_NAME is missing or invalid - nothing was changed. "
                    + "Set ADMIN_NAME to the owner's full name (no digits, each part 50 characters or fewer) "
                    + "and run this again; customers see this name on the booking page.");
                return;
            }

            /* Keyed on ADMIN_EMAIL, not "find any admin". The old lookup was
             * FirstOrDefault(u => u.Role == ADMIN), which ignored the email entirely and then overwrote
             * whatever it found - so running the seed a second time with different values renamed admin #1
             * instead of creating admin #2, and with two admins in the table it picked one arbitrarily
             * (there was no OrderBy). Keying on the email makes each run idempotent for ONE account, which
             * is what lets a shop have an owner and a manager: run it once per admin.
             *
             * The trade: changing ADMIN_EMAIL no longer RENAMES the existing admin, it creates an
             * additional one, and the old account keeps working on its old password. That's announced
             * loudly below rather than left to be discovered.
             *
             * Looked up WITHOUT the role filter on purpose. Email is uniquely indexed on User, so filtering
             * to admins here would miss a non-admin holding that address, fall through to the insert, and
             * die on the unique index with a stack trace instead of a sentence. */
            var existing = context.Users.FirstOrDefault(u => u.Email == adminEmail);

            if (existing != null && existing.Role != Role.ADMIN)
            {
                Console.WriteLine(
                    $"{adminEmail} already belongs to a {existing.Role} account - nothing was changed. "
                    + "Use a different ADMIN_EMAIL, or promote that account by hand if it really should be "
                    + "an admin.");
                return;
            }

            var admin = existing;

            if (admin == null)
            {
                var otherAdmins = context.Users.Count(u => u.Role == Role.ADMIN);
                admin = new User
                {
                    Email = adminEmail,
                    Password = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                    Role = Role.ADMIN,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    TokenVersion = 0
                };
                context.Users.Add(admin);
                context.SaveChanges();

                if (otherAdmins > 0)
                {
                    // The one case worth shouting about: you meant to correct a typo in ADMIN_EMAIL and
                    // instead you now have two admins, the old one still working on its old password.
                    Console.WriteLine(
                        $"ADDITIONAL admin created for {adminEmail}. There {(otherAdmins == 1 ? "was" : "were")} "
                        + $"already {otherAdmins} admin account(s), and none of them use this email. If you meant "
                        + "to CHANGE an existing admin's email rather than add a new one, this did not do that - "
                        + "the previous account still exists and its password still works.");
                }
                else
                {
                    Console.WriteLine($"Admin user created for {adminEmail}.");
                }
            }
            else
            {
                bool changed = false;

                // No email branch here any more: the account was found BY its email, so it already matches.

                // BCrypt.Verify(plaintext, storedHash) is the only correct way to detect
                // "does the env password differ from what's stored" - hashes are salted,
                // so re-hashing and string-comparing against the stored hash would always
                // report "different" even when the password hasn't changed.
                bool passwordMatches = !string.IsNullOrEmpty(admin.Password)
                    && BCrypt.Net.BCrypt.Verify(adminPassword, admin.Password);
                if (!passwordMatches)
                {
                    admin.Password = BCrypt.Net.BCrypt.HashPassword(adminPassword);
                    changed = true;
                }

                if (changed)
                {
                    admin.TokenVersion = (admin.TokenVersion ?? 0) + 1; // invalidate old JWTs
                    admin.UpdatedAt = DateTime.UtcNow;
                    context.SaveChanges();
                    Console.WriteLine($"Admin user updated for {adminEmail} (TokenVersion bumped to {admin.TokenVersion}).");
                }
                else
                {
                    Console.WriteLine("Admin user already matches ADMIN_EMAIL/ADMIN_PASSWORD - no changes.");
                }
            }

            // Runs on both paths above (created and already-existing), so an install that adds
            // ADMIN_IS_BARBER later picks it up on the next run rather than only on a fresh database.
            // The name was validated at the top, so by here it's known good.
            EnsureAdminIsBookable(context, admin, firstName, lastName, adminIsBarber);
        }

        /* Makes the admin a bookable chair as well as a manager, when ADMIN_IS_BARBER says so.
         *
         * Role and Barber row are two different things: the role decides what they may DO, the Barber row
         * makes them something a customer can book. Nothing in the booking, closure, schedule, webhook or
         * confirm-cash paths looks at the role - they all key off BarberId - so an admin who owns a Barber
         * row is handled correctly by every one of them without a single extra role check anywhere. That's
         * the reason this belongs here and not as a special case in the customer picker.
         *
         * ADDITIVE ONLY, on purpose. If the row already exists this leaves it completely alone: no
         * reactivating, no renaming, no touching the schedule. Setting ADMIN_IS_BARBER back to false and
         * re-running does NOT remove it either. Deactivating a barber voids the checkouts in flight on
         * their chair, flags their confirmed bookings for review and bumps TokenVersion (signing them
         * out), and a config flag has no business doing any of that to a live shop by accident - the
         * owner hides from the customer picker with the AcceptsNewBookings toggle on the Team page
         * instead, which is reversible and touches nothing else. */
        private static void EnsureAdminIsBookable(
            BarberShopContext context, User admin, string firstName, string lastName, bool adminIsBarber)
        {
            if (!adminIsBarber)
            {
                Console.WriteLine("ADMIN_IS_BARBER is not 'true' - admin will not appear as a bookable barber.");
                return;
            }

            if (context.Barbers.Any(b => b.UserId == admin.Id))
            {
                Console.WriteLine("Admin already has a barber profile - leaving it untouched.");
                return;
            }

            // The customer picker builds the barber's card from User.Name/Surname, and a seeded admin has
            // neither by default - so a nameless admin would reach customers as a blank card. Run() refuses
            // the whole seed before reaching here if the name isn't usable.
            admin.Name = firstName;
            admin.Surname = lastName;
            /* Saved WITHOUT bumping TokenVersion, unlike the email/password block above: a name is profile
             * data, not a credential, so correcting a typo in it shouldn't sign the owner out of their own
             * dashboard mid-shift. */
            admin.UpdatedAt = DateTime.UtcNow;

            var barber = new Barber
            {
                UserId = admin.Id,
                isActive = true,
                AcceptsNewBookings = true
            };
            context.Barbers.Add(barber);
            context.SaveChanges();

            // A barber with no schedule is invisible to the picker and unbookable, and DeleteVersion
            // refuses to leave anyone with zero versions - so the row is never created without one.
            var schedule = DefaultSchedule.Build(context.ShopHours.ToList());
            schedule.BarberId = barber.Id;
            context.BarberSchedules.Add(schedule);
            context.SaveChanges();

            Console.WriteLine(
                $"Admin is now a bookable barber as \"{firstName} {lastName}\" (barber id {barber.Id}), "
                + "with default hours matching the shop's opening hours. Adjust them in the Schedules page.");
        }
    }
}
