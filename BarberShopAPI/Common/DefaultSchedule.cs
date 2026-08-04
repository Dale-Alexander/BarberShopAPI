using BarberShopAPI.Models;

namespace BarberShopAPI.Common
{
    /* The hours a barber starts life with when nobody has said otherwise: one open-ended current version,
     * every day 09:00-17:30 - the shop's historic hardcoded hours, matching the day-one seed migration.
     *
     * The point of it existing at all is that a barber with NO schedule is unbookable and invisible to the
     * customer picker, and DeleteVersion refuses to leave anyone with zero versions. So every path that
     * brings a barber into existence has to hand them one of these.
     *
     * It lived as a private method in BarbersController, which was fine while create-barber was the only
     * such path. The admin seed is now a second one, and a copy-pasted 09:00-17:30 in a file nobody opens
     * between releases is exactly the kind of thing that drifts. The admin refines the real hours in the
     * schedule editor afterwards either way - this only has to be sane, not correct. */
    public static class DefaultSchedule
    {
        public static BarberSchedule Build()
        {
            var schedule = new BarberSchedule
            {
                EffectiveFrom = ShopClock.Today,
                EffectiveTo = null,
                Shifts = new List<BarberScheduleShift>()
            };
            for (int d = 0; d < 7; d++)
            {
                schedule.Shifts.Add(new BarberScheduleShift
                {
                    DayOfWeek = (DayOfWeek)d,
                    StartTime = new TimeOnly(9, 0),
                    EndTime = new TimeOnly(17, 30)
                });
            }
            return schedule;
        }
    }
}
