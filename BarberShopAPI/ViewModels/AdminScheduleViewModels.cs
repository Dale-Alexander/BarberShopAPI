namespace BarberShopAPI.ViewModels
{
    // A single working-hours block on a weekday. DayOfWeek is 0=Sunday..6=Saturday (System.DayOfWeek).
    // Multiple blocks on the same day = a split shift (e.g. a lunch break).
    public class ScheduleShiftViewModel
    {
        public int DayOfWeek { get; set; }
        public TimeOnly StartTime { get; set; }
        public TimeOnly EndTime { get; set; }
    }

    // A schedule version as shown in the admin editor (includes the row Id so it can be edited/removed,
    // unlike the customer-facing BarberScheduleViewModel).
    public class AdminScheduleVersionViewModel
    {
        public int Id { get; set; }
        public DateOnly EffectiveFrom { get; set; }
        public DateOnly? EffectiveTo { get; set; }
        public List<ScheduleShiftViewModel> Shifts { get; set; } = new();

        /* Only set on the response to creating a version: the already-flagged bookings these hours bring
         * back inside the schedule, so the editor can list them for the admin. Always empty when the list
         * of versions is read (GetBarberSchedule), where it means nothing. */
        public List<OrphanedBookingViewModel> BackInsideHours { get; set; } = new();
    }

    // Body for replacing a version's shifts (PUT). ConfirmOrphaned = the admin has seen the confirmed
    // future bookings that will fall outside the new hours and wants to proceed anyway (they're
    // grandfathered, never cancelled - see Phase 7 / the orphaned-booking check).
    public class SaveScheduleShiftsViewModel
    {
        public List<ScheduleShiftViewModel> Shifts { get; set; } = new();
        public bool ConfirmOrphaned { get; set; }
    }

    // Body for starting a new seasonal version from a date (POST). The current open-ended version is
    // automatically closed the day before EffectiveFrom (the chaining rule).
    public class CreateScheduleVersionViewModel
    {
        public DateOnly EffectiveFrom { get; set; }
        public List<ScheduleShiftViewModel> Shifts { get; set; } = new();
        public bool ConfirmOrphaned { get; set; }
    }

    // A confirmed future booking left outside a barber's hours by a schedule change - surfaced to the
    // admin so they can reschedule / refund / honour it by hand. Never auto-cancelled.
    public class OrphanedBookingViewModel
    {
        public int Id { get; set; }
        public string Date { get; set; } = "";
        public string Time { get; set; } = "";
        public string? Customer { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
    }
}
