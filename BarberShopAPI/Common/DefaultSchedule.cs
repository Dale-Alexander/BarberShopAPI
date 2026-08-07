using BarberShopAPI.Models;

namespace BarberShopAPI.Common
{
    /* The hours a barber starts life with when nobody has said otherwise: one open-ended current version
     * covering the shop's own opening hours, and nothing at all on the days the shop is shut.
     *
     * The point of it existing at all is that a barber with NO schedule is unbookable and invisible to the
     * customer picker, and DeleteVersion refuses to leave anyone with zero versions. So every path that
     * brings a barber into existence has to hand them one of these.
     *
     * It lived as a private method in BarbersController, which was fine while create-barber was the only
     * such path. The admin seed is now a second one, and a copy-pasted 09:00-17:30 in a file nobody opens
     * between releases is exactly the kind of thing that drifts. The admin refines the real hours in the
     * schedule editor afterwards either way - this only has to be sane, not correct.
     *
     * It has to be more than sane in one respect now: shop hours are a ceiling over every shift, so a
     * default built from a hardcoded 09:00-17:30 would put a brand-new barber in violation the moment the
     * shop opens later than nine or closes earlier than half five - and the admin would open the schedule
     * editor to a barber they cannot save, over hours they never chose. Deriving the default from the
     * hours themselves means it fits by construction, whatever they are. */
    public static class DefaultSchedule
    {
        public static BarberSchedule Build(IEnumerable<ShopHours> shopHours)
        {
            var byDay = shopHours.ToDictionary(h => h.DayOfWeek);
            var schedule = new BarberSchedule
            {
                EffectiveFrom = ShopClock.Today,
                EffectiveTo = null,
                Shifts = new List<BarberScheduleShift>()
            };
            for (int d = 0; d < 7; d++)
            {
                var day = (DayOfWeek)d;
                /* A weekday with no row is treated like a closed one - no shift. The seven rows are seeded,
                 * so a missing one means the data is wrong, and giving a new barber hours on a day nobody
                 * configured is the worse of the two guesses. */
                if (!byDay.TryGetValue(day, out var hours) || hours.IsClosed) continue;
                schedule.Shifts.Add(new BarberScheduleShift
                {
                    DayOfWeek = day,
                    StartTime = hours.OpenTime,
                    EndTime = hours.CloseTime
                });
            }
            return schedule;
        }
    }
}
