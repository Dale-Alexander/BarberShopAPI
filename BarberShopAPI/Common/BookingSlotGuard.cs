using BarberShopAPI.ViewModels;
using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Common
{
    /* "Whose fault is it that this slot no longer works?" - the two things that can invalidate a booking
     * after it was made and are unambiguously the shop's doing.
     *
     * A deactivated barber wins over a closure: it's the one whose recovery is "rebook with someone else"
     * rather than "pick another time", so telling the customer the shop was shut would send them straight
     * back to a barber who has gone.
     *
     * Being outside the barber's shifts is deliberately NOT here. Staff can book and move appointments
     * outside a barber's hours on purpose (ConfirmOutsideHours - the shop staying open late), so such a
     * booking sits outside the shifts from the moment it exists and says nothing about whether the hours
     * later moved. The checkout paths can safely ask that question because a customer booking is always
     * made inside the schedule; this one can't.
     *
     * State, never the NeedsReview flag or its note. The flag only means "a human still owes someone
     * something" - a booking can carry it because a refund failed or an email bounced, which says nothing
     * about why it's being cancelled now - and the note is free text that would break the first time
     * someone reworded a sentence. */
    public static class BookingSlotGuard
    {
        /// <summary>The reason this booking's slot is no longer serviceable, or null if nothing is wrong with it.</summary>
        public static async Task<CancellationReason?> ResolveBlockingReasonAsync(BarberShopContext context, Booking booking)
        {
            var appointmentDate = DateOnly.FromDateTime(booking.StartDateTime);
            var appointmentTime = TimeOnly.FromDateTime(booking.StartDateTime);
            var endTime = TimeOnly.FromDateTime(booking.StartDateTime.AddMinutes(booking.DurationMin));

            if (!await context.Barbers.AnyAsync(b => b.Id == booking.BarberId && b.isActive))
                return CancellationReason.BarberUnavailable;

            if (await IsSlotClosedAsync(context, booking)) return CancellationReason.ShopClosure;

            return null;
        }

        /* Builds the rows for the three "these are fine again" lists - widened hours, revived barber, deleted
         * closure - so all three say the same things in the same shape.
         *
         * StillBlockedBy is the point of it. Each list only ever proves ITS OWN dimension is clear, so
         * without this they could only tell the admin "if this was the only reason, mark it reviewed" and
         * leave them to work out whether it was. Now the row says whether anything else about the slot is
         * still in the way. */
        public static async Task<List<OrphanedBookingViewModel>> DescribeAsync(
            BarberShopContext context, IEnumerable<Booking> bookings)
        {
            var rows = new List<OrphanedBookingViewModel>();
            foreach (var b in bookings)
            {
                rows.Add(new OrphanedBookingViewModel
                {
                    Id = b.Id,
                    Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                    Time = b.StartDateTime.ToString("h:mm tt"),
                    Customer = b.User != null ? $"{b.User.Name} {b.User.Surname}".Trim() : null,
                    Email = b.ContactEmail,
                    Phone = b.User != null ? b.User.Phone : null,
                    StillBlockedBy = await ResolveBlockingReasonAsync(context, b) switch
                    {
                        CancellationReason.BarberUnavailable => "Its barber is no longer active",
                        CancellationReason.ShopClosure => "A shop closure still covers this slot",
                        _ => null
                    }
                });
            }
            return rows;
        }

        /* Whether this booking still sits inside its barber's working hours.
         *
         * Useless on its own for deciding fault - staff book outside a barber's hours deliberately
         * (ConfirmOutsideHours), so "doesn't fit" alone proves nothing, which is why ResolveBlockingReasonAsync
         * doesn't ask it. Paired with the schedule flow's review note it becomes exact: the note says an hours
         * change stranded this booking, and this says whether that is STILL true. Together they separate a real
         * casualty from a note left behind after the hours were put back. */
        public static async Task<bool> FitsBarbersHoursAsync(BarberShopContext context, Booking booking)
        {
            var appointmentDate = DateOnly.FromDateTime(booking.StartDateTime);
            var appointmentTime = TimeOnly.FromDateTime(booking.StartDateTime);
            var endTime = TimeOnly.FromDateTime(booking.StartDateTime.AddMinutes(booking.DurationMin));

            var graceMin = await context.ShopSettings.Select(s => s.GraceMinutesAfterClose).FirstAsync();
            var scheduleVersions = await context.BarberSchedules
                .Include(s => s.Shifts)
                .Where(s => s.BarberId == booking.BarberId
                            && s.EffectiveFrom <= appointmentDate
                            && (s.EffectiveTo == null || s.EffectiveTo >= appointmentDate))
                .ToListAsync();
            // Needed only to decide whether the day's last shift runs to closing and so earns its grace -
            // this asks about the BARBER's hours, and doesn't check the shop's on its own account.
            var shopHours = await context.ShopHours.ToListAsync();

            return ScheduleResolver.FitsWithinAShift(scheduleVersions, shopHours, appointmentDate, appointmentTime, endTime, graceMin);
        }

        /// <summary>
        /// Whether ANY active closure still covers this booking's slot. Split out because the closure flow
        /// needs exactly this question and no more: deleting one closure while another still covers the slot
        /// leaves the booking just as shut, so it must not be announced as reopened.
        /// </summary>
        public static async Task<bool> IsSlotClosedAsync(BarberShopContext context, Booking booking)
            => await ClosuresCoveringSlot(context, booking)
                .AnyAsync(s => s.BarberId == null || s.BarberId == booking.BarberId);

        /// <summary>
        /// Whether a SHOP-WIDE closure covers this booking's slot, as opposed to only the barber's own
        /// time off. Both are "closed" as far as IsSlotClosedAsync is concerned, but they are not the same
        /// thing to tell a customer: if the shop is shut their only option is another time, whereas if it
        /// was their barber's day off the shop is open and another chair may be free the same day. The
        /// cancellation email branches on this so it stops saying "we've had to close the shop" for what
        /// was really one barber being off.
        /// </summary>
        public static async Task<bool> IsSlotClosedShopWideAsync(BarberShopContext context, Booking booking)
            => await ClosuresCoveringSlot(context, booking).AnyAsync(s => s.BarberId == null);

        /* The date/time overlap test both questions above share. Kept in one place deliberately: they
         * differ ONLY in which barber's closures count, and two hand-copied overlap predicates would
         * eventually drift and start disagreeing about whether a slot is shut. */
        private static IQueryable<ShopClosure> ClosuresCoveringSlot(BarberShopContext context, Booking booking)
        {
            var appointmentDate = DateOnly.FromDateTime(booking.StartDateTime);
            var appointmentTime = TimeOnly.FromDateTime(booking.StartDateTime);
            var endTime = TimeOnly.FromDateTime(booking.StartDateTime.AddMinutes(booking.DurationMin));

            return context.ShopClosures.Where(s =>
                s.IsActive == true &&
                ((s.EndDate == null && s.StartDate == appointmentDate) ||
                 (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) &&
                (s.IsFullDay || (s.StartTime < endTime && s.EndTime > appointmentTime)));
        }
    }
}
