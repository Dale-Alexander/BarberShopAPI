using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class CreateServiceViewModel
    {
        [MaxLength(1000)]
        public IFormFile? ImageFile { get; set; }//file from device
        [Url]
        [MaxLength(1000)]
        public string? ImageUrl { get; set; } // web Url
        [Required]
        [MinLength(2, ErrorMessage = "Service name must be at least 2 characters")]
        [MaxLength(100)]
        public string Title { get; set; }
        [Required]
        [MaxLength(255)]
        public string Description { get; set; }
        [Required]
        [Range(1, 300)]
        public int DurationMin { get; set; }
        [Required]
        [Range(0.01,1000)]
        public decimal Price { get; set; }

    }
}
