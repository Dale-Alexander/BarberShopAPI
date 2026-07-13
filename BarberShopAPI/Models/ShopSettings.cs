using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.Models
{
    /* Single-row shop-wide configuration table (convention: the only row has Id == 1, seeded in
     * BarberShopContext.OnModelCreating). Holds settings that are policy, not per-entity data.
     * Kept as a typed singleton (rather than key/value) so future toggles can be added as columns. */
    public class ShopSettings
    {
        [Key]
        public int Id { get; set; }

        /* Minutes of gap required AFTER each booking before the next one may start (cleanup/reset
         * time between clients). 0 disables it, leaving bookings back-to-back. Applied only to the
         * booking-vs-booking overlap checks in BookingsController - never to working-hours or
         * closure checks. */
        public int BufferMin { get; set; }

        /* Default duration (minutes) pre-filled when an admin creates a booking. Admin bookings
         * aren't tied to services, so this seeds the on-page duration control in the booking picker
         * (the admin can still override it per booking). Only a default - not enforced anywhere. */
        public int DefaultAdminBookingDurationMin { get; set; }

        /* Minutes a booking may run PAST closing time. The customer working-hours rule becomes
         * end <= ShopClose + this. 0 keeps the strict "must finish by closing" behaviour; a larger
         * value lets the last client run over. Customer-only, like the rest of the working-hours rule. */
        public int GraceMinutesAfterClose { get; set; }
    }
}
