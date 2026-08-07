using BarberShopAPI.Models;

namespace BarberShopAPI.Common
{
    /* The shop-hours counterpart to ScheduleResolver: pure functions over an already-loaded set of the
     * seven ShopHours rows, so the same rules can be applied from a controller, from a validation path, or
     * from a test without a DbContext. The frontend mirrors these to draw and grey the picker; this copy is
     * the authority. */
    public static class ShopHoursResolver
    {
        public static ShopHours? ForDay(IEnumerable<ShopHours> hours, DayOfWeek day) =>
            hours.FirstOrDefault(h => h.DayOfWeek == day);

        /* Does [start, end] sit inside the shop's hours for `date`?
         *
         * Grace applies here exactly as it does to a barber's last shift, and it has to. Req 2 makes a shift
         * ending at closing time the normal case, so without this a non-zero GraceMinutesAfterClose would
         * let a booking run past the barber's shift and then fail on the shop instead - the grace setting
         * would break the last slot of every day rather than allow it.
         *
         * A closed day fails, as does a weekday with no row at all: the seven rows are seeded, so a missing
         * one means something is wrong with the data, and refusing bookings is the safe reading of that. */
        public static bool FitsShopHours(
            IEnumerable<ShopHours> hours, DateOnly date, TimeOnly start, TimeOnly end, int graceMinutes)
        {
            var day = ForDay(hours, date.DayOfWeek);
            if (day == null || day.IsClosed) return false;
            return start >= day.OpenTime && end <= day.CloseTime.AddMinutes(graceMinutes);
        }

        /* Why a shift breaks the proposed hours, or null when it doesn't. Req 2's invariant in one place,
         * shared by the two directions it is enforced from - editing a schedule against today's hours, and
         * editing the hours against every existing schedule - because two copies would eventually disagree
         * about what "fits" means and let a violation in through whichever side was wrong.
         *
         * No grace here, deliberately. Grace lets a BOOKING run over; it doesn't let a shift be rostered
         * past closing. Allowing that would compound - a shift ending at close+grace would then permit a
         * booking to close+2*grace. */
        public static string? ShiftViolation(ShopHours? day, TimeOnly shiftStart, TimeOnly shiftEnd)
        {
            if (day == null) return "the shop has no hours set for this day";
            if (day.IsClosed) return "the shop is closed on this day";
            if (shiftStart < day.OpenTime)
                return $"it starts before the shop opens ({day.OpenTime:HH\\:mm})";
            if (shiftEnd > day.CloseTime)
                return $"it ends after the shop closes ({day.CloseTime:HH\\:mm})";
            return null;
        }
    }
}
