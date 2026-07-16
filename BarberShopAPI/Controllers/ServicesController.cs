using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ServicesController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public ServicesController(BarberShopContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetServices()
        {
            var ServicesList = (from s in _context.Services
                                where s.IsActive == true
                                select new ServicesDisplayViewModel
                                {
                                    Name = s.Name,
                                    Description = s.Description,
                                    DurationMin = s.DurationMin,
                                    Price = s.Price,
                                    ImageUrl = s.ImageUrl
                                });
            var result = await ServicesList.ToListAsync();
            return Ok(result);
        }
        [Authorize(Roles = "ADMIN")]
        /* ASP.NET code will check if the JWT token has a claim of type ClaimTypes.Role(check the authController) and it will
         * see if the role assigned to the token matches the allowed role in [Authorize]*/
        [HttpPost("create-service")]
        public async Task<IActionResult> CreateService([FromForm] CreateServiceViewModel request)
            /*  */
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);/* if some of the data annotations of the view model are violated */
            }
            // [Required] rejects null/empty but still lets whitespace-only ("   ") through, and it never
            // trims. Trim here and validate the result so the stored name is clean - this also keeps the
            // UX_Service_Name unique index honest, since " Haircut " and "Haircut" are different keys to it.
            var title = request.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
                return BadRequest(new { message = "Service name must be at least 2 characters" });
            var description = request.Description?.Trim();
            if (string.IsNullOrWhiteSpace(description))
                return BadRequest(new { message = "Description is required" });
            try
            {
                string finalImageUrl = null;
                if(request.ImageFile != null)
                    /* Check if the image is uploaded from their device
                     */
                {
                    var uploadsFolder = Path.Combine("wwwroot", "uploads");
                    Directory.CreateDirectory(uploadsFolder);
                    /* Path.combine builds a platform independent path
                     * to the uploads folder inside your "wwwroot".
                     Example:C:\Project\wwwroot\uploads
                    Directory.CreateDirectory ensures the folder exists. If it
                    already exists it does nothing*/
                    var fileName = Guid.NewGuid() + Path.GetExtension(request.ImageFile.FileName);
                    var filePath = Path.Combine(uploadsFolder, fileName);

                    /* Guid.NewGuid() generates a unique ID, so each uploaded file has a unique name
                     * Path.GetExtension keeps the original file extension.
                     * Path.Combine Combines the uploads folder path with the
                     * new unique file name. The is the full path on your server where the file
                     will be saved. */
                    using var stream = new FileStream(filePath, FileMode.Create);
                    await request.ImageFile.CopyToAsync(stream);
                    /* FileStream opens a stream to write the file to disk. FileMode.Create
                     * means it creates the file or overwrites if it exists(shouldnt happen
                     because of GUID) CopyToAsync(stream) writes the uploaded file content
                    into the file asynchronously*/
                    finalImageUrl = $"/uploads/{fileName}"; 
                    //stores the relative URL
                    //example: /uploads/3f8b2c1a-9d8e-4a56-823f-f7a1d3c2a123.png
                }
                else if (!string.IsNullOrWhiteSpace(request.ImageUrl))
                {
                    finalImageUrl = request.ImageUrl;
                }
                // Services.ImageUrl is NOT NULL, so a service with neither an uploaded file nor a URL
                // would throw on save. Reject up front with a clean 400 instead of a 500.
                if (string.IsNullOrWhiteSpace(finalImageUrl))
                    return BadRequest(new { message = "A service image is required" });
                    var service = new Service
                    {
                        Name = title,
                        Price = request.Price,
                        DurationMin = request.DurationMin,
                        Description = description,
                        ImageUrl = finalImageUrl
                    };
                _context.Services.Add(service);
                await _context.SaveChangesAsync();
                return Ok(service);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601))
            {
                return Conflict(new { message = "A service with this name already exists" });
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, "An unexpected error occurred");
            }

        }

        [Authorize(Roles = "ADMIN")]
        [HttpPatch("update-service/{id}")]
        public async Task<IActionResult> UpdateService(int id, [FromForm] UpdateServiceViewModel request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            // Only edit live services. A soft-deleted (IsActive == false) row is treated as gone, same
            // as GetServices which never surfaces it - reviving it is the delete endpoint's job, not this one.
            var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == id && s.IsActive);
            if (service == null) return NotFound(new { message = "Service not found" });

            // Trim-and-validate mirrors CreateService: [MaxLength] alone lets whitespace-only names
            // through and never trims, which would poison the UX_Service_Name unique index.
            if (request.Title != null)
            {
                var title = request.Title.Trim();
                if (title.Length < 2)
                    return BadRequest(new { message = "Service name must be at least 2 characters" });
                service.Name = title;
            }
            if (request.Description != null)
            {
                var description = request.Description.Trim();
                if (string.IsNullOrWhiteSpace(description))
                    return BadRequest(new { message = "Description is required" });
                service.Description = description;
            }
            if (request.DurationMin.HasValue) service.DurationMin = request.DurationMin.Value;
            if (request.Price.HasValue) service.Price = request.Price.Value;

            try
            {
                // A new upload always wins; otherwise a bare ImageUrl replaces it; otherwise the
                // existing image is left untouched. When we replace a locally-stored file we delete
                // the old one so orphaned uploads don't pile up in wwwroot (same cleanup as DeleteBarber).
                string oldLocalImage = null;
                if (request.ImageFile != null)
                {
                    var uploadsFolder = Path.Combine("wwwroot", "uploads");
                    Directory.CreateDirectory(uploadsFolder);
                    var fileName = Guid.NewGuid() + Path.GetExtension(request.ImageFile.FileName);
                    var filePath = Path.Combine(uploadsFolder, fileName);
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(stream);
                    }
                    if (!string.IsNullOrWhiteSpace(service.ImageUrl) && !service.ImageUrl.StartsWith("http"))
                        oldLocalImage = service.ImageUrl;
                    service.ImageUrl = $"/uploads/{fileName}";
                }
                else if (!string.IsNullOrWhiteSpace(request.ImageUrl))
                {
                    if (!string.IsNullOrWhiteSpace(service.ImageUrl) && !service.ImageUrl.StartsWith("http"))
                        oldLocalImage = service.ImageUrl;
                    service.ImageUrl = request.ImageUrl;
                }

                await _context.SaveChangesAsync();

                // Delete the superseded file only after the row is safely persisted - if the save
                // above threw, we must not have removed the image the record still points at.
                if (oldLocalImage != null)
                {
                    var oldPath = Path.Combine("wwwroot", oldLocalImage.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }
                return Ok(service);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601))
            {
                return Conflict(new { message = "A service with this name already exists" });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, "An unexpected error occurred");
            }
        }

        [Authorize(Roles = "ADMIN")]
        [HttpDelete("delete/{id}")]
        public async Task<IActionResult> DeleteService(int id)
        {
            try
            {
                // Soft delete, matching DeleteBarber and the Service.IsActive/GetServices contract.
                // Services are referenced by historical BookingService rows, so a hard delete would
                // break booking history and any completed-booking price/name lookups. Flipping IsActive
                // hides it from the customer catalogue while keeping past bookings intact.
                var service = await _context.Services.FirstOrDefaultAsync(s => s.Id == id);
                if (service == null) return NotFound(new { message = "Service not found" });
                if (!service.IsActive) return BadRequest(new { message = "This service is already inactive" });

                if (!string.IsNullOrWhiteSpace(service.ImageUrl) && !service.ImageUrl.StartsWith("http"))
                {
                    var filePath = Path.Combine("wwwroot", service.ImageUrl.TrimStart('/'));
                    if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
                }

                service.IsActive = false;
                await _context.SaveChangesAsync();
                return Ok(new { message = "Service deleted successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "An unexpected error occurred" });
            }
        }
    }
}
