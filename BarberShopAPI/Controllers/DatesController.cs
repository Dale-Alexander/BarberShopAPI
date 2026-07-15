using BarberShopAPI.Data;
using BarberShopAPI.ViewModels;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Security.Claims;
using BarberShopAPI.Common;
using Stripe;
namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DatesController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public DatesController(BarberShopContext context)
        {
            _context = context;
        }
        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPost]
        public async Task<IActionResult> AddClosureAsync([FromBody] ShopClosureViewModel newClosure)
        {
            try
            {
                var role = User.FindFirstValue(ClaimTypes.Role);
                if(role == "BARBER")
                {
                    var userId = int.Parse(User.FindFirstValue("id"));
                    var barber = await _context.Barbers.FirstOrDefaultAsync(b => b.UserId == userId);
                    if (barber == null) return NotFound(new { message = "Barber profile not found" });
                    newClosure.BarberId = barber.Id;
                }
                await ValidateClosureAsync(newClosure);

                // Find bookings this closure would land on. A shop-wide closure (BarberId == null) hits
                // every barber's bookings; a barber closure only that barber's. We express the closure as a
                // [start, end) DateTime window and use the same interval-overlap test the booking-create path
                // uses, so the two directions agree on what "conflicts" means.
                var newEndDate = newClosure.EndDate ?? newClosure.StartDate;
                var closureStart = newClosure.IsFullDay
                    ? newClosure.StartDate.ToDateTime(TimeOnly.MinValue)
                    : newClosure.StartDate.ToDateTime(newClosure.StartTime.Value);
                var closureEnd = newClosure.IsFullDay
                    ? newEndDate.AddDays(1).ToDateTime(TimeOnly.MinValue)
                    : newClosure.StartDate.ToDateTime(newClosure.EndTime.Value);

                var conflicts = await _context.Bookings
                    .Include(b => b.User)
                    .Where(b => b.Status != BookingStatus.CANCELLED &&
                                (newClosure.BarberId == null || b.BarberId == newClosure.BarberId) &&
                                b.StartDateTime < closureEnd &&
                                b.StartDateTime.AddMinutes(b.DurationMin) > closureStart)
                    .ToListAsync();

                // Soft warn: don't create the closure yet - hand the admin the conflicting bookings so they
                // can decide. They re-submit with ConfirmCancelBookings = true to go ahead.
                if (conflicts.Count > 0 && !newClosure.ConfirmCancelBookings)
                {
                    // Tell the admin exactly who the automatic cancellation email can reach and who it can't.
                    // A booking is only actually emailed on cancel when it's COMPLETED *and* has a ContactEmail:
                    // PENDING conflicts are never emailed (they were never confirmed - see CancelConflictingBookingsAsync),
                    // and admin-created bookings carry only a phone, no email. Everyone else must be reached by phone.
                    var conflictDetails = conflicts.Select(b => new
                    {
                        b.Id,
                        Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                        Time = b.StartDateTime.ToString("h:mm tt"),
                        Customer = b.User != null ? $"{b.User.Name} {b.User.Surname}".Trim() : null,
                        Status = b.Status.ToString(),
                        Email = b.ContactEmail,
                        Phone = b.User != null ? b.User.Phone : null,
                        WillBeEmailed = b.Status == BookingStatus.COMPLETED && !string.IsNullOrWhiteSpace(b.ContactEmail)
                    }).ToList();

                    var emailedCount = conflictDetails.Count(c => c.WillBeEmailed);
                    var phoneOnlyCount = conflictDetails.Count - emailedCount;

                    var message = $"This closure overlaps {conflictDetails.Count} existing booking(s). "
                        + "Confirming will cancel and refund them. "
                        + (emailedCount > 0
                            ? $"{emailedCount} customer(s) with an email on file will be notified automatically. "
                            : "")
                        + (phoneOnlyCount > 0
                            ? $"{phoneOnlyCount} have no email and must be contacted by phone."
                            : "");

                    return Conflict(new
                    {
                        requiresConfirmation = true,
                        message,
                        conflicts = conflictDetails
                    });
                }

                var closure = new ShopClosure
                {
                    StartDate = newClosure.StartDate,
                    EndDate = newClosure.EndDate,
                    IsFullDay = newClosure.IsFullDay,
                    StartTime = newClosure.StartTime,
                    EndTime = newClosure.EndTime,
                    BarberId = newClosure.BarberId,
                    Reason = newClosure.Reason
                };
                /* The reason we do this is because the database only accepts the type "ShopClosure" and not "ShopClosureViewModel" */
                _context.ShopClosures.Add(closure);
                await _context.SaveChangesAsync();

                // Closure now exists, so the slot is blocked against NEW bookings. Clear out the ones that
                // were already on it. We create the closure first so that even if a cancellation below fails,
                // the slot stays closed and the failure is isolated to that one booking (logged for follow-up).
                await CancelConflictingBookingsAsync(conflicts);

                return Ok(new GetBarberShopClosuresViewModel
                {
                    Id = closure.Id,
                    StartDate = closure.StartDate,
                    EndDate = closure.EndDate,
                    StartTime = closure.StartTime,
                    EndTime = closure.EndTime,
                    Reason = closure.Reason,
                    IsFullDay = closure.IsFullDay,
                });
            }
            catch(ValidationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }

        /* Cancels the bookings a just-created closure landed on. COMPLETED (paid) bookings go through the
         * shared BookingCanceller (refund + closure email). PENDING bookings are different: their payment
         * is still in flight, so instead of refunding we try to void the in-flight PaymentIntent. If Stripe
         * won't let us (the payment is already succeeding), we leave the booking PENDING and rely on the
         * webhook's closure guard to refund + cancel it when the payment lands - it can never be confirmed
         * onto the now-closed slot either way. Failures are logged, not thrown, so one bad booking doesn't
         * undo the closure or block the others.*/
        private async Task CancelConflictingBookingsAsync(List<Booking> conflicts)
        {
            foreach (var booking in conflicts)
            {
                if (booking.Status == BookingStatus.PENDING)
                {
                    if (!string.IsNullOrWhiteSpace(booking.StripePaymentIntentId))
                    {
                        try
                        {
                            await new PaymentIntentService().CancelAsync(booking.StripePaymentIntentId);
                        }
                        catch (StripeException ex)
                        {
                            Console.WriteLine($"Booking {booking.Id}: couldn't cancel PaymentIntent for a pending closure-conflict, leaving it for the webhook guard: {ex.Message}");
                            continue;
                        }
                    }
                    // No reminder job to delete: those are only scheduled at confirmation (COMPLETED),
                    // so a PENDING booking never has one.
                    booking.Status = BookingStatus.CANCELLED;
                    await _context.SaveChangesAsync();
                    // No email: a pending booking never got a confirmation, so there's nothing to walk back.
                }
                else
                {
                    var outcome = await BookingCanceller.CancelAsync(_context, booking.Id, dueToClosure: true);
                    if (outcome != BookingCanceller.Outcome.Cancelled)
                    {
                        // Nothing here will retry (unlike the webhook's Stripe-retry paths) and there's no
                        // interactive admin to re-click, so this booking would otherwise be silently stuck:
                        // the slot is closed but the customer wasn't refunded/cancelled. Flag it so it shows
                        // up in the admin's review worklist instead of only in the logs.
                        Console.WriteLine($"Booking {booking.Id}: closure cancellation returned {outcome}, needs manual follow-up.");
                        booking.NeedsReview = true;
                        booking.ReviewReason = $"Closure cancellation returned {outcome} - check Stripe for a charge on this booking and refund/reconcile by hand.";
                        await _context.SaveChangesAsync();
                    }
                }
            }
        }

        private async Task ValidateClosureAsync(ShopClosureViewModel newClosure)
        {
            if(newClosure.EndDate != null && newClosure.StartDate != newClosure.EndDate && !newClosure.IsFullDay) throw new ValidationException("Multi-day closures must be full day");
            if (newClosure.IsFullDay)
            {
                newClosure.StartTime = null;
                newClosure.EndTime = null;
            }
            else
            {
                if(newClosure.StartTime == null || newClosure.EndTime == null)
                {
                    throw new ValidationException("Partial day closures require both StartTime and EndTime");
                }
                if (newClosure.StartTime >= newClosure.EndTime)
                    throw new ValidationException("StartTime must be before EndTime");
            }
            var newEndDate = newClosure.EndDate ?? newClosure.StartDate;
            var existingClosures = await _context.ShopClosures
                .Where(c => c.IsActive == true && c.StartDate <= newEndDate && (c.EndDate ?? c.StartDate) >= newClosure.StartDate)
                .Select(c => new ShopClosureViewModel
                {
                    EndDate = c.EndDate,
                    StartDate = c.StartDate,
                    IsFullDay = c.IsFullDay,
                    StartTime = c.StartTime,
                    EndTime = c.EndTime,
                    BarberId = c.BarberId,
                    Reason = c.Reason,
                })
                .ToListAsync();
            foreach (var existing in existingClosures)
            {
                CheckConflict(existing, newClosure);
            }
        }
        private bool IsSameScope(ShopClosureViewModel existing, ShopClosureViewModel newClosure)
        {
            //both shop-wide
            if (existing.BarberId == null && newClosure.BarberId == null) return true;
            //same barber
            if (existing.BarberId != null && newClosure.BarberId != null && existing.BarberId == newClosure.BarberId) return true;
            return false;
        }
        private void CheckConflict(ShopClosureViewModel existing, ShopClosureViewModel newClosure) { 
            /* This method checks if they actually conflict. Existing's date/s overlap with new Closure's. 
            /*  Determine if the 2 closures are in the same scope.
             *  They conflict if both are shop wide or both for the same barber*/
            bool sameScope = IsSameScope(existing, newClosure);
            if (!sameScope) return;
            /* if they are not in the same scope, then they dont conflict */

            /* Case 1: 
             Existing is a full day which conflicts with any new Closure so no need to check times*/
            if (existing.IsFullDay ) throw new ValidationException(BuildMessage(existing, newClosure, "conflicts with an existing full day closure"));

            //Case 2:  New closure is full day - conflicts with an existing partial closure
            if (newClosure.IsFullDay ) throw new ValidationException(BuildMessage(existing, newClosure, "a full day closure conflicts with an existing partial day closure"));

            //Case 3: This is really for when both newClosure and existing are 1 day. Both are partial single day- check time overlap
            bool overlaps = existing.StartTime < newClosure.EndTime &&
                existing.EndTime > newClosure.StartTime;
            if (overlaps) throw new ValidationException(BuildMessage(existing, newClosure, "overlaps with an existing partial day closure"));
        }

        private string BuildMessage(ShopClosureViewModel existing, ShopClosureViewModel newClosure, string reason)
        {
            string scope = existing.BarberId == null ? "shop-wide" : $"barber {existing.BarberId}";
            string timeInfo = existing.IsFullDay ? "full day" : $"{existing.StartTime}-{existing.EndTime}";
            string dateInfo = existing.EndDate != null ? $"{existing.StartDate} to {existing.EndDate}" : $"{existing.StartDate}";
            return $"Cannot add closure on {dateInfo}: {reason} ({scope}, {timeInfo}).";
        }
        [Authorize(Roles = "ADMIN")]
        [HttpGet("admin/closures")]
        public async Task<IActionResult> GetAdminClosures()
        {
            var todayDate = ShopClock.Today;
            try
            {
                var shopClosures = await _context.ShopClosures.Where(c => c.StartDate >= todayDate && c.IsActive == true)
                    .Select(c => new GetAdminShopClosuresViewModel
                    {
                        Id = c.Id,
                        StartDate = c.StartDate,
                        EndDate = c.EndDate,
                        StartTime = c.StartTime,
                        EndTime = c.EndTime,
                        Reason = c.Reason,
                        BarberName = c.Barber.User.Name,
                        IsFullDay = c.IsFullDay,
                    }).ToListAsync();
                return Ok(shopClosures);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }

        [Authorize(Roles = "BARBER")]
        [HttpGet("barber/{barberId}/closures")]
        public async Task<IActionResult> GetBarberClosures(int barberId)
        {
            var todayDate = ShopClock.Today;
            try
            {
                var shopClosures = await _context.ShopClosures.Where(c => c.IsActive == true && c.StartDate >= todayDate && c.BarberId == barberId)
                    .Select(c => new GetBarberShopClosuresViewModel
                    {
                        Id = c.Id,
                        StartDate = c.StartDate,
                        EndDate = c.EndDate,
                        StartTime = c.StartTime,
                        EndTime = c.EndTime,
                        Reason = c.Reason,
                        IsFullDay = c.IsFullDay
                    }).ToListAsync();
                return Ok(shopClosures);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPatch("delete/{closureId}")]
        public async Task<IActionResult> DeleteClosure(int closureId)
        {
            try
            {
                var shopClosure = await _context.ShopClosures.FirstOrDefaultAsync(c => c.IsActive == true && c.Id == closureId);
                if (shopClosure == null) return NotFound(new { message = "This shop closure was not found" });
                shopClosure.IsActive = false;
                await _context.SaveChangesAsync();
                return Ok(new { message = $"The event '{shopClosure.Reason}' was deleted" });
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }
    }
}
/* "Cannot add closure on 2026-03-20: overlaps with an existing partial-day closure (shop-wide, 09:00-12:00)."
"Cannot add closure on 2026-03-20: conflicts with an existing full-day closure (barber 3, full day)."
"Cannot add closure on 2026-03-20: a full-day closure conflicts with an existing partial-day closure (shop-wide, 09:00-12:00)." */

/* private string BuildMessage(ShopClosure existing, ShopClosure newClosure, string reason)
{
string scope = existing.BarberId == null ? "shop-wide" : $"barber {existing.BarberId}";
return $"The closure on {newClosure.Date} {reason} ({scope}, " +
$"{(existing.IsFullDay ? "full day" : $"{existing.StartTime}-{existing.EndTime}")}).";
}
```

---

### Example 1 � Partial overlapping partial (shop-wide)
```
Existing : BarberId = null | 09:00�12:00 | Mar 20
New      : BarberId = null | 11:00�14:00 | Mar 20
```
```
"The closure on 2026-03-20 overlaps with an existing partial-day closure (shop-wide, 09:00-12:00)."
```

---

### Example 2 � New full-day over existing partial (shop-wide)
```
Existing : BarberId = null | 09:00�12:00 | Mar 20
New      : BarberId = null | Full-day    | Mar 20
```
```
"The closure on 2026-03-20 a full-day closure conflicts with an existing partial-day closure (shop-wide, 09:00-12:00)."
```

---

### Example 3 � Full-day already exists (barber-specific)
```
Existing : BarberId = 3 | Full-day | Mar 20
New      : BarberId = 3 | anything | Mar 20
```
```
"The closure on 2026-03-20 conflicts with an existing full-day closure (barber 3, full day)."
```

---

### Example 4 � Partial overlapping partial (barber-specific)
```
Existing : BarberId = 2 | 14:00�17:00 | Mar 20
New      : BarberId = 2 | 16:00�18:00 | Mar 20
```
```
"The closure on 2026-03-20 overlaps with an existing partial-day closure (barber 2, 14:00-17:00)." */

/* [HttpPost]
public async Task<IActionResult> AddClosure(ShopClosureViewModel dto)
{
if (!dto.IsFullDay && (dto.StartTime == null || dto.EndTime == null))
    return BadRequest("StartTime and EndTime are required for part day closures.");

bool fullDayExists = await _context.ShopClosures.AnyAsync(s =>
    s.BarberId == dto.BarberId &&
    s.Date == dto.Date &&
    s.IsFullDay
);
if (fullDayExists) return BadRequest("A full day closure already exists for this date.");

if (dto.IsFullDay)
{
    bool partDayExists = await _context.ShopClosures.AnyAsync(s =>
        s.BarberId == dto.BarberId &&
        s.Date == dto.Date
    );
    if (partDayExists) return BadRequest("Part day closures already exist for this date.");
}

if (!dto.IsFullDay)
{
    bool overlap = await _context.ShopClosures.AnyAsync(s =>
        s.BarberId == dto.BarberId &&
        s.Date == dto.Date &&
        s.StartTime < dto.EndTime &&
        s.EndTime > dto.StartTime
    );
    if (overlap) return BadRequest("This time range overlaps with an existing closure.");
}

var closure = new ShopClosure
{
    BarberId = dto.BarberId,
    Date = dto.Date,
    IsFullDay = dto.IsFullDay,
    StartTime = dto.StartTime,
    EndTime = dto.EndTime,
    Reason = dto.Reason
};

_context.ShopClosures.Add(closure);
await _context.SaveChangesAsync();
return Ok(closure);
} */