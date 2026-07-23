namespace BarberShopAPI.ViewModels
{
    public class BookingsAdminViewModel
    {
        public int Id { get; set; }
        public DateTime StartDateTime { get; set; }
        // Booking lifecycle (COMPLETED = confirmed / CANCELLED), separate from PaymentStatus below. The
        // table badges each row with it so the "All" filter reads correctly and cancelled rows don't
        // offer actions the backend will reject.
        public string Status { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public decimal? Amount { get; set; }
        public string PaymentStatus { get; set; }
        public string PaymentMethod { get; set; }
        public bool NeedsReview { get; set; }
        public string? ReviewReason { get; set; }
    }
}
