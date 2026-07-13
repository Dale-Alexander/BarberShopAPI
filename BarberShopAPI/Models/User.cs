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
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? Email { get; set; }
        public string? Password { get; set; }
        public int? TokenVersion { get; set; }

        /* The forgot-password recovery flow (authController.cs: RequestPasswordReset /
         * ResetPassword) works off these two fields instead of a session/cookie, because
         * whoever is using it has by definition lost access to their account.
         *
         * PasswordResetTokenHash: only the SHA-256 hash of the reset token is stored here,
         * never the raw token itself - same reasoning as hashing Password, except SHA-256
         * (fast) is fine here instead of BCrypt (slow) because this token is a random
         * 256-bit value, not a guessable human password. If the DB ever leaked, this
         * column alone wouldn't let anyone use the reset link that's on its way to the
         * user's inbox.
         *
         * PasswordResetTokenExpiresAt: the link is only valid for 30 minutes from the
         * moment it's requested (see RequestPasswordReset). ResetPassword rejects the
         * token once this passes.
         *
         * Both fields get set together when a reset is requested, and both get nulled
         * out together once the reset succeeds - so a link can only ever be used once,
         * and requesting a new link automatically invalidates any older unused one
         * (it just overwrites these same two columns).
         */
        public string? PasswordResetTokenHash { get; set; }
        public DateTime? PasswordResetTokenExpiresAt { get; set; }
    }
}
