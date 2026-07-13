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

        /* Two-step confirmation for closures that overlap existing bookings. The first request comes in
         * false: if there are conflicting bookings the API returns them (409) instead of creating the
         * closure. The admin re-submits with this true to say "yes, cancel & refund those bookings and
         * make the closure anyway".*/
        public bool ConfirmCancelBookings { get; set; }
    }
}