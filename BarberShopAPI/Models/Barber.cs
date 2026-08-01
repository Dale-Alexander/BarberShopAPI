using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BarberShopAPI.Models
{
    public class Barber
    {
        [Key]
        public int Id { get; set; }
        [Required]
        public int UserId { get; set; }
        [ForeignKey("UserId")]
        public virtual User User { get; set; }
        public string? Bio { get; set; }
        public string? ImageUrl { get; set; }
        public virtual ICollection<Booking> Bookings { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public bool isActive { get; set; } = true;
        /* Two separate questions, deliberately not one flag:
         *   isActive           - is this person staff? Drives login, dashboard access and whether their
         *                        future bookings get cancelled on deactivation.
         *   AcceptsNewBookings - can a CUSTOMER pick them? Nothing else.
         * A barber working their notice is isActive=true, AcceptsNewBookings=false: customers can't book
         * them any more, but they keep their login and work off their own calendar until the last one is
         * done. Only two queries read this (BarbersController.GetBarbersWithBookings and
         * BookingsController.CreatePendingBookings) - everything else, especially the "has this barber
         * left?" checks that cancel and refund in-flight bookings, must stay on isActive alone. */
        public bool AcceptsNewBookings { get; set; } = true;
        public ICollection<ShopClosure>? Closures { get; set; }
    }
}
