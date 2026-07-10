using BarberShopAPI.ViewModels;
namespace BarberShopAPI.ViewModels
{
    public class BarberBookingViewModel
    {
        public int BarberId { get; set; }
        public string BarberName { get; set; }
        public string? BarberSurname { get; set; }
        public string ImageUrl { get; set; }
        public ICollection<BookingsDateAndTimeViewModel> Bookings { get; set; }
        public ICollection<BarberShopClosureViewModel> DateClosures { get; set; }
    }
}
