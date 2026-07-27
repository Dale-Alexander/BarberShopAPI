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

        // Does [start, end] fit ENTIRELY within a single shift on `date`? Grace (minutes a booking
        // may run past close) applies ONLY to the day's LAST shift, never a mid-day lunch gap - so a
        // booking can't bleed through the break between split shifts. No governing version => false.
        public static bool FitsWithinAShift(
            IEnumerable<BarberSchedule> schedules, DateOnly date,
            TimeOnly start, TimeOnly end, int graceMinutes)
        {
            var shifts = ShiftsForDate(schedules, date);
            if (shifts.Count == 0) return false;//this is when a day has no shifts like sunday

            for (int i = 0; i < shifts.Count; i++)
            {
                var (shiftStart, shiftEnd) = shifts[i];
                var isLast = i == shifts.Count - 1;
                var allowedEnd = isLast ? shiftEnd.AddMinutes(graceMinutes) : shiftEnd;
                if (start >= shiftStart && end <= allowedEnd) return true;
                /* if the booking's slot lands within a valid shift, return true. IF a day has multiple shifts, check if the current shift is the last shift of the day,
                 * and if it is add graceMinutes to it, dont add grace to the end of the first shift because you are allowing to be made in a lunch break for example. 
                 */
            }
            return false;
        }
    }
}
