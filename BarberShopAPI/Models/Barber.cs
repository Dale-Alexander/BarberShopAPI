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
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public bool isActive { get; set; } = true;
        public ICollection<ShopClosure>? Closures { get; set; }
    }
}
