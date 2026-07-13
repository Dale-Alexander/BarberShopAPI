using BarberShopAPI.Models.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BarberShopAPI.Models
{
    public class Payment
    {
        [Key]
        public int Id { get; set; }
        public int BookingId { get; set; }
        [ForeignKey("BookingId")]
        public virtual Booking Booking { get; set; }

        [Column(TypeName = "decimal(6,2)")]
        public decimal? Amount{ get; set; }
        public PaymentMethod Method { get; set; }
        public PaymentStatus Status {get;set;} = PaymentStatus.PENDING;
        public string? StripePaymentIntentId { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
