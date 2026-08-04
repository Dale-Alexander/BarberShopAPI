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

        /* `includeUnbookable` switches this from the customer roster to the staff one, and is honoured only
         * for a signed-in ADMIN or BARBER - the endpoint is anonymous, so an unauthenticated caller passing
         * it still gets the customer view.
         *
         * The difference is AcceptsNewBookings, which per the Barber model means "can a CUSTOMER pick
         * them?" and nothing else. Staff were reading the customer roster, so a barber closed to new
         * bookings vanished from the picker - including from their OWN edit page, leaving them unable to
         * move the appointments they were still working through, which is the whole point of that toggle.
         * It also put the UI out of step with the backend, where CreateAdminBooking and UpdateBooking have
         * only ever gated staff on isActive. */
        [HttpGet("barbers-with-bookings")]
        public async Task<IActionResult> GetBarbersWithBookings([FromQuery] bool includeUnbookable = false)
        {
            try
            {
                var todayDate = ShopClock.Today;
                var currentTime = ShopClock.TimeOfDay;
                var nowMalta = ShopClock.Now;
                /* it is important that these are declared outside because EF Core cant
                 * translate them*/
                var staffView = includeUnbookable && (User.IsInRole("ADMIN") || User.IsInRole("BARBER"));
                // Customer-facing roster answers "who can I book?" - which needs BOTH flags. A barber
                // working their notice is still staff (isActive) but must not appear in the customer picker.
                var barbers = await _context.Barbers.Where(b => b.isActive == true && (staffView || b.AcceptsNewBookings))
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
                
                // Each active barber's weekly schedule versions. Only current/future versions matter
                // for bookable dates (a version wholly in the past can't govern today..horizon), so
                // filter to EffectiveTo == null OR EffectiveTo >= today. Sent as a template the picker
                // resolves per chosen date - far smaller than resolving every day in the horizon.
                var barberIds = barbers.Select(b => b.BarberId).ToList();
                var schedules = await _context.BarberSchedules
                    .Where(s => barberIds.Contains(s.BarberId)
                                && (s.EffectiveTo == null || s.EffectiveTo >= todayDate))
                    .Select(s => new
                    {
                        s.BarberId,
                        Vm = new BarberScheduleViewModel
                        {
                            EffectiveFrom = s.EffectiveFrom,
                            EffectiveTo = s.EffectiveTo,
                            Shifts = s.Shifts
                                .OrderBy(sh => sh.DayOfWeek).ThenBy(sh => sh.StartTime)
                                .Select(sh => new BarberScheduleShiftViewModel
                                {
                                    DayOfWeek = (int)sh.DayOfWeek,
                                    StartTime = sh.StartTime,
                                    EndTime = sh.EndTime
                                }).ToList()
                        }
                    }).ToListAsync();

                var schedulesByBarber = schedules.GroupBy(x => x.BarberId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Vm).ToList());
                foreach (var b in barbers)
                    b.Schedule = schedulesByBarber.TryGetValue(b.BarberId, out var list)
                        ? list : new List<BarberScheduleViewModel>();

                // Expose the buffer, grace-after-close and lead-time/horizon so the customer slot picker
                // greys out / disables the same slots the backend will reject (see ShopSettings).
                var settings = await _context.ShopSettings
                    .Select(s => new { s.BufferMin, s.GraceMinutesAfterClose, s.MinAdvanceBookingMinutes, s.MaxAdvanceBookingDays })
                    .FirstAsync();

                return Ok(new
                {
                    barbers,
                    shopClosures,
                    bufferMin = settings.BufferMin,
                    graceMinutesAfterClose = settings.GraceMinutesAfterClose,
                    minAdvanceBookingMinutes = settings.MinAdvanceBookingMinutes,
                    maxAdvanceBookingDays = settings.MaxAdvanceBookingDays
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
                                   AcceptsNewBookings = b.AcceptsNewBookings,
                                   Email = b.User.Email,
                                   IsAdmin = b.User.Role == Role.ADMIN
                               });
            var result = await BarbersList.ToListAsync();
            return Ok(result);
        }
        // The default schedule handed to a brand-new barber (and to a revived one with no schedule to
        // reuse). Moved to Common/DefaultSchedule.cs now that the admin seed needs the same hours - see
        // that file for why it isn't copy-pasted into both.
        private static BarberSchedule BuildDefaultSchedule() => DefaultSchedule.Build();

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
            // Validates and splits in one go - the halves are used further down, where this used to
            // re-split the same string by hand.
            if (!PersonName.TrySplit(request.FullName, out var firstName, out var lastName))
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
                        /* An ADMIN who also cuts hair owns a Barber row like anyone else, so reactivating
                         * their chair lands here - and the three credential lines below would quietly take
                         * the shop off them: demoted to BARBER, password overwritten with whatever was typed
                         * in the modal, and signed out. Recoverable only from the database.
                         *
                         * Reviving a chair says nothing about the person's rank or their login. For an admin
                         * we reactivate the barber row and touch neither: no role change, no password, no
                         * TokenVersion bump. The credential reset exists for a returning EMPLOYEE, where the
                         * admin is handing out a fresh password and needs to be sure it's the one in use. */
                        var isAdminAccount = existingUser.Role == Role.ADMIN;

                        existingUser.Name = firstName;
                        existingUser.Surname = lastName;
                        existingUser.Email = request.Email;
                        if (!isAdminAccount)
                        {
                            existingUser.Password = BCrypt.Net.BCrypt.HashPassword(request.Password);
                            existingUser.Role = Role.BARBER;
                        }
                        existingUser.Barber.isActive = true;
                        // Reset the bookable flag too. A barber who was closed to new bookings while working
                        // their notice keeps that flag through deactivation, so without this they'd come back
                        // live on the team screen but invisible in the customer picker, with no obvious cause.
                        existingUser.Barber.AcceptsNewBookings = true;

                        // Reuse the barber's own schedule if it survived deactivation (soft-delete keeps
                        // the rows), so a revived barber comes back with their real hours rather than a
                        // generic default. Only seed a default when there's no current version to reuse -
                        // e.g. barbers deactivated before scheduling existed have none. Keeps the
                        // "no barber is ever unbookable" guarantee without overwriting real hours.
                        var hasCurrentSchedule = await _context.BarberSchedules
                            .AnyAsync(s => s.BarberId == existingUser.Barber.Id && s.EffectiveTo == null);
                        if (!hasCurrentSchedule)
                        {
                            var seed = BuildDefaultSchedule();
                            seed.BarberId = existingUser.Barber.Id;
                            _context.BarberSchedules.Add(seed);
                        }

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
                        // Skipped for an admin: the reasoning below is about forcing a returning employee
                        // onto the new password, and no new password was set for them. Bumping it would
                        // only bounce the owner to the login screen for reopening their own chair.
                        if (!isAdminAccount) existingUser.TokenVersion++;
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

                        /* The bookings this barber's departure stranded are fine again now that they're
                         * back: same barber, same time, and the note saying they'd left is no longer true.
                         * Nothing is unflagged here - the note may have picked up other problems since
                         * (see BookingReview), and only the admin can judge that. So they're listed, and
                         * the admin clears each one having read it, exactly as with a widened schedule.
                         * Bookings already reassigned or cancelled while the barber was away won't match:
                         * a reassigned one sits on a different chair and a cancelled one isn't COMPLETED. */
                        var nowMalta = ShopClock.Now;
                        var revivedBarberId = existingUser.Barber.Id;
                        var stranded = await _context.Bookings
                            .Include(bk => bk.User)
                            .Where(bk => bk.BarberId == revivedBarberId
                                         && bk.Status == BookingStatus.COMPLETED
                                         && bk.StartDateTime > nowMalta
                                         && bk.NeedsReview
                                         && bk.ReviewReason != null
                                         && bk.ReviewReason.Contains(ReviewMarkers.BarberLeft))
                            .ToListAsync();

                        /* Scoped to THIS barber being back and nothing else. A closure created over the slot
                         * while they were away leaves its own note, which the admin reads - filtering those
                         * out here would hide that the departure problem really is resolved. Same rule as the
                         * widened-hours list: each one vouches for its own dimension only, and says on each
                         * row (StillBlockedBy) whether anything else about the slot is still in the way. */
                        var backOnDuty = await BookingSlotGuard.DescribeAsync(_context, stranded);

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
                            acceptsNewBookings = true,
                            totalBookings = await _context.Bookings.CountAsync(bk =>
                                bk.BarberId == existingUser.Barber.Id && bk.Status == BookingStatus.COMPLETED),
                            // Empty on the create branch below - a brand-new barber has stranded nothing.
                            backOnDuty
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

                    // Seed the default schedule via the schedule's own Barber nav (Barber has no inverse
                    // collection). EF resolves BarberId from the same SaveChanges, so the barber is
                    // bookable immediately - the admin is routed to the schedule editor to refine it.
                    var schedule = BuildDefaultSchedule();
                    schedule.Barber = barber;
                    _context.BarberSchedules.Add(schedule);

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
                        acceptsNewBookings = true,
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
                // TrySplit also caps each half at 50 to match User.Name/Surname (nvarchar(50)), and hands
                // back the exact halves it validated - so what's checked is what gets stored.
                if (!PersonName.TrySplit(request.FullName, out var firstName, out var lastName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                barber.User.Name = firstName;
                barber.User.Surname = lastName;
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

        /* Opens/closes a barber to NEW customer bookings without deactivating them. This is the
         * "working their notice" switch: they stay staff, keep their login and their calendar, and honour
         * or cancel their remaining appointments themselves - customers just can't pick them any more.
         *
         * Existing bookings are deliberately untouched. Cancelling and refunding is what DeleteBarber does,
         * and the whole point of this endpoint is to stop new bookings WITHOUT that.
         *
         * ADMIN-only: a barber must not be able to hide themselves from the roster. */
        [Authorize(Roles = "ADMIN")]
        [HttpPatch("{id}/accepting-bookings")]
        public async Task<IActionResult> SetAcceptingBookings(int id, [FromBody] SetAcceptsNewBookingsViewModel request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(new { message = "acceptsNewBookings is required" });

                // Live barbers only, matching UpdateBarber. A deactivated row is already unbookable through
                // the isActive half of the filter, so flipping this on one would be a no-op that reads like
                // it did something.
                var barber = await _context.Barbers.FirstOrDefaultAsync(b => b.Id == id && b.isActive);
                if (barber == null) return NotFound(new { message = "Barber not found" });

                barber.AcceptsNewBookings = request.AcceptsNewBookings!.Value;
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = barber.AcceptsNewBookings
                        ? "This barber is taking new bookings again"
                        : "This barber is no longer taking new bookings",
                    acceptsNewBookings = barber.AcceptsNewBookings
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "An unexpected error occurred" });
            }
        }

        /* Deactivating no longer destroys the barber's upcoming work. Firing someone and cancelling every
         * appointment they had are two different decisions, and only the admin can make the second one -
         * some of those customers will happily see another barber. So:
         *
         *   CONFIRMED (COMPLETED) bookings  - left exactly as they are and flagged into the Needs Review
         *                                     worklist, where the admin reassigns them to another barber
         *                                     (bookings/update-booking) or cancels them (bookings/cancel).
         *                                     Nothing is refunded and no customer is emailed here: the
         *                                     appointment still stands until the admin decides otherwise.
         *   PENDING bookings                - cancelled outright, as before. Their payment is still in
         *                                     flight and they were never confirmed to anyone, so there is
         *                                     nothing to honour and nothing to reassign (UpdateBooking
         *                                     refuses a PENDING booking anyway).
         *
         * `confirm` is still required whenever there's upcoming work, because the admin needs to see what
         * they're taking on before the barber loses their login. */
        [Authorize(Roles = "ADMIN")]
        [HttpDelete("delete/{id}")]
        public async Task<IActionResult> DeleteBarber(int id, [FromQuery] bool confirm = false)
        {
            try
            {
                var barber = await _context.Barbers.Include(b => b.User).FirstOrDefaultAsync(b => b.Id == id);
                if (barber == null) return NotFound(new { message = "Barber not found" });
                if (!barber.isActive) return BadRequest(new { message = "This barber is already inactive" });

                // Past bookings are left alone (they're history).
                var futureBookings = await _context.Bookings
                    .Include(b => b.User)
                    .Where(b => b.BarberId == id && b.Status != BookingStatus.CANCELLED && b.StartDateTime > ShopClock.Now)
                    .ToListAsync();

                var pendingBookings = futureBookings.Where(b => b.Status == BookingStatus.PENDING).ToList();
                var confirmedBookings = futureBookings.Where(b => b.Status != BookingStatus.PENDING).ToList();

                if (futureBookings.Count > 0 && !confirm)
                {
                    // Same shape as the closure conflict payload so the frontend modal stays familiar, but
                    // `willBeCancelled` replaces `willBeEmailed`: what the admin needs to know here is which
                    // bookings survive for them to deal with, not who gets a cancellation email.
                    var conflictDetails = futureBookings.Select(b => new
                    {
                        b.Id,
                        Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                        Time = b.StartDateTime.ToString("h:mm tt"),
                        Customer = b.User != null ? $"{b.User.Name} {b.User.Surname}".Trim() : null,
                        Status = b.Status.ToString(),
                        Email = b.ContactEmail,
                        Phone = b.User != null ? b.User.Phone : null,
                        WillBeCancelled = b.Status == BookingStatus.PENDING
                    }).ToList();

                    /* The softer option is only worth surfacing here, at the moment the admin is about to
                     * take someone's login away. Deactivating is right when the barber has gone; if they're
                     * working their notice they can still serve these themselves, and nothing else in the UI
                     * tells the admin there's another way.
                     *
                     * Except on an ADMIN's chair, where no login is at stake - their dashboard access comes
                     * from the role, not this row - so the closing sentence would be describing a
                     * consequence that doesn't happen. */
                    var isAdminAccount = barber.User != null && barber.User.Role == Role.ADMIN;
                    var message = $"This barber has {conflictDetails.Count} upcoming booking(s). "
                        + (confirmedBookings.Count > 0
                            ? $"The {confirmedBookings.Count} confirmed one(s) will NOT be cancelled - they stay live and are flagged in Needs Review "
                              + "for you to reassign to another barber or cancel yourself. Those customers are not notified yet. "
                            : "")
                        + (pendingBookings.Count > 0
                            ? $"{pendingBookings.Count} unconfirmed booking(s) still at checkout will be cancelled automatically. "
                            : "")
                        + (isAdminAccount
                            ? "This is an admin account, so no login is revoked - only the chair closes. To stop new "
                              + "bookings without touching these appointments, close them to new bookings instead."
                            : "If they're working their notice, close them to new bookings instead - these appointments stand "
                              + "and they keep their login until the last one is done.");

                    return Conflict(new
                    {
                        requiresConfirmation = true,
                        message,
                        conflicts = conflictDetails
                    });
                }

                // Keep the image file and ImageUrl on deactivation so a reactivated barber gets their
                // photo back. Deleting the file here left the row pointing at a missing file, so the
                // Inactive card and the Reactivate modal fell back to the placeholder. Superseded files
                // are cleaned up when a photo is replaced/removed (UpdateBarber and the revive branch),
                // not here.
                barber.isActive = false;

                var user = await _context.Users.FindAsync(barber.UserId);
                /* BARBER accounts only. When the deactivated barber makes their next request, the middleware
                 * sees the token version mismatch and returns a 401 - locking them out instantly without any
                 * extra middleware logic. Otherwise a deactivated barber's JWT stays valid and they can keep
                 * reaching the dashboard until it expires, which is a real security hole.
                 *
                 * An ADMIN who also cuts hair is the exception, and the role tested is the DEACTIVATED
                 * person's, not the caller's - so it holds whether they closed their own chair or another
                 * admin closed it for them. Their dashboard access comes from the role, not the Barber row,
                 * so closing the chair revokes no privilege and the bump protects nothing: they can log
                 * straight back in anyway (the login block only fires for BARBER accounts). All it would do
                 * is dump them on the login screen mid-click, which reads as a crash - and it would strand
                 * them outside the very dashboard they need to deal with the bookings this just flagged. */
                if (user != null && user.Role == Role.BARBER) user.TokenVersion++;
                await _context.SaveChangesAsync();

                // Deactivate first (blocks NEW bookings via the isActive availability filter), then deal with
                // the existing ones so nobody can book this barber mid-sweep. Only the PENDING ones go to the
                // shared canceller (it voids the in-flight PaymentIntent and sends no email, since a pending
                // booking was never confirmed to the customer in the first place).
                if (pendingBookings.Count > 0)
                    await BookingConflictCanceller.CancelConflictingBookingsAsync(_context, pendingBookings, CancellationReason.BarberUnavailable);

                /* The confirmed ones keep their barber, their time and their money, and land in the worklist
                 * instead. The note has to say the customer hasn't been told: unlike every other NeedsReview
                 * entry, nothing has happened to this booking yet - it is still on, and it stays on until the
                 * admin reassigns or cancels it. The phone number rides along because a walk-in booked by
                 * staff may have no email to reassign-notify or cancel-notify later. */
                var barberName = barber.User != null ? $"{barber.User.Name} {barber.User.Surname}".Trim() : "This barber";
                foreach (var booking in confirmedBookings)
                {
                    booking.FlagForReview(
                        $"{barberName} {ReviewMarkers.BarberLeft} - {booking.StartDateTime:MMM d 'at' h:mm tt}. "
                        + "The customer has NOT been told. Reassign it to another barber or cancel it."
                        + (string.IsNullOrWhiteSpace(booking.ContactEmail)
                            ? $" No email on file - reach them on {(string.IsNullOrWhiteSpace(booking.User?.Phone) ? "the number on the booking" : booking.User!.Phone)}."
                            : ""));
                }
                if (confirmedBookings.Count > 0) await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = "Barber deactivated successfully",
                    flaggedForReview = confirmedBookings.Count,
                    cancelled = pendingBookings.Count
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "An unexpected error occurred" });
            }
        }
    }
}
