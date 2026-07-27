using System;
namespace BarberShopAPI.Models
{
    public class BarberScheduleShift
    {
        public int Id { get; set; }
        public int BarberScheduleId { get; set; }
        public virtual BarberSchedule? Schedule { get; set; }
        public DayOfWeek DayOfWeek { get; set; }   // System.DayOfWeek: Sunday = 0 .. Saturday = 6
        public TimeOnly StartTime { get; set; }
        public TimeOnly EndTime { get; set; }
    }
}
