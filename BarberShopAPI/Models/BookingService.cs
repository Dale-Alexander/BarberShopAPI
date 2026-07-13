using BarberShopAPI.Models.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BarberShopAPI.Models
{
    public class BookingService
    {
        [Key]
        public int Id { get; set; }
        public int BookingId { get; set; }
        [ForeignKey("BookingId")]
        public virtual Booking Booking { get; set; }
        public int ServiceId { get; set; }
        [ForeignKey("ServiceId")]
        public virtual Service Service { get; set; }
        public BookingServiceStatus Status { get; set; } = BookingServiceStatus.ACTIVE;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
