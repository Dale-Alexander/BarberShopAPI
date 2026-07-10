using BarberShopAPI.Models;
using Microsoft.Identity.Client;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class AdminCreateBookingCreateViewModel
    {
        [Required]
        public DateTime StartDateTime { get; set; }
        [Required]
        public int BarberId { get; set; }
        [Required]
        public int DefaultDurationMin { get; set; }
        [Required]
        public string Phone { get; set; }

        public string FullName { get; set; }
    }
}
