namespace BarberShopAPI.ViewModels
{
    public class EditBookingViewModel
    {
        public int BarberId { get; set; }
        public DateTime StartDateTime {get;set;}
        // Needed by the reschedule slot picker so it greys slots by the booking's real length.
        public int DurationMin { get; set; }
    }
}
