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
            if (version?.Shifts == null) return Array.Empty<(TimeOnly, TimeOnly)>();
            return version.Shifts
                .Where(sh => sh.DayOfWeek == date.DayOfWeek)
                .Select(sh => (sh.StartTime, sh.EndTime))
                .OrderBy(r => r.StartTime)
                .ToList();
        }

        // Does [start, end] fit ENTIRELY within a single shift on `date`? Grace (minutes a booking
        // may run past close) applies ONLY to the day's LAST shift, never a mid-day lunch gap - so a
        // booking can't bleed through the break between split shifts. No governing version => false.
        public static bool FitsWithinAShift(
            IEnumerable<BarberSchedule> schedules, DateOnly date,
            TimeOnly start, TimeOnly end, int graceMinutes)
        {
            var shifts = ShiftsForDate(schedules, date);
            if (shifts.Count == 0) return false;

            for (int i = 0; i < shifts.Count; i++)
            {
                var (shiftStart, shiftEnd) = shifts[i];
                var isLast = i == shifts.Count - 1;
                var allowedEnd = isLast ? shiftEnd.AddMinutes(graceMinutes) : shiftEnd;
                if (start >= shiftStart && end <= allowedEnd) return true;
            }
            return false;
        }
    }
}
