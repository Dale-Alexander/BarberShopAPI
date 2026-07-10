using System.Globalization;

namespace BarberShopAPI.ViewModels
{
    public class ServicesDisplayViewModel
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public decimal Price { get; set; }
        public int DurationMin { get; set; }
        public string ImageUrl { get; set; }
    }
}
