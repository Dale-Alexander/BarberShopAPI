using BarberShopAPI.Models;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class BarbersForAdminDisplayViewModel
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string? LastName { get; set; }
        public string ImageUrl { get; set; }
        public int TotalBookings { get; set; }
        // The admin team screen lists deactivated barbers too (under an "Inactive" tab), so it needs
        // the flag to badge them, and the email to prefill the read-only field in the reactivate modal
        // - reactivating routes through CreateBarber's revive-by-email branch.
        public bool IsActive { get; set; }
        // Independent of IsActive: an active barber can still be closed to new bookings while they work
        // out their notice. The card badges this state and the toggle flips it.
        public bool AcceptsNewBookings { get; set; }
        public string Email { get; set; }
    }
}
