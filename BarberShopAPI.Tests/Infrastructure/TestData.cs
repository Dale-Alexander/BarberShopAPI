using BarberShopAPI.Common;
using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* Builders for the rows the cancellation/orphaning flows read. Deliberately writes through EF (not raw
     * SQL) so the same defaults, conversions and constraints the application sees apply here too.
     *
     * Times are Malta wall-clock, matching Booking.StartDateTime and ShopClock.Now - never DateTime.Now,
     * which would be the host machine's timezone and would silently shift bookings across the
     * "is it in the future?" boundary on a differently-configured machine. */
    public static class TestData
    {
        private static int _seq;
        private static int Next() => Interlocked.Increment(ref _seq);
        /* Every time next is called it returns a unique number. That number is used to generate unique data
         * For example: Email = $"user{n}@example.test" becomes
         user1@exmaple.test
         user2@exmaple.test etc
        */

        /// <summary>A Malta wall-clock instant `daysAhead` days from today at the given time.</summary>
        public static DateTime FutureAt(int daysAhead, int hour, int minute = 0) =>
            ShopClock.Today.AddDays(daysAhead).ToDateTime(new TimeOnly(hour, minute));

        /* TestData.FutureAt(7, 10) means "7 days from today at 10:00 AM" */
        /// <summary>A future date that lands on a specific weekday, so shift-per-weekday rules are exercised deliberately.</summary>
        public static DateOnly NextWeekday(DayOfWeek day, int minDaysAhead = 7)
        {
            var date = ShopClock.Today.AddDays(minDaysAhead);
            while (date.DayOfWeek != day) date = date.AddDays(1);
            return date;
        }

        public static User AddUser(this BarberShopContext db, Role role = Role.CUSTOMER, string? name = null, string? email = null)
        {
            var n = Next();
            var user = new User
            {
                Name = name ?? $"Test{n}",
                Surname = "Person",
                Phone = $"+35679{n:000000}",
                Email = email ?? $"user{n}@example.test",
                Role = role,
                Password = "not-a-real-hash",
                TokenVersion = 0
            };
            db.Users.Add(user);
            db.SaveChanges();
            return user;
        }

        /// <summary>A barber plus their linked user account, with no schedule - callers add the version they need.</summary>
        public static Barber AddBarber(this BarberShopContext db, bool isActive = true)
        {
            var user = db.AddUser(Role.BARBER);
            var barber = new Barber { UserId = user.Id, isActive = isActive, Bio = "Test barber" };
            db.Barbers.Add(barber);
            db.SaveChanges();
            return barber;
        }

        /// <summary>
        /// A schedule version. `shifts` are (weekday, start, end) triples; the default is every day 09:00-17:30,
        /// matching the SeedBarberSchedules back-fill so "the barber works normal hours" needs no ceremony.
        /// </summary>
        public static BarberSchedule AddSchedule(
            this BarberShopContext db, int barberId, DateOnly effectiveFrom, DateOnly? effectiveTo = null,
            params (DayOfWeek Day, TimeOnly Start, TimeOnly End)[] shifts)
        {
            if (shifts.Length == 0)
                shifts = Enumerable.Range(0, 7)
                    .Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(17, 30)))
                    .ToArray();

            var schedule = new BarberSchedule
            {
                BarberId = barberId,
                EffectiveFrom = effectiveFrom,
                EffectiveTo = effectiveTo,
                Shifts = shifts.Select(s => new BarberScheduleShift
                {
                    DayOfWeek = s.Day,
                    StartTime = s.Start,
                    EndTime = s.End
                }).ToList()
            };
            db.BarberSchedules.Add(schedule);
            db.SaveChanges();
            return schedule;
        }

        public static Service AddService(this BarberShopContext db, decimal price = 25m, int durationMin = 30)
        {
            var n = Next();
            var service = new Service
            {
                Name = $"Service {n}",
                Description = "Test service",
                ImageUrl = "/uploads/test.png",
                Price = price,
                DurationMin = durationMin,
                IsActive = true
            };
            db.Services.Add(service);
            db.SaveChanges();
            return service;
        }

        /// <summary>
        /// A booking with no Stripe payment intent - the shape tier 1 uses everywhere, so the production
        /// code's Stripe branches (which all guard on StripePaymentIntentId / a COMPLETED card payment)
        /// are never entered.
        /// </summary>
        public static Booking AddBooking(
            this BarberShopContext db, int barberId, DateTime startDateTime,
            BookingStatus status = BookingStatus.COMPLETED, int durationMin = 30,
            User? customer = null, string? contactEmail = "customer@example.test",
            Service? service = null, string? reminderJobId = null)
        {
            customer ??= status == BookingStatus.PENDING ? null : db.AddUser();
            service ??= db.AddService(durationMin: durationMin);

            var booking = new Booking
            {
                PublicId = Guid.NewGuid().ToString("N"),
                BarberId = barberId,
                StartDateTime = startDateTime,
                DurationMin = durationMin,
                Status = status,
                UserId = customer?.Id,
                ContactEmail = status == BookingStatus.PENDING ? null : contactEmail,
                ReminderJobId = reminderJobId
            };
            db.Bookings.Add(booking);
            db.SaveChanges();

            db.BookingServices.Add(new BookingService { BookingId = booking.Id, ServiceId = service.Id });
            db.SaveChanges();
            return booking;
        }

        /// <summary>A settled cash payment - money that never went through Stripe, so cancelling it refunds nothing.</summary>
        public static Payment AddCashPayment(this BarberShopContext db, int bookingId, decimal amount = 25m)
        {
            var payment = new Payment
            {
                BookingId = bookingId,
                Amount = amount,
                Method = PaymentMethod.CASH,
                Status = PaymentStatus.COMPLETED,
                PaidAt = DateTime.UtcNow
            };
            db.Payments.Add(payment);
            db.SaveChanges();
            return payment;
        }

        /* A settled CARD payment. Safe in tier 1 only on paths that never reach Stripe - specifically the
         * inside-the-cutoff cancel, where BookingCanceller evaluates refundAllowed BEFORE calling Stripe and
         * short-circuits to CancelledNoRefund. Any test that would let a refund actually be attempted
         * belongs in tier 2; the dummy intent id below makes that mistake fail loudly rather than quietly
         * talking to Stripe. */
        public static Payment AddCardPayment(this BarberShopContext db, int bookingId, decimal amount = 25m)
        {
            var payment = new Payment
            {
                BookingId = bookingId,
                Amount = amount,
                Method = PaymentMethod.CARD,
                Status = PaymentStatus.COMPLETED,
                StripePaymentIntentId = $"pi_test_never_sent_to_stripe_{Next()}",
                PaidAt = DateTime.UtcNow
            };
            db.Payments.Add(payment);
            db.SaveChanges();
            return payment;
        }

        public static ShopClosure AddClosure(
            this BarberShopContext db, DateOnly startDate, DateOnly? endDate = null,
            bool isFullDay = true, TimeOnly? startTime = null, TimeOnly? endTime = null, int? barberId = null)
        {
            var closure = new ShopClosure
            {
                StartDate = startDate,
                EndDate = endDate,
                IsFullDay = isFullDay,
                StartTime = startTime,
                EndTime = endTime,
                BarberId = barberId,
                IsActive = true,
                Reason = "Test closure"
            };
            db.ShopClosures.Add(closure);
            db.SaveChanges();
            return closure;
        }

        public static void SetShopSetting(this BarberShopContext db, Action<ShopSettings> configure)
        {
            var settings = db.ShopSettings.Single(s => s.Id == 1);
            configure(settings);
            db.SaveChanges();
        }
    }
}
