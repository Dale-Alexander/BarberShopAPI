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
            if (context.Users.Any(u => u.Email == "swazzed@gmail.com"))
            {
                Console.WriteLine("Admin user already exists!");
                return;
            }

            var adminUser = new User
            {
                Email = "swazzed@gmail.com",
                Password = BCrypt.Net.BCrypt.HashPassword("Swazzy2929"),
                Role = Role.ADMIN,
                CreatedAt = DateTime.Now,
                TokenVersion = 0
            };

            context.Users.Add(adminUser);
            context.SaveChanges();
            Console.WriteLine("Admin user created successfully!");
        }
    }
}
