using System;
namespace BarberShopAPI.Models
{
    public class ShopClosure
    {
        public int Id { get; set; }
        public DateOnly StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public TimeOnly? StartTime { get; set; }
        public TimeOnly? EndTime { get; set; }
        public string? Reason { get; set; }
        public bool IsFullDay { get; set; }
        public virtual Barber? Barber { get; set; }
        public int? BarberId { get; set; }
        public bool IsActive { get; set; } = true;
    }
}