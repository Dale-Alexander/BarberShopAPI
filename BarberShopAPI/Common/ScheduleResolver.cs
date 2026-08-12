using BarberShopAPI.Models;

namespace BarberShopAPI.Common
{
    // Resolves a barber's working hours for a given date from their effective-dated weekly
    // schedule versions. Pure functions over already-loaded schedules, so they're trivially
    // testable and callable from any controller without a DbContext. The frontend mirrors this
    // same logic to grey out slots (see BarberDateAndTime); this backend copy is the authority.
    public static class ScheduleResolver
    {
        // The version governing a date is the one whose [EffectiveFrom, EffectiveTo] window covers
        // it (EffectiveTo == null = open-ended / current). Returns null when the barber has no
        // version covering that date - callers treat that as "closed" (fail closed).

        /* loops through a barber's schedules and returns the one where it covers the date
         *The inner if is there as a safety net so that when two schedules overlap(which shouldnt happen because schedules should not overlap with each other),
         *the schedule which started latest is chosen.There might not be a schedule which covers the date so we return null in that case
         */
        public static BarberSchedule? VersionForDate(IEnumerable<BarberSchedule> schedules, DateOnly date)
        {
            BarberSchedule? best = null;
            foreach (var s in schedules)
            {
                if (s.EffectiveFrom <= date && (s.EffectiveTo == null || s.EffectiveTo >= date))
                {
                    // Non-overlap is enforced on write; if two ever match, prefer the one starting
                    // latest (the more recent / more specific regime).
                    if (best == null || s.EffectiveFrom > best.EffectiveFrom) best = s;
                }
            }
            return best;
        }

        // The shift ranges for a date: the governing version's shifts for that weekday, ordered by
        // start. Empty = closed that day (no governing version, or none scheduled that weekday).
        public static IReadOnlyList<(TimeOnly Start, TimeOnly End)> ShiftsForDate(
            IEnumerable<BarberSchedule> schedules, DateOnly date)
        {
            var version = VersionForDate(schedules, date);
            if (version?.Shifts == null) return Array.Empty<(TimeOnly, TimeOnly)>();//this checks whether a schedule version was found for that date and it checks if that version has shifts
            return version.Shifts
                .Where(sh => sh.DayOfWeek == date.DayOfWeek)
                .Select(sh => (sh.StartTime, sh.EndTime))
                .OrderBy(r => r.StartTime)
                .ToList();
        }/* This returns the shifts for that booking's date. It returns specifically the shifts which land on the same day of the week as the best booking.
          * A day of the week may have more than 1 shift, just remember that. We order the shifts*/

        /* Does [start, end] fit ENTIRELY within a single shift on `date`?
         *
         * Grace (minutes a booking may run past CLOSING) is granted to at most one shift: the day's last, and
         * only when that shift actually runs to closing time. Two things follow from that, and both matter.
         *
         * Never a mid-day lunch gap, so a booking can't bleed through the break between split shifts.
         *
         * And never a shift that finishes while the shop is still open. The setting is grace after CLOSE, so a
         * barber whose last shift ends at 13:00 in a shop that closes at 17:30 was being made bookable until
         * 13:30 - not grace at anything, just half an hour of someone's afternoon, taken with no confirmation
         * because this reported no breach. The reason grace has to reach a shift at all is narrower than the
         * old rule assumed: Req 2 makes "the last shift ends when the shop closes" the normal case, so grace
         * applied only to the shop would let a booking clear the shop's close and then fail here, breaking the
         * last slot of every day instead of allowing it. Testing the shift against closing time buys exactly
         * that and nothing more. Hence shopHours, which is why this needs them at all.
         *
         * The start must be strictly inside the shift for the same reason it must be strictly before closing
         * (see ShopHoursResolver.FitsShopHours): grace lengthens an appointment that began during the shift,
         * it doesn't add one after it.
         *
         * No governing version => false. */
        public static bool FitsWithinAShift(
            IEnumerable<BarberSchedule> schedules, IEnumerable<ShopHours> shopHours, DateOnly date,
            TimeOnly start, TimeOnly end, int graceMinutes)
        {
            var shifts = ShiftsForDate(schedules, date);
            if (shifts.Count == 0) return false;//this is when a day has no shifts like sunday

            // No row, or a closed day, means no closing time for a shift to reach - so no shift earns grace.
            // Fail-safe either way: the booking paths ask FitsShopHours as well, which refuses both outright.
            var day = ShopHoursResolver.ForDay(shopHours, date.DayOfWeek);

            for (int i = 0; i < shifts.Count; i++)
            {
                var (shiftStart, shiftEnd) = shifts[i];
                var closesTheDay = i == shifts.Count - 1
                    && day != null && !day.IsClosed && shiftEnd >= day.CloseTime;
                var allowedEnd = closesTheDay ? shiftEnd.AddMinutes(graceMinutes) : shiftEnd;
                if (start >= shiftStart && start < shiftEnd && end <= allowedEnd) return true;
            }
            return false;
        }
    }
}
