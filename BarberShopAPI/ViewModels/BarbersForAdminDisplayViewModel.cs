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
        public string Email { get; set; }
    }
}
