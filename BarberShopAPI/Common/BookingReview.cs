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
    /* The distinctive sentences the shop-side flows write into a review note, kept together because more
     * than one place has to recognise them later:
     *   - the flow that caused the problem writes one;
     *   - the flow that UNDOES it (widened hours, revived barber, deleted closure) matches on it to tell
     *     the admin which notes it just made stale;
     *   - CancelBooking matches on the hours one to work out what to tell the customer.
     *
     * Matching on a sentence is only safe while that sentence has exactly one author - which is the whole
     * reason they live here as constants rather than being typed out at each site. Change the wording and
     * every reader changes with it. */
    public static class ReviewMarkers
    {
        public const string OutsideHours =
            "The barber's working hours changed and this booking now falls outside their "
            + "schedule - honour it, reschedule it, or cancel it.";

        public const string BarberLeft = "has left the shop, and this booking is still live";

        /* A closure is either shop-wide (ShopClosure.BarberId == null) or one barber's own time off, and
         * the two need different notes: saying "the shop is closed" for a barber's day off is simply
         * false, and telling the admin to reassign a shop-wide closure is useless advice - every barber
         * is shut, so moving it to a colleague changes nothing.
         *
         * Two constants rather than one because the sentences share no useful wording. Anything matching
         * a closure note has to test BOTH - see DeleteClosure, the only reader. */
        public const string ShopClosed = "The shop is closed for this booking's slot";

        public const string BarberClosed = "is off for this booking's slot";

        /* The refund Stripe accepted and then failed hours later (charge.refund.updated / refund.failed).
         * Unlike every other marker here, nothing ever undoes this one: no flow puts the money back by
         * itself, so there is nothing to match on it for a "this is stale now" announcement. It is a
         * constant anyway so the sentence has one author, like the rest.
         *
         * The wording has to be blunt. Every other note describes something that has NOT happened to the
         * customer yet; this one describes a promise the shop has already broken - the cancellation email
         * told them the money was on its way, and it isn't coming without a human. */
        public const string RefundFailed = "The refund for this booking FAILED after Stripe had accepted it";
    }

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

        /* No counterpart to the above on purpose - nothing ever takes a note back off.
         *
         * The hours note is the one case where the app COULD disprove itself and withdraw the sentence
         * (it's stored as a whole constant, unlike the barber and closure notes, which paste a name and a
         * time into the middle and so have no exact text to remove). It deliberately doesn't:
         *
         *   - it would be the only reason that behaved that way, so an admin who saw a note quietly vanish
         *     after fixing the hours would reasonably expect the same after reviving a barber, and be left
         *     wondering what went wrong when it didn't;
         *   - the cost of leaving it is one narrow case - hours put back (or the booking moved) without
         *     marking it reviewed, then cancelled - where the customer is told their barber no longer works
         *     at that time, and is refunded in full inside the cutoff. Wrong, but wrong in the customer's
         *     favour, and it takes an admin skipping the sign-off the whole design asks for.
         *
         * One rule, for every reason: notes go on automatically and come off only when a human marks the
         * booking reviewed. */
    }
}
