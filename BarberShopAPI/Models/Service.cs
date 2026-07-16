using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BarberShopAPI.Models
{
    public class Service
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        [Column(TypeName = "decimal(6,2)")]
        public decimal Price { get; set; }
        public int DurationMin { get; set; }
        public bool IsActive { get; set; } = true;
        [MaxLength(1000)]
        public string ImageUrl { get; set; }
        public virtual ICollection<BookingService> Bookings { get; set; }
    }
}
