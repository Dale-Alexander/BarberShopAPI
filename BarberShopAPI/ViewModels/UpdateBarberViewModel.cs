using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    // Every field is optional so this behaves as a PATCH: the admin sends only what changed and
    // untouched fields keep their stored value. Deliberately scoped to name + image only - email and
    // password are the barber's login credentials and are not editable here. Barber images are
    // optional (the column is nullable), so there's no "image required" guard like the one on services.
    public class UpdateBarberViewModel
    {
        public IFormFile? ImageFile { get; set; }//file from device
        [Url]
        public string? ImageUrl { get; set; }//web Url
        [MaxLength(100)]
        public string? FullName { get; set; }

        // Explicit "clear the photo" signal. Needed because in a PATCH an omitted image can't be told
        // apart from "remove it" - without this flag, no-image always means "leave as-is". A new
        // ImageFile/ImageUrl takes precedence over this (replacing beats removing).
        public bool RemoveImage { get; set; }
    }
}
