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
        public DbSet<ShopHours> ShopHours { get; set; }
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

            /* Booking already points at User once (the customer). OverriddenBy is a SECOND path to the same
             * table, so it has to be spelled out: EF would otherwise pair it with Booking.User and, worse,
             * SQL Server refuses two cascade paths from one table. Restrict, not cascade - deleting the
             * staff member who authorised an override must never take the customer's booking with it. */
            modelBuilder.Entity<Booking>()
                .HasOne(b => b.OverriddenBy)
                .WithMany()
                .HasForeignKey(b => b.OverriddenByUserId)
                .OnDelete(DeleteBehavior.Restrict);

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

            /* Replaces CK_Payments_Amount_Max400, which bounded only the top: it read [Amount] <= 400, so a
             * negative amount satisfied it and could have landed in the revenue figures as a credit nobody
             * gave. The three API paths that set an amount (confirm-cash, mark-cash-paid, edit-amount) all
             * already refuse <= 0, so nothing could reach it - this makes the column say what those three say.
             *
             * NULL stays legal and must: an admin booking is created with no amount at all and captures one
             * later at mark-paid time, so a large share of live rows are legitimately NULL. */
            payment.ToTable(t => t.HasCheckConstraint(
                "CK_Payments_Amount", "[Amount] IS NULL OR ([Amount] > 0 AND [Amount] <= 400)"));

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

            // Existing barbers must stay bookable when this column is added, and every new barber starts
            // bookable - the admin opts them out explicitly.
            barber.Property(b => b.AcceptsNewBookings)
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

            /* The two rules SchedulesController.ValidateShifts already enforces on every write, mirrored on the
             * column so they hold for seeds and any future writer too. Deliberately NOT the overlap rule from
             * that same method - that one is about a set of rows on the same day, which a check constraint
             * (one row at a time) cannot see.
             *
             * Start < end also means no overnight shift, which the app has never supported: ValidateShifts has
             * always required it, and a shift crossing midnight would break ShiftsForDate's ordering and the
             * grace-at-close rule. If overnight shifts are ever wanted, this constraint is one of the places
             * that has to change. */
            barberScheduleShift.ToTable(t =>
            {
                t.HasCheckConstraint("CK_BarberScheduleShifts_StartBeforeEnd", "[StartTime] < [EndTime]");
                t.HasCheckConstraint("CK_BarberScheduleShifts_DayOfWeek", "[DayOfWeek] BETWEEN 0 AND 6");
            });

            barberScheduleShift.HasOne(sh => sh.Schedule)
                               .WithMany(s => s.Shifts)
                               .HasForeignKey(sh => sh.BarberScheduleId)
                               .OnDelete(DeleteBehavior.Cascade);

            barberScheduleShift.HasIndex(sh => sh.BarberScheduleId);

            /* The same bounds UpdateShopSettingsViewModel already enforces with [Range]/[AllowedValues], kept
             * here as well so they hold for every writer rather than only the settings endpoint. Seeds,
             * migrations, the test fixture's raw UPDATE and any future admin tooling all reach this table
             * without passing through that view model, and these are policy numbers the whole booking engine
             * reads - a slot step that doesn't divide 60 walks the picker off the hour, a negative grace
             * silently shortens every day. The annotations stay: they give the admin a field-level message
             * instead of a 500, and this is the backstop for everything that isn't that form.
             *
             * One constraint per column rather than one composite, so a violation names the column it broke. */
            modelBuilder.Entity<ShopSettings>().ToTable(t =>
            {
                t.HasCheckConstraint("CK_ShopSettings_BufferMin", "[BufferMin] BETWEEN 0 AND 120");
                t.HasCheckConstraint("CK_ShopSettings_DefaultAdminBookingDurationMin", "[DefaultAdminBookingDurationMin] BETWEEN 5 AND 240");
                t.HasCheckConstraint("CK_ShopSettings_GraceMinutesAfterClose", "[GraceMinutesAfterClose] BETWEEN 0 AND 120");
                t.HasCheckConstraint("CK_ShopSettings_MinAdvanceBookingMinutes", "[MinAdvanceBookingMinutes] BETWEEN 0 AND 1440");
                t.HasCheckConstraint("CK_ShopSettings_MaxAdvanceBookingDays", "[MaxAdvanceBookingDays] BETWEEN 1 AND 365");
                t.HasCheckConstraint("CK_ShopSettings_RefundCutoffHours", "[RefundCutoffHours] BETWEEN 0 AND 168");
                // Not a range: the value has to divide 60 or the picker's grid walks off the hour.
                t.HasCheckConstraint("CK_ShopSettings_SlotStepMin", "[SlotStepMin] IN (5, 10, 15, 20, 30)");
            });

            // Seed the single shop-settings row. Buffer defaults to 0 so behaviour is unchanged
            // until an admin sets it from the dashboard; admin bookings default to 30 min; grace of
            // 0 keeps the strict "must finish by closing" rule.
            modelBuilder.Entity<ShopSettings>().HasData(
                new ShopSettings { Id = 1, BufferMin = 0, DefaultAdminBookingDurationMin = 30, GraceMinutesAfterClose = 0, MinAdvanceBookingMinutes = 90, MaxAdvanceBookingDays = 60, RefundCutoffHours = 24, SlotStepMin = 30 });

            /* Mirrors ShopHoursDayViewModel.Validate, INCLUDING its exemption for a closed day - and the
             * exemption is the whole reason this isn't a plain OpenTime < CloseTime. A closed day's times are
             * ignored by every reader and kept only so reopening restores what was there before, so they are
             * allowed to be junk; constraining them would make a day with junk times impossible to close,
             * which is exactly the state an admin closes a day to get out of. */
            modelBuilder.Entity<ShopHours>().ToTable(t => t.HasCheckConstraint(
                "CK_ShopHours_OpenBeforeClose", "[IsClosed] = 1 OR [OpenTime] < [CloseTime]"));

            /* All seven days open 09:00-17:30 - the same hours DefaultSchedule gives a new barber and the
             * same ones the day-one schedule seed used. Deliberately permissive rather than realistic (no
             * closed Sunday, even though the shop will want one): these hours become a CEILING over every
             * existing barber shift the moment they exist, so seeding anything narrower than the shifts
             * already in the database would put every barber in violation on day one and lock the admin out
             * of the schedule editor until they fixed hours they never set. The migration widens these
             * further to cover whatever the live data actually holds; narrowing them is the admin's job,
             * and doing it by hand is what surfaces the barbers who need adjusting first. */
            for (int d = 0; d < 7; d++)
                modelBuilder.Entity<ShopHours>().HasData(new ShopHours
                {
                    Id = d + 1,
                    DayOfWeek = (DayOfWeek)d,
                    OpenTime = new TimeOnly(9, 0),
                    CloseTime = new TimeOnly(17, 30),
                    IsClosed = false
                });

            base.OnModelCreating(modelBuilder);
        }
        /* onModelCreating is a place to configure EF Core beyong default conventions
         * You are telling EF Core: In the bookings table, create a unique index on 
         * (BarberId, StartDateTime)
         */

    }
}
