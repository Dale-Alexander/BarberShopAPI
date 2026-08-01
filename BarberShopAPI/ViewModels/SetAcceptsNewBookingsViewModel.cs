using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class SetAcceptsNewBookingsViewModel
    {
        // Required (not a plain bool) so an empty or malformed body is a 400 rather than silently
        // defaulting to false and closing a barber to new bookings the admin never touched.
        [Required]
        public bool? AcceptsNewBookings { get; set; }
    }
}
