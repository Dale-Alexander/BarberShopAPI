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
        /* Who the appointment is with, and how to reach the customer. Both are on the row rather than
         * baked into ReviewReason: ten different places write that text (BookingCanceller,
         * BookingConflictCanceller, EmailService x4, SchedulesController, WebHookController x2) and most
         * of them only say "the barber", which is useless in a list spanning the whole shop. Putting the
         * name here fixes every one of them at once, and the phone means a worklist whose instruction is
         * "call the customer" no longer has to embed the number in the sentence. */
        public string? BarberName { get; set; }
        public string? Phone { get; set; }
        public bool NeedsReview { get; set; }
        public string? ReviewReason { get; set; }

        /* Staff deliberately placed this booking outside the shop's hours / the barber's shifts. On the row
         * so the table can mark it as agreed rather than leaving a 19:00 appointment looking like a
         * mistake somebody should chase. Deliberately NOT folded into NeedsReview: that flag means "a human
         * still owes someone something", and these mean the opposite - it was already decided. */
        public bool OutsideShopHours { get; set; }
        public bool OutsideBarberSchedule { get; set; }
    }
}
