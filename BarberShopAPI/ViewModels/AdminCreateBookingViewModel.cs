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
        [MaxLength(20)] // matches the User.Phone column (nvarchar(20))
        public string Phone { get; set; }

        [MaxLength(100)] // split into User.Name/User.Surname (nvarchar(50) each); per-part length
                         // is enforced in the controller's IsValidName
        public string FullName { get; set; }
    }
}
