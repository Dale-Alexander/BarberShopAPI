using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Models
{
    /* The shop's own opening hours, one row per weekday (seven rows, seeded in BarberShopContext and
     * keyed by DayOfWeek - there is no "add a day" operation, only editing the seven).
     *
     * Its own table rather than fourteen columns on ShopSettings because a weekday needs three values,
     * not two: IsClosed is a real state and encoding it as OpenTime == CloseTime is the kind of sentinel
     * every reader has to remember. It also matches the shape of BarberScheduleShift, which is what it
     * constrains.
     *
     * Two jobs, and it is worth being clear that neither is "validate bookings on its own":
     *   - the CEILING for barber shifts. A shift may not start before OpenTime, end after CloseTime, or
     *     exist at all on a closed day (enforced in SchedulesController, and in reverse when these hours
     *     are edited). Because customers are already bounded by shifts, that invariant is also what
     *     bounds customers to shop hours - without a second check on the customer booking path;
     *   - the default range of the STAFF slot grid, which is otherwise unbounded - staff aren't limited
     *     to any barber's hours, so without this there is nothing to draw.
     * Staff can still deliberately book outside these hours; see Booking.OutsideShopHours.
     *
     * NOT versioned, unlike BarberSchedule. Versioning exists there because a barber's hours decide
     * whether a booking was valid on its date, so history has to be reconstructable. These decide
     * nothing retroactively - booking validity still resolves through shifts, which ARE versioned - so a
     * second effective-dated timeline would only be one more thing to keep in step with the first.
     * Seasonal hours, if they are ever wanted, are better served by applying a change across every
     * barber's schedule from a date than by dating this. */
    [Index(nameof(DayOfWeek), IsUnique = true)]
    public class ShopHours
    {
        [Key]
        public int Id { get; set; }

        public DayOfWeek DayOfWeek { get; set; }

        /* Ignored entirely when IsClosed - they are kept at their previous values rather than nulled so
         * that reopening a day restores the hours it used to have instead of an empty form. */
        public TimeOnly OpenTime { get; set; }
        public TimeOnly CloseTime { get; set; }

        // No barber may hold a shift on this weekday, and the staff grid is empty for it by default.
        public bool IsClosed { get; set; }
    }
}
