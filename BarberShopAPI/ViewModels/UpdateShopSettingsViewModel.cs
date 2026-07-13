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
    }
}
