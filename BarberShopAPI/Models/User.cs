using BarberShopAPI.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.Models
{
    [Index(nameof(Email), IsUnique = true)]
    [Index(nameof(Phone), IsUnique = true)]
    public class User
    {
        [Key]
        public int Id { get; set; }
        [MaxLength(100)]
        public string? Name { get; set; }
        [MaxLength(100)]
        public string? Surname { get; set; }

        [Phone]
        public string? Phone { get; set; }
        public Role Role { get; set; } = Role.CUSTOMER;
        public virtual Barber? Barber { get; set; }
        public virtual ICollection<Booking> Bookings { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public string? Email { get; set; }
        public string? Password { get; set; }
        public int? TokenVersion { get; set; }
    }
}
