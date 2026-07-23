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
            // Deliberately unfiltered: this admin-only screen shows deactivated barbers too, so the
            // admin can reach an ex-barber's history and revive a wrongly-deleted one. The customer-
            // facing barbers-with-bookings above stays active-only. Active first, then by name, so
            // the working roster leads.
            var BarbersList = (from b in _context.Barbers
                               orderby b.isActive descending, b.User.Name, b.User.Surname
                               select new BarbersForAdminDisplayViewModel
                               {
                                   Id = b.Id,
                                   FirstName = b.User.Name,
                                   LastName = b.User.Surname,
                                   ImageUrl = b.ImageUrl,
                                   TotalBookings = b.Bookings.Count(bk => bk.Status == BookingStatus.COMPLETED),
                                   IsActive = b.isActive,
                                   Email = b.User.Email
                               });
            var result = await BarbersList.ToListAsync();
            return Ok(result);
        }
        // Same rules customer names go through in BookingsController.IsValidName: non-empty,
        // at least 2 real characters, and no digits - a barber is a person, not "123".
        private bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Trim().Length < 2) return false;
            if (name.Any(char.IsDigit)) return false;
            // The name is split into first/last and stored in User.Name/User.Surname, each nvarchar(50).
            // Validate against the same split so an over-long part gets a clean 400 here instead of a
            // truncation error on save.
            var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var firstName = parts.Length > 0 ? parts[0] : "";
            var lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            if (firstName.Length > 50 || lastName.Length > 50) return false;
            return true;
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
            if (!IsValidName(request.FullName))
                return BadRequest(new { message = "Please enter a valid full name" });
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

                    // The email may already belong to a non-barber account (e.g. the admin, or a
                    // customer). There is no Barber row to revive in that case, so guard against a
                    // NullReferenceException on existingUser.Barber below and return a clean message.
                    if (existingUser.Barber == null)
                    {
                        return Conflict(new { message = "This email is already in use by another account" });
                    }

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

                        // A new photo replaces the old one; supplying none KEEPS what's already there.
                        // The admin team screen's Reactivate flow shows the barber's existing photo in the
                        // modal, so nulling the column when no file is picked would silently wipe a photo
                        // the admin was just looking at. Matches UpdateBarber's "leave as-is" semantics.
                        // When we do replace a locally-stored file, remember it and delete it after the
                        // save succeeds - same cleanup as UpdateService and DeleteBarber, so a superseded
                        // upload doesn't orphan in wwwroot. http URLs aren't ours to delete.
                        string oldLocalImage = null;
                        if (finalImageUrl != null)
                        {
                            if (!string.IsNullOrWhiteSpace(existingUser.Barber.ImageUrl)
                                && !existingUser.Barber.ImageUrl.StartsWith("http")
                                && existingUser.Barber.ImageUrl != finalImageUrl)
                                oldLocalImage = existingUser.Barber.ImageUrl;

                            existingUser.Barber.ImageUrl = finalImageUrl;
                        }
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

                        // Delete the superseded file only after the row is safely persisted - if the
                        // save above threw, we must not have removed the image the record still points at.
                        if (oldLocalImage != null)
                        {
                            var oldPath = Path.Combine("wwwroot", oldLocalImage.TrimStart('/'));
                            if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                        }

                        // Return the same shape as the create branch below, and the same shape the admin
                        // team list is built from - the caller drops this straight into its list. The id
                        // in particular was missing here, so a revived barber rendered with an undefined
                        // id and dead row actions until a refresh.
                        return Ok(new
                        {
                            message = "Barber created successfully",
                            id = existingUser.Barber.Id,
                            // The persisted value, not finalImageUrl - those differ when no new photo
                            // was supplied and the existing one was kept.
                            imageUrl = existingUser.Barber.ImageUrl,
                            firstName = firstName,
                            lastName = lastName,
                            email = existingUser.Email,
                            isActive = true,
                            totalBookings = await _context.Bookings.CountAsync(bk =>
                                bk.BarberId == existingUser.Barber.Id && bk.Status == BookingStatus.COMPLETED)
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
                        lastName = lastName,
                        email = user.Email,
                        isActive = true,
                        totalBookings = 0
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
        [HttpPatch("update/{id}")]
        public async Task<IActionResult> UpdateBarber(int id, [FromForm] UpdateBarberViewModel request)
        {
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .FirstOrDefault();
                return BadRequest(new { message = errors ?? "Invalid request" });
            }

            // Only edit live barbers - a soft-deleted row is treated as gone, and bringing one back is
            // CreateBarber's revive job, not this endpoint's. Include the User so we can update the name.
            var barber = await _context.Barbers.Include(b => b.User)
                .FirstOrDefaultAsync(b => b.Id == id && b.isActive);
            if (barber == null) return NotFound(new { message = "Barber not found" });

            if (request.FullName != null)
            {
                // IsValidName also caps each split part at 50 to match User.Name/Surname (nvarchar(50)).
                if (!IsValidName(request.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                var parts = request.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                barber.User.Name = parts.Length > 0 ? parts[0] : "";
                barber.User.Surname = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            }

            try
            {
                // A new upload always wins; otherwise a bare ImageUrl replaces it; otherwise the existing
                // image is left untouched. When we replace a locally-stored file we remember the old one
                // and delete it after the save so orphaned uploads don't pile up (same as UpdateService).
                string oldLocalImage = null;
                if (request.ImageFile != null)
                {
                    var extension = Path.GetExtension(request.ImageFile.FileName).ToLower();
                    var uploadsFolder = Path.Combine("wwwroot", "uploads");
                    Directory.CreateDirectory(uploadsFolder);
                    var fileName = Guid.NewGuid() + extension;
                    var filePath = Path.Combine(uploadsFolder, fileName);
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await request.ImageFile.CopyToAsync(stream);
                    }
                    if (!string.IsNullOrWhiteSpace(barber.ImageUrl) && !barber.ImageUrl.StartsWith("http"))
                        oldLocalImage = barber.ImageUrl;
                    barber.ImageUrl = $"/uploads/{fileName}";
                }
                else if (!string.IsNullOrWhiteSpace(request.ImageUrl))
                {
                    if (!string.IsNullOrWhiteSpace(barber.ImageUrl) && !barber.ImageUrl.StartsWith("http"))
                        oldLocalImage = barber.ImageUrl;
                    barber.ImageUrl = request.ImageUrl;
                }
                else if (request.RemoveImage)
                {
                    // Explicit removal - the barber image column is nullable, so clear it and drop the
                    // old local file. http URLs aren't ours to delete.
                    if (!string.IsNullOrWhiteSpace(barber.ImageUrl) && !barber.ImageUrl.StartsWith("http"))
                        oldLocalImage = barber.ImageUrl;
                    barber.ImageUrl = null;
                }

                await _context.SaveChangesAsync();

                // Delete the superseded file only after the row is safely persisted - if the save above
                // threw, we must not have removed the image the record still points at.
                if (oldLocalImage != null)
                {
                    var oldPath = Path.Combine("wwwroot", oldLocalImage.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }

                return Ok(new
                {
                    message = "Barber updated successfully",
                    id = barber.Id,
                    firstName = barber.User.Name,
                    lastName = barber.User.Surname,
                    imageUrl = barber.ImageUrl
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "An unexpected error occurred" });
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
