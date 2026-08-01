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

        // Optional for staff bookings (a walk-in known only by phone). Nullable on purpose: a
        // non-nullable string is treated as implicitly required by the model binder, which would 400
        // a blank name before the controller runs.
        [MaxLength(100)] // split into User.Name/User.Surname (nvarchar(50) each); per-part length
                         // is enforced in the controller's IsValidName when a name is provided
        public string? FullName { get; set; }

        /* Staff deliberately booking outside the barber's working hours - see the matching field on
         * UpdateBookingViewModel. Refused (409) unless set, and a barber may only set it for themselves. */
        public bool ConfirmOutsideHours { get; set; }
    }
}
