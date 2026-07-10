namespace BarberShopAPI.ViewModels
{
    public class CheckoutBookingDisplayDetailsViewModel
    {
        public string BarberName { get; set; }
        public string BarberSurname { get; set; }
        public string ImageUrl { get; set; }
        public List<string> ServiceNames { get; set; }
        public DateTime StartDateTime { get; set; }
        public int DurationMin { get; set; }
        public decimal Price { get; set; }
    }
}
