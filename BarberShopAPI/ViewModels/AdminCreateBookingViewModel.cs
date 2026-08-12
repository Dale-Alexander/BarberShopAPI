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
        /* The same 5-240 the shop's own DefaultAdminBookingDurationMin is bounded by, and the same bound the
         * picker's duration box already validates against inline. The controller only ever rejected <= 0, so
         * a four-figure duration reached the hours check and was refused there instead - a confusing "outside
         * the barber's working hours" for what is really a nonsense length. */
        [Range(5, 240, ErrorMessage = "Booking duration must be between 5 and 240 minutes")]
        public int DefaultDurationMin { get; set; }
        [Required]
        [MaxLength(20)] // matches the User.Phone column (nvarchar(20))
        public string Phone { get; set; }

        // Optional for staff bookings (a walk-in known only by phone). Nullable on purpose: a
        // non-nullable string is treated as implicitly required by the model binder, which would 400
        // a blank name before the controller runs.
        [MaxLength(100)] // split into User.Name/User.Surname; each half is capped at 50 by
                         // PersonName.TrySplit when a name is provided (an app rule, not the column's)
        public string? FullName { get; set; }

        /* Staff deliberately booking outside the barber's working hours - see the matching field on
         * UpdateBookingViewModel. Refused (409) unless set, and a barber may only set it for themselves. */
        public bool ConfirmOutsideHours { get; set; }
    }
}
