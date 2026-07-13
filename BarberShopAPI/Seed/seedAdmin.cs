// Seed/SeedAdmin.cs
using System;
using System.Linq;
using BCrypt.Net;
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

            if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            {
                Console.WriteLine("ADMIN_EMAIL/ADMIN_PASSWORD not set in environment - skipping admin seed.");
                return;
            }

            var admin = context.Users.FirstOrDefault(u => u.Role == Role.ADMIN);

            if (admin == null)
            {
                context.Users.Add(new User
                {
                    Email = adminEmail,
                    Password = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                    Role = Role.ADMIN,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    TokenVersion = 0
                });
                context.SaveChanges();
                Console.WriteLine($"Admin user created for {adminEmail}.");
                return;
            }

            bool changed = false;

            if (!string.Equals(admin.Email, adminEmail, StringComparison.OrdinalIgnoreCase))
            {
                admin.Email = adminEmail;
                changed = true;
            }

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
    }
}
