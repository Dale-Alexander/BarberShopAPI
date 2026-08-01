using BarberShopAPI.Models;

namespace BarberShopAPI.Common
{
    /* The one way a booking gets into the Needs Review worklist.
     *
     * Every site used to assign ReviewReason directly, which meant a second problem silently erased the
     * first: a booking flagged "check Stripe for a charge on this booking" that then fell outside the
     * barber's new hours ended up saying only that the hours changed, and the money reminder was gone.
     * Appending keeps both, and the admin reads the full story before clearing it.
     *
     * Nothing here clears the flag - mark-reviewed is the only way out of the worklist (see the note in
     * BookingsController.UpdateBooking for why nothing clears it implicitly). */
    public static class BookingReview
    {
        public static void FlagForReview(this Booking booking, string reason)
        {
            booking.NeedsReview = true;

            if (string.IsNullOrWhiteSpace(reason)) return;
            if (string.IsNullOrWhiteSpace(booking.ReviewReason))
            {
                booking.ReviewReason = reason;
                return;
            }
            /* The same problem hitting twice - a barber's hours edited two days running, a retried job -
             * is one note, not two identical sentences the admin has to read past. */
            if (booking.ReviewReason.Contains(reason)) return;

            booking.ReviewReason = $"{booking.ReviewReason.TrimEnd()} {reason}";
        }
    }
}
