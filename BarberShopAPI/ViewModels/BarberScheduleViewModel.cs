namespace BarberShopAPI.ViewModels
{
    // A barber's weekly schedule version sent to the slot picker. The frontend resolves which
    // version governs a chosen date (EffectiveTo == null = current/open-ended) and generates slots
    // from that version's shifts for the day - mirroring the backend ScheduleResolver.
    public class BarberScheduleViewModel
    {
        public DateOnly EffectiveFrom { get; set; }
        public DateOnly? EffectiveTo { get; set; }
        public ICollection<BarberScheduleShiftViewModel> Shifts { get; set; } = new List<BarberScheduleShiftViewModel>();
    }

    public class BarberScheduleShiftViewModel
    {
        public int DayOfWeek { get; set; }   // 0 = Sunday .. 6 = Saturday (System.DayOfWeek)
        public TimeOnly StartTime { get; set; }
        public TimeOnly EndTime { get; set; }
    }
}
