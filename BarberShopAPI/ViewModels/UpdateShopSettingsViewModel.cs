using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class UpdateShopSettingsViewModel
    {
        // Gap (minutes) required between consecutive bookings. 0 disables it; capped to keep a
        // fat-fingered value from silently blocking a whole day of slots.
        [Range(0, 120, ErrorMessage = "Buffer must be between 0 and 120 minutes")]
        public int BufferMin { get; set; }

        // Default duration (minutes) pre-filled for admin-created bookings.
        [Range(5, 240, ErrorMessage = "Default admin booking duration must be between 5 and 240 minutes")]
        public int DefaultAdminBookingDurationMin { get; set; }

        // Minutes a booking may run past closing time. 0 = must finish by closing.
        [Range(0, 120, ErrorMessage = "Grace after close must be between 0 and 120 minutes")]
        public int GraceMinutesAfterClose { get; set; }

        // Minimum lead time (minutes) before a customer's slot. 0 = no lead time; capped at 24h.
        [Range(0, 1440, ErrorMessage = "Minimum advance booking must be between 0 and 1440 minutes")]
        public int MinAdvanceBookingMinutes { get; set; }

        // How far ahead (days) a customer may book. At least 1 day; capped at a year.
        [Range(1, 365, ErrorMessage = "Maximum advance booking must be between 1 and 365 days")]
        public int MaxAdvanceBookingDays { get; set; }

        // Hours before the appointment within which a cancellation forfeits the refund. 0 = always refund; capped at a week.
        [Range(0, 168, ErrorMessage = "Refund cutoff must be between 0 and 168 hours")]
        public int RefundCutoffHours { get; set; }

        /* Spacing of the slot times the picker offers. An allowed SET rather than a [Range]: the value has
         * to divide 60 or the grid walks off the hour (a 25-minute step gives 09:00, 09:25, 09:50, 10:15),
         * and that isn't a bound any range can express. Enforced here and not only in the dropdown, since
         * the dropdown isn't what the endpoint trusts. */
        [AllowedValues(5, 10, 15, 20, 30, ErrorMessage = "Slot step must be 5, 10, 15, 20 or 30 minutes")]
        public int SlotStepMin { get; set; }
    }
}
