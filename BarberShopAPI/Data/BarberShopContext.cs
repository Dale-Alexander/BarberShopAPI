using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Threading;

namespace BarberShopAPI.Data
{
    public class BarberShopContext : DbContext
    // DbContext is Ef Core's main class for talking to the database.
    //It acts like a bridge between my C# models and the database tables
    //Each instance of DbContext allows you to query data: context.Users.ToList(), CRUD etc
    {
        public BarberShopContext(DbContextOptions<BarberShopContext> options) : base(options)
        {

        }
        /* 
         *DbContextOptions contains configuration for the database (like the connection string, provider type)
         *You pass these options from Program.cs when you configure EF Core:
         *builder.Services.AddDbContext<BarberShopContext>(options =>
         options.UseNpgsql(connectionString));
         This lets EfCore which database to connect to and how
         */
        public DbSet<User> Users { get; set; }
        public DbSet<Booking> Bookings { get; set; }
        public DbSet<Barber> Barbers { get; set; }
        public DbSet<Service> Services { get; set; }
        public DbSet<BookingService> BookingServices { get; set; }
        public DbSet<Payment> Payments { get; set; }
        public DbSet<ShopClosure> ShopClosures { get; set; }
        public DbSet<ShopSettings> ShopSettings { get; set; }
        public DbSet<BarberSchedule> BarberSchedules { get; set; }
        public DbSet<BarberScheduleShift> BarberScheduleShifts { get; set; }

        /* 
         *Each DbSet corresponds to a table in the database. EF Core will
         *map my model class User to a Users table automatically
         *You use them like this: context.Bookings.ToList();
         */
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {

            var shopClosure = modelBuilder.Entity<ShopClosure>();

            shopClosure.HasIndex(c => new { c.StartDate, c.EndDate })
      .IsUnique()
      .HasFilter("[BarberId] IS NULL AND [IsFullDay] = 1 AND [IsActive] = 1"); // shop-wide full day

            shopClosure.HasIndex(c => new { c.BarberId, c.StartDate, c.EndDate })
                   .IsUnique()
                   .HasFilter("[BarberId] IS NOT NULL AND [IsFullDay] = 1 AND [IsActive] = 1"); // barber full day
            //the time overlapping is considered in the backend
            shopClosure.Property(sc => sc.IsActive).HasDefaultValue(true);

            modelBuilder.Entity<Booking>().HasIndex(b => new { b.BarberId, b.StartDateTime }).IsUnique();

            var user = modelBuilder.Entity<User>();

            // Map enum Role as string and set SQL default
            user.Property(u => u.Role)
                .HasConversion<string>();


            // Default timestamps
            user.Property(u => u.CreatedAt)
                .HasDefaultValueSql("GETUTCDATE()");

            user.Property(u => u.UpdatedAt)
                .HasDefaultValueSql("GETUTCDATE()");

            // Column lengths
            user.Property(u => u.Name).HasMaxLength(50);
            user.Property(u => u.Surname).HasMaxLength(50);
            user.Property(u => u.Phone).HasMaxLength(20);
            user.Property(u => u.Email).HasMaxLength(100);

            // **Unique constraints**
            user.HasIndex(u => u.Phone)
                .IsUnique()
                .HasDatabaseName("UX_User_Phone");

            user.HasIndex(u => u.Email)
                .IsUnique()
                .HasDatabaseName("UX_User_Email");

            var service = modelBuilder.Entity<Service>();

            // Defaults
            service.Property(s => s.IsActive)
                   .HasDefaultValue(true);

            // Optional: Column lengths
            service.Property(s => s.Name).HasMaxLength(100);
            service.Property(s => s.Description).HasMaxLength(500);
            service.Property(s => s.ImageUrl).HasMaxLength(1000);

            // Unique service names, but only among ACTIVE services (mirrors the barber soft-delete
            // pattern): a name freed up by deactivating a service can be reused by a new one.
            service.HasIndex(s => s.Name)
                   .IsUnique()
                   .HasFilter("[IsActive] = 1")
                   .HasDatabaseName("UX_Service_Name");

            var payment = modelBuilder.Entity<Payment>();

            payment.HasIndex(p => p.BookingId)
       .IsUnique()
       .HasDatabaseName("UX_Payment_BookingId");

            payment.HasIndex(p => p.StripePaymentIntentId)
                   .IsUnique()
                   .HasDatabaseName("UX_Payment_StripePaymentIntentId");

            // Enum mappings as strings


            payment.Property(p => p.Status)
                   .HasConversion(
                       v => v.ToString(),
                       v => (PaymentStatus)Enum.Parse(typeof(PaymentStatus), v)
                   )
                   .HasDefaultValueSql("'PENDING'");

            // Timestamps defaults
            payment.Property(p => p.CreatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");

            payment.Property(p => p.UpdatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");

            payment.Property(p => p.Method)
       .HasConversion(
           v => v.ToString(),
           v => (PaymentMethod)Enum.Parse(typeof(PaymentMethod), v)
       )
       .HasDefaultValueSql("'CASH'");

            var bookingService = modelBuilder.Entity<BookingService>();

            // Enum mapping

            bookingService.Property(bs => bs.Status)
                          .HasConversion(
                              v => v.ToString(),
                              v => (BookingServiceStatus)Enum.Parse(typeof(BookingServiceStatus), v)
                          )
                          .HasDefaultValueSql("'ACTIVE'");

            // UpdatedAt default
            bookingService.Property(bs => bs.UpdatedAt)
                          .HasDefaultValueSql("GETUTCDATE()");
            var booking = modelBuilder.Entity<Booking>();
            booking.Property(b => b.Status)
                   .HasConversion(
                       v => v.ToString(),
                       v => (BookingStatus)Enum.Parse(typeof(BookingStatus), v)
                   )
                   .HasDefaultValueSql("'PENDING'");

            var barber = modelBuilder.Entity<Barber>();
            // In your DbContext OnModelCreating:

            // Timestamps
            booking.Property(b => b.CreatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");
            booking.Property(b => b.UpdatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");

            barber.HasIndex(b => b.UserId)
       .IsUnique()
       .HasDatabaseName("UX_Barber_UserId");

            // Default value for IsActive
            barber.Property(b => b.isActive)
                   .HasDefaultValue(true);

            // Default timestamps
            barber.Property(b => b.CreatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");
            barber.Property(b => b.UpdatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");

            var barberSchedule = modelBuilder.Entity<BarberSchedule>();

            barberSchedule.HasOne(s => s.Barber)
                          .WithMany()
                          .HasForeignKey(s => s.BarberId)
                          .OnDelete(DeleteBehavior.Cascade);

            // Fast lookup of the version effective on a given date.
            barberSchedule.HasIndex(s => new { s.BarberId, s.EffectiveFrom });

            // At most one open-ended (current) version per barber - same filtered-index
            // technique as the closure uniqueness guards above.
            barberSchedule.HasIndex(s => s.BarberId)
                          .IsUnique()
                          .HasFilter("[EffectiveTo] IS NULL")
                          .HasDatabaseName("UX_BarberSchedule_CurrentVersion");

            var barberScheduleShift = modelBuilder.Entity<BarberScheduleShift>();

            barberScheduleShift.HasOne(sh => sh.Schedule)
                               .WithMany(s => s.Shifts)
                               .HasForeignKey(sh => sh.BarberScheduleId)
                               .OnDelete(DeleteBehavior.Cascade);

            barberScheduleShift.HasIndex(sh => sh.BarberScheduleId);

            // Seed the single shop-settings row. Buffer defaults to 0 so behaviour is unchanged
            // until an admin sets it from the dashboard; admin bookings default to 30 min; grace of
            // 0 keeps the strict "must finish by closing" rule.
            modelBuilder.Entity<ShopSettings>().HasData(
                new ShopSettings { Id = 1, BufferMin = 0, DefaultAdminBookingDurationMin = 30, GraceMinutesAfterClose = 0, MinAdvanceBookingMinutes = 90, MaxAdvanceBookingDays = 60, RefundCutoffHours = 24 });

            base.OnModelCreating(modelBuilder);
        }
        /* onModelCreating is a place to configure EF Core beyong default conventions
         * You are telling EF Core: In the bookings table, create a unique index on 
         * (BarberId, StartDateTime)
         */

    }
}
