using System.ComponentModel.DataAnnotations;
using Resend;

namespace BarberShopAPI.ViewModels
{
    public class ConfirmCashBookingViewModel
    {
        public string FullName { get; set; }
        public string Phone { get; set; }
        // Booking's public slug, not the internal int (see PaymentIntentViewModel).
        public string BookingId { get; set; }
        // NOTE: the amount is NOT accepted from the client. It's computed server-side from the
        // booking's own services (see ConfirmCashBooking) so the recorded total can't be tampered
        // with. Any amount sent in the request body is ignored.
        public string Email { get; set; }
    }
}
