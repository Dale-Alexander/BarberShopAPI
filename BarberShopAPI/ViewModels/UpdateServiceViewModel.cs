using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    // Every field is optional so this behaves as a PATCH: the admin sends only what changed and
    // untouched fields keep their stored value. Nullable value types (int?/decimal?) let us tell
    // "not provided" apart from a real 0, which the [Range] attributes would otherwise reject anyway.
    public class UpdateServiceViewModel
    {
        [MaxLength(1000)]
        public IFormFile? ImageFile { get; set; }//file from device
        [Url]
        [MaxLength(1000)] // matches the Services.ImageUrl column (nvarchar(1000)).
        public string? ImageUrl { get; set; } // web Url
        [MaxLength(100)]
        public string? Title { get; set; }
        [MaxLength(255)]
        public string? Description { get; set; }
        [Range(1, 300)]
        public int? DurationMin { get; set; }
        [Range(0.01, 1000)]
        public decimal? Price { get; set; }
    }
}
