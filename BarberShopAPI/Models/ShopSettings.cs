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

        /* Minutes a booking may run PAST the day's last shift end. The customer working-hours rule
         * becomes end <= (last shift end) + this. 0 keeps the strict "must finish by closing" behaviour;
         * a larger value lets the last client run over. Grace applies only to the final shift of the day,
         * never a mid-day split-shift gap. Customer-only, like the rest of the working-hours rule. */
        public int GraceMinutesAfterClose { get; set; }

        /* Minimum lead time (minutes) a customer must leave before the slot start - the booking must be
         * at least this far in the future. Customer-only: enforced in BookingsController.ValidateBookingTime
         * and mirrored by the customer slot picker (BarberDateAndTime.jsx). Staff bookings are exempt. */
        public int MinAdvanceBookingMinutes { get; set; }

        /* How far ahead (days) a customer may book - the booking start must be within this horizon.
         * Customer-only, same enforcement/mirroring as MinAdvanceBookingMinutes. */
        public int MaxAdvanceBookingDays { get; set; }

        /* Cancelling within this many hours of the appointment forfeits the customer's refund (the barber
         * has too little time to rebook). Enforced in BookingCanceller and mirrored by the staff booking
         * tables (BookingsTable.jsx / BarberBookings.jsx). Never applies to shop-side or forced refunds. */
        public int RefundCutoffHours { get; set; }
    }
}
