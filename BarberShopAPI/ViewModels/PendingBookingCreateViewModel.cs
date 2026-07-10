using BarberShopAPI.Models;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class PendingBookingCreateViewModel
    {
        [Required]
        public ICollection<int> ServicesIds { get; set; }
        [Required]
        public DateTime StartDateTime { get; set; }
        [Required]
        public int BarberId { get; set; }
    }
}
