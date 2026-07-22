using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class PaymentIntentViewModel
    {
        public string FullName { get; set; }
        public string Phone { get; set; }
        // NOTE: the amount is NOT accepted from the client. It's computed server-side from the
        // booking's own services (see StartBookingCardFlow) so a customer can't tamper with what
        // they're charged. Any amount sent in the request body is ignored.
        // The booking's non-guessable public id (slug), not the internal int - the guest client only
        // ever knows the slug. Resolved to the real booking server-side (StartBookingCardFlow).
        public string BookingId { get; set; }
        public string Email { get; set; }
    }
}
