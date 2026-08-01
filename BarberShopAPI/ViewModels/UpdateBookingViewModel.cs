namespace BarberShopAPI.ViewModels
{
    public class UpdateBookingViewModel
    {
        public int? BarberId { get; set; }
        public DateTime? StartDateTime { get; set; }

        /* Staff acknowledging that the slot falls outside the barber's working hours and choosing it
         * anyway - the shop staying open late for a regular. Without it the move is refused (409), so an
         * out-of-hours booking can only ever be the result of someone deciding to make one, never of a
         * request that drifted there. A barber may only set it for their own chair; see UpdateBooking. */
        public bool ConfirmOutsideHours { get; set; }
    }
}