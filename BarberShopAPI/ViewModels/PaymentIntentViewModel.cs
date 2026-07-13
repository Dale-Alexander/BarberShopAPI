using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class PaymentIntentViewModel
    {
        public string FullName { get; set; }
        public string Phone { get; set; }
        public long Amount { get; set; } // stripe uses cents
        public int BookingId { get; set; }
        public string Email { get; set; }
    }
}
