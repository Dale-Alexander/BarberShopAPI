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
                    var service = new Service
                    {
                        Name = request.Title,
                        Price = request.Price,
                        DurationMin = request.DurationMin,
                        Description = request.Description,
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
    }
}
