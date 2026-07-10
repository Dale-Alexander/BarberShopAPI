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
