using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    // Step 2 of the forgot-password flow (ResetPassword.jsx, reached via the emailed
    // link). Token is the raw value from the URL - the backend re-hashes it and compares
    // against User.PasswordResetTokenHash, it never trusts an email/user id from the client.
    public class ResetPasswordViewModel
    {
        [Required]
        public string Token { get; set; }
        [Required]
        [MinLength(6, ErrorMessage = "Password must be at least 6 characters")]
        [MaxLength(40)]
        public string NewPassword { get; set; }
        [Required]
        [MaxLength(40)]
        public string ConfirmNewPassword { get; set; }
    }
}
