using System;
namespace BarberShopAPI.Models
{
    public class BarberSchedule
    {
        public int Id { get; set; }
        public int BarberId { get; set; }
        public virtual Barber? Barber { get; set; }
        public DateOnly EffectiveFrom { get; set; }
        public DateOnly? EffectiveTo { get; set; }   // null = current / open-ended version
        public virtual ICollection<BarberScheduleShift> Shifts { get; set; } = new List<BarberScheduleShift>();
    }
}
