using Microsoft.Identity.Client;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class NewPasswordViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
        [Required]
        [MaxLength(40)]
        public string newPassword { get; set; }
        [Required]
        [MaxLength(40)]
        public string confirmNewPassword { get; set; }
    }
}
