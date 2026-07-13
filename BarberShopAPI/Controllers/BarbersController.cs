using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using BarberShopAPI.Common;
namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BarbersController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public BarbersController(BarberShopContext context)
        {
            _context = context;
        }

        [HttpGet("barbers-with-bookings")]
        public async Task<IActionResult> GetBarbersWithBookings()
        {
            try
            {
                var todayDate = ShopClock.Today;
                var currentTime = ShopClock.TimeOfDay;
                var nowMalta = ShopClock.Now;
                /* it is important that these are declared outside because EF Core cant
                 * translate them*/
                var barbers = await _context.Barbers.Where(b => b.isActive == true)
                .Select(b => new BarberBookingViewModel
                {
                    BarberId = b.Id,
                    BarberName = b.User.Name,
                    BarberSurname = b.User.Surname,
                    ImageUrl = b.ImageUrl,
                    Bookings = b.Bookings.Where(b => b.Status != BookingStatus.CANCELLED
                    && b.StartDateTime >= nowMalta)
                    .Select(bk => new BookingsDateAndTimeViewModel
                    {
                        BookingId = bk.Id,
                        StartDateTime = bk.StartDateTime,
                        DurationMin = bk.DurationMin
                    }).ToList(),
                    DateClosures = b.Closures.Where(c => c.IsActive == true
                        && (
                            (c.EndDate == null && (
                                (c.IsFullDay && c.StartDate >= todayDate) ||
                                (!c.IsFullDay && c.StartDate >= todayDate && c.EndTime > currentTime)
                            )) ||
                            (c.EndDate != null && c.EndDate >= todayDate) // range: still ongoing or future
                        ))
                    .Select(c => new BarberShopClosureViewModel
                    {
                        StartDate = c.StartDate,
                        EndDate = c.EndDate,
                        StartTime = c.StartTime,
                        EndTime = c.EndTime,
                        IsFullDay = c.IsFullDay
                    }).ToList()
                }).ToListAsync();

                //fetch shop-wide closures seperately in the same request
                var shopClosures = await _context.ShopClosures.Where(c =>
                    c.IsActive == true && (
                        (c.EndDate == null && (
                            (c.IsFullDay && c.StartDate >= todayDate) ||
                            (!c.IsFullDay && c.StartDate >= todayDate && c.EndTime > currentTime)
                        )) ||
                        (c.EndDate != null && c.EndDate >= todayDate) // range: still ongoing or future
                    ))
                    .Select(c => new BarberShopClosureViewModel
                    {
                        StartDate = c.StartDate,
                        EndDate = c.EndDate,
                        StartTime = c.StartTime,
                        EndTime = c.EndTime,
                        IsFullDay = c.IsFullDay
                    }).ToListAsync();
                
                // Expose the between-booking buffer and grace-after-close so the customer slot picker
                // greys out the same slots the backend will reject (see ShopSettings).
                var settings = await _context.ShopSettings
                    .Select(s => new { s.BufferMin, s.GraceMinutesAfterClose })
                    .FirstAsync();

                return Ok(new
                {
                    barbers,
                    shopClosures,
                    bufferMin = settings.BufferMin,
                    graceMinutesAfterClose = settings.GraceMinutesAfterClose
                });
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, "Unexpected Server error occurred");
            }
        }

        [HttpGet("admin")]
        [Authorize(Roles = "ADMIN")]
        public async Task<IActionResult> GetBarbersForAdmin()
        {
            var BarbersList = (from b in _context.Barbers
                               where b.isActive == true
                               select new BarbersForAdminDisplayViewModel
                               {
                                   Id = b.Id,
                                   FirstName = b.User.Name,
                                   LastName = b.User.Surname,
                                   ImageUrl = b.ImageUrl,
                                   TotalBookings = b.Bookings.Count(bk => bk.Status == BookingStatus.COMPLETED)
                               });
            var result = await BarbersList.ToListAsync();
            return Ok(result);
        }
        [Authorize]
        [HttpGet("{barberId}/bookings")]
        public async Task<IActionResult> GetBarberBookings(int barberId)
        //Here the parameter barberId in the function refers to the barberId in HttpGet(...). The barberId in HttpGet refers
        //to the barberId being passed from the frontend
        {
            var barber = await _context.Barbers
                .Where(b => b.Id == barberId && b.isActive == true)
                .Select(b => new
                {
                    BarberName = b.User.Name,
                    BarberSurname = b.User.Surname,
                    BarberImageUrl = b.ImageUrl,
                    BarberActive = b.isActive,
                    Bookings = b.Bookings.Where(booking => booking.Status == BookingStatus.COMPLETED)
                    .Select(bk => new
                    {
                        bk.UserId,
                        Name = bk.User.Name,
                        Surname = bk.User.Surname,
                        bk.StartDateTime,
                        Phone = bk.User.Phone,
                        Services = bk.Services
                            .Select(s => new
                            {
                                ServiceName = s.Service.Name,
                                s.Service.Price
                            })
                    })
                }).FirstOrDefaultAsync();

            if (barber == null) return NotFound();
            return Ok(barber);
        }


        private bool IsUniqueConstraintViolation(DbUpdateException ex)
        /* important that this is private otherwise Swagger might think that it is an endpoint and it doesnt see
         * that is has [HttpPost/Get/etc] so it will result in an error because an endpoint reques these things but in reality
         this is not an endpoint*/
        {
            if (ex.InnerException is SqlException sqlEx)
            {
                return sqlEx.Number == 2627 || sqlEx.Number == 2601;
            }
            return false;
        }
        [Authorize(Roles = "ADMIN")]
        [HttpPost("create-barber")]
        public async Task<IActionResult> CreateBarber([FromForm] CreateBarberViewModel request)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .FirstOrDefault();
                return BadRequest(new { message = errors ?? "Invalid request" });
            }
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                string finalImageUrl = null;
                if (request.ImageFile != null)
                {
                    var extension = Path.GetExtension(request.ImageFile.FileName).ToLower();
                    var uploadsFolder = Path.Combine("wwwroot", "uploads");
                    Directory.CreateDirectory(uploadsFolder);

                    //var fileName = Guid.NewGuid() + Path.GetExtension(request.ImageFile.FileName);

                    var fileName = Guid.NewGuid() + extension;
                    var filePath = Path.Combine(uploadsFolder, fileName);

                    using var stream = new FileStream(filePath, FileMode.Create);
                    //using var image = await Image.LoadAsync(request.ImageFile.OpenReadStream());
                    //await image.SaveAsJpegAsync(filePath);

                    await request.ImageFile.CopyToAsync(stream);

                    finalImageUrl = $"/uploads/{fileName}";
                }
                else if (!string.IsNullOrWhiteSpace(request.ImageUrl))
                {
                    finalImageUrl = request.ImageUrl;
                }
                var parts = request.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                //the above splits by space and ignores extra spaces
                string firstName = parts.Length > 0 ? parts[0] : "";
                string lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";

                var existingUser = await _context.Users.Include(u => u.Barber).FirstOrDefaultAsync(b => b.Email == request.Email);

                if (existingUser != null)
                {
                    Console.WriteLine(existingUser);
                    Console.WriteLine($"Barber: {existingUser.Barber}");

                    if (existingUser.Barber.isActive == true)
                    {
                        return Conflict(new { message = "A barber with this email already exists" });
                    }
                    else
                    {
                        existingUser.Name = firstName;
                        existingUser.Surname = lastName;
                        existingUser.Email = request.Email;
                        existingUser.Password = BCrypt.Net.BCrypt.HashPassword(request.Password);
                        existingUser.Role = Role.BARBER;
                        existingUser.Barber.isActive = true;
                        existingUser.Barber.ImageUrl = finalImageUrl;
                        existingUser.TokenVersion++;
                        /* Why increment instead of resetting to 0 ? 
                         * Resetting to 0 could theoretically match an old token that also had version 0 from before 
                         * they were deactivated, letting them log in without fresh credentials. Incrementing 
                         * guarantees any old tokens are invalidated and they must log in again with the new password you just set.
                         
                         But the real problem is this scenario:

                        Barber has TokenVersion = 0, gets deactivated, TokenVersion incremented to 1
                        Barber logs in after deactivation somehow and gets a JWT with tokenVersion: 1
                        Admin revives them, doesn't increment — TokenVersion stays at 1
                        That JWT from step 2 still works — the barber never needed to log in fresh with their new credentials

                        Essentially, not incrementing on revival means you can't guarantee the barber is using the credentials the admin just set.
                        They could be using a token issued at any point when their TokenVersion happened to be 1.
                        The increment on revival forces them to log in fresh, proving they have the new password the admin assigned them.*/
                        await _context.SaveChangesAsync();
                        await transaction.CommitAsync();
                        return Ok(new
                        {
                            message = "Barber created successfully",
                            imageUrl = finalImageUrl,
                            firstName = firstName,
                            lastName = lastName
                        });

                        //otherwise create a new barber
                    }
                }
                else
                {
                    var user = new User
                    {
                        Name = firstName,
                        Surname = lastName,
                        Email = request.Email,
                        Password = BCrypt.Net.BCrypt.HashPassword(request.Password),
                        Role = Role.BARBER,
                        TokenVersion = 0
                    };

                    var barber = new Barber
                    {
                        User = user,
                        ImageUrl = finalImageUrl,
                    };

                    _context.Users.Add(user);
                    _context.Barbers.Add(barber);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    Console.WriteLine("User.barber:", user.Barber);
                    return Ok(new
                    {
                        message = "Barber created successfully",
                        id = barber.Id,
                        imageUrl = finalImageUrl,
                        firstName = firstName,
                        lastName = lastName
                    });
                }
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                await transaction.RollbackAsync();
                var sqlEx = ex.InnerException as SqlException;
                string message = "This barber already exists";
                if (sqlEx != null)
                {
                    if (sqlEx.Message.Contains("Email"))
                        message = "A barber with this email already exists";
                }
                return Conflict(new { message });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "An unexpected error occured" });
            }
        }
        [Authorize(Roles = "ADMIN")]
        [HttpDelete("delete/{id}")]
        public async Task<IActionResult> DeleteBarber(int id)
        {
            try
            {
                var barber = await _context.Barbers.FirstOrDefaultAsync(b => b.Id == id);
                if (barber == null) return NotFound(new { message = "Barber not found" });
                if (!barber.isActive) return BadRequest(new { message = "This barber is already inactive" });
                if (!string.IsNullOrWhiteSpace(barber.ImageUrl) && !barber.ImageUrl.StartsWith("http"))
                {
                    var filePath = Path.Combine("wwwroot", barber.ImageUrl.TrimStart('/'));
                    /* the stored imageUrl looks like /uploads/abc.jpg. If you do
                     * Path.Combine("wwwroot", "/uploads/abc.jpg"), the leading slash makes it 
                     treat the second part as an absolute path and ignore wwwroot entirely. Trimming
                    it gives uploads/abc.jpg so the combine correctly produces wwwroot/uploads/abc.jpg*/
                    if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
                }
                barber.isActive = false;

                var user = await _context.Users.FindAsync(barber.UserId);
                if (user != null) user.TokenVersion++;
                /* That's it. When the deactivated barber makes their next request, the middleware will see the token version mismatch and return a 401 — 
                 * locking them out instantly without needing any extra middleware logic.
                 * Otherwise, If a barber gets deactivated, their JWT is still valid and they can 
                 * still access the dashboard until it expires — which is a real security hole.*/
                await _context.SaveChangesAsync();
                return Ok(new { message = "Barber deleted successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "An unexpected error occurred" });
            }
        }
    }
}
