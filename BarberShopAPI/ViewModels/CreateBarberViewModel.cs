using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class CreateBarberViewModel
    {
        public IFormFile? ImageFile { get; set; }//file drom device
        [Url]
        public string? ImageUrl { get; set; }//web file
        [Required]
        [MaxLength(100)]
        public string FullName { get; set; }
        /*[Required]
        [Phone]
        public string Phone { get; set; }*/
        [Required]
        [MaxLength(100)]
        [EmailAddress]
        public string Email { get; set; }
        [Required]
        [MinLength(6, ErrorMessage = "Password must be at least 6 characters")]
        [MaxLength(100)]
        public string Password { get; set; }

    }
}
