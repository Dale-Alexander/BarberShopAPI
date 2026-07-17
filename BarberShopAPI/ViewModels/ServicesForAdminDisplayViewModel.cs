namespace BarberShopAPI.ViewModels
{
    // Admin catalogue row. Unlike the customer-facing ServicesDisplayViewModel this carries the Id,
    // which the manage-services page needs to target the update/delete endpoints. Mirrors the
    // barbers/admin fetch (BarbersForAdminDisplayViewModel).
    public class ServicesForAdminDisplayViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public decimal Price { get; set; }
        public int DurationMin { get; set; }
        public string ImageUrl { get; set; }
    }
}
