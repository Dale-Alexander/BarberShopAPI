using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class ConfirmCashBookingViewModel
    {
        public string FullName { get; set; }
        public string Phone { get; set; }
        public int BookingId { get; set; }
        public decimal? Amount { get; set; }
    }
}
