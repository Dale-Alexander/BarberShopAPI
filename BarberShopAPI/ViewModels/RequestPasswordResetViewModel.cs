using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    // Step 1 of the forgot-password flow (ForgotPassword.jsx). Just an email -
    // no password yet, since the whole point is the caller doesn't have one that works.
    public class RequestPasswordResetViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }
}
