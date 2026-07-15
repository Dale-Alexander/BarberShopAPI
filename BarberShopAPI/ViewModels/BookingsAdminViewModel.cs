namespace BarberShopAPI.ViewModels
{
    public class BookingsAdminViewModel
    {
        public int Id { get; set; }
        public DateTime StartDateTime { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public decimal? Amount { get; set; }
        public string PaymentStatus { get; set; }
        public string PaymentMethod { get; set; }
        public bool NeedsReview { get; set; }
        public string? ReviewReason { get; set; }
    }
}
