using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class ShopClosureViewModel
    {
        public int? BarberId { get; set; }

        [Required]
        public DateOnly StartDate { get; set; }

        public DateOnly? EndDate { get; set; }
        [Required]
        public bool IsFullDay { get; set; }

        public TimeOnly? StartTime { get; set; }
        public TimeOnly? EndTime { get; set; }
        public string? Reason { get; set; }
    }
}