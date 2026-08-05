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

        /* Closures are the manager's to set: only an ADMIN creates or removes them, for any barber or for
         * the whole shop. A barber reads the ones that affect him (his own, plus shop-wide) on the calendar
         * and does nothing else with them, so the single read below is the only ownership question left in
         * this controller. The private CallerBarberId helper that used to sit here moved to
         * Common/CallerBarber.cs - see that file for why. */
        [Authorize(Roles = "ADMIN")]
        [HttpPost]
        public async Task<IActionResult> AddClosureAsync([FromBody] ShopClosureViewModel newClosure)
        {
            try
            {
                // An admin may scope a closure to a specific barber (BarberId set) or leave it shop-wide
                // (null). An unvalidated id would create an orphan closure pointing at no real barber.
                // Active-only, matching the customer-facing roster - a deactivated barber takes no bookings.
                if (newClosure.BarberId != null)
                {
                    var barberExists = await _context.Barbers
                        .AnyAsync(b => b.Id == newClosure.BarberId && b.isActive);
                    if (!barberExists)
                        return BadRequest(new { message = "Selected barber not found or is inactive" });
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
                /* Confirmed bookings are GRANDFATHERED, exactly as a barber deactivation or an hours change
                 * treats them: they stay live and go into the worklist for the admin to reassign, move or
                 * cancel. Cancelling them automatically was the only irreversible act in the admin screens -
                 * one wrong end date pushed real refunds through Stripe that fixing the date couldn't undo -
                 * and it threw away the better outcome, which is usually "let's move you to Thursday" rather
                 * than handing the money back and hoping they rebook.
                 *
                 * Pending ones are still cancelled outright: nobody needs to review a checkout in flight, and
                 * the customer recovers on the spot. */
                var pendingConflicts = conflicts.Where(b => b.Status == BookingStatus.PENDING).ToList();
                var confirmedConflicts = conflicts.Where(b => b.Status != BookingStatus.PENDING).ToList();

                if (conflicts.Count > 0 && !newClosure.ConfirmCancelBookings)
                {
                    var conflictDetails = conflicts.Select(b => new
                    {
                        b.Id,
                        Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                        Time = b.StartDateTime.ToString("h:mm tt"),
                        Customer = b.User != null ? $"{b.User.Name} {b.User.Surname}".Trim() : null,
                        Status = b.Status.ToString(),
                        Email = b.ContactEmail,
                        Phone = b.User != null ? b.User.Phone : null,
                        // Nothing is emailed on confirm any more - a grandfathered booking is still ON, so
                        // telling the customer anything yet would be wrong. Kept as a field because the
                        // admin still needs to know who they'll be able to reach when they do act.
                        WillBeEmailed = false
                    }).ToList();

                    // Same reasoning as the review note below: reassigning is only an option when the
                    // closure is one barber's. A shop-wide closure shuts every chair.
                    var confirmedOptions = newClosure.BarberId == null
                        ? "move or cancel"
                        : "reassign, move or cancel";
                    var message = $"This closure overlaps {conflictDetails.Count} existing booking(s). "
                        + (confirmedConflicts.Count > 0
                            ? $"{confirmedConflicts.Count} confirmed booking(s) will NOT be cancelled - they stay live "
                              + $"and are flagged in Needs Review for you to {confirmedOptions}. The customer "
                              + "has not been told. "
                            : "")
                        + (pendingConflicts.Count > 0
                            ? $"{pendingConflicts.Count} booking(s) still at checkout will be cancelled automatically."
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
                await BookingConflictCanceller.CancelConflictingBookingsAsync(_context, pendingConflicts, CancellationReason.ShopClosure);

                /* The confirmed ones keep their slot, their barber and their money, and land in the worklist.
                 * The note has to say the customer hasn't been told: unlike most NeedsReview entries nothing
                 * has happened to this booking yet - it is still on, and stays on until the admin acts. The
                 * phone number rides along because a walk-in booked by staff may have no email at all. */
                // Only barber-scoped closures carry a name; shop-wide (BarberId == null) stays null so the
                // admin calendar renders just the reason. Lets the client show "Name - reason" immediately
                // instead of falling back to the reason until the next reload. Resolved before the loop
                // below because the review note names the barber when the closure is only theirs.
                var barberName = closure.BarberId == null
                    ? null
                    : await _context.Barbers
                        .Where(b => b.Id == closure.BarberId)
                        .Select(b => b.User.Name)
                        .FirstOrDefaultAsync();

                /* Shop-wide and barber-only closures get different notes. A barber's day off doesn't close
                 * the shop, and a shop-wide closure can't be solved by reassigning - every chair is shut,
                 * so the only real options are moving the booking or cancelling it. */
                var closureIsShopWide = closure.BarberId == null;
                foreach (var booking in confirmedConflicts)
                {
                    var what = closureIsShopWide
                        ? ReviewMarkers.ShopClosed
                        : $"{barberName ?? "This barber"} {ReviewMarkers.BarberClosed}";
                    var options = closureIsShopWide
                        ? "Move it or cancel it."
                        : "Reassign it to another barber, move it, or cancel it.";

                    booking.FlagForReview(
                        $"{what} - {booking.StartDateTime:MMM d 'at' h:mm tt}. "
                        + $"The customer has NOT been told. {options}"
                        + (string.IsNullOrWhiteSpace(booking.ContactEmail)
                            ? $" No email on file - reach them on {(string.IsNullOrWhiteSpace(booking.User?.Phone) ? "the number on the booking" : booking.User!.Phone)}."
                            : ""));
                }
                if (confirmedConflicts.Count > 0) await _context.SaveChangesAsync();

                return Ok(new GetBarberShopClosuresViewModel
                {
                    Id = closure.Id,
                    StartDate = closure.StartDate,
                    EndDate = closure.EndDate,
                    StartTime = closure.StartTime,
                    EndTime = closure.EndTime,
                    Reason = closure.Reason,
                    IsFullDay = closure.IsFullDay,
                    BarberName = barberName,
                    IsShopWide = closure.BarberId == null,
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

        /* The barber's read-only view of the time he isn't working. Now that he can't create or delete
         * closures, this list stopped being "the closures you can manage" and became "the days you're off"
         * - so it has to include SHOP-WIDE closures (BarberId == null) as well as his own. Leaving them out
         * was defensible while the list was actionable (he could never have deleted a shop-wide closure
         * anyway), but as a straight answer to "am I working that day" it was wrong: a public holiday shut
         * the whole shop and his calendar showed an ordinary working day.
         *
         * IsShopWide tells the two apart on the client, which renders them distinctly - the barber should
         * be able to see at a glance whether it's his own time off or the shop being closed around him. */
        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpGet("barber/{barberId}/closures")]
        public async Task<IActionResult> GetBarberClosures(int barberId)
        {
            // A barber may only view their own closures; an admin may view any barber's.
            if (!await User.CanAccessBarberAsync(_context, barberId))
                return StatusCode(403, new { message = "You can only view your own closures" });
            var todayDate = ShopClock.Today;
            try
            {
                var shopClosures = await _context.ShopClosures
                    .Where(c => c.IsActive == true && c.StartDate >= todayDate
                                && (c.BarberId == barberId || c.BarberId == null))
                    .Select(c => new GetBarberShopClosuresViewModel
                    {
                        Id = c.Id,
                        StartDate = c.StartDate,
                        EndDate = c.EndDate,
                        StartTime = c.StartTime,
                        EndTime = c.EndTime,
                        Reason = c.Reason,
                        IsFullDay = c.IsFullDay,
                        IsShopWide = c.BarberId == null
                    }).ToListAsync();
                return Ok(shopClosures);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        [Authorize(Roles = "ADMIN")]
        [HttpPatch("delete/{closureId}")]
        public async Task<IActionResult> DeleteClosure(int closureId)
        {
            try
            {
                var shopClosure = await _context.ShopClosures.FirstOrDefaultAsync(c => c.IsActive == true && c.Id == closureId);
                if (shopClosure == null) return NotFound(new { message = "This shop closure was not found" });
                /* Bookings this closure flagged and left live. Gathered BEFORE it's switched off, so the
                 * overlap test still has the closure's own dates to work from. */
                var closureStart = shopClosure.IsFullDay
                    ? shopClosure.StartDate.ToDateTime(TimeOnly.MinValue)
                    : shopClosure.StartDate.ToDateTime(shopClosure.StartTime!.Value);
                var closureEnd = shopClosure.IsFullDay
                    ? (shopClosure.EndDate ?? shopClosure.StartDate).AddDays(1).ToDateTime(TimeOnly.MinValue)
                    : shopClosure.StartDate.ToDateTime(shopClosure.EndTime!.Value);

                var now = ShopClock.Now;
                var flagged = await _context.Bookings
                    .Include(b => b.User)
                    .Where(b => b.Status == BookingStatus.COMPLETED
                                && b.StartDateTime > now
                                && b.NeedsReview
                                // Both variants: a shop-wide closure and a barber's own time off write
                                // different sentences, and either can be the note this closure left.
                                && b.ReviewReason != null
                                && (b.ReviewReason.Contains(ReviewMarkers.ShopClosed)
                                    || b.ReviewReason.Contains(ReviewMarkers.BarberClosed))
                                && (shopClosure.BarberId == null || b.BarberId == shopClosure.BarberId)
                                && b.StartDateTime < closureEnd
                                && b.StartDateTime.AddMinutes(b.DurationMin) > closureStart)
                    .ToListAsync();

                shopClosure.IsActive = false;
                await _context.SaveChangesAsync();

                /* Only the ones no OTHER closure still shuts. Closures overlap - a shop-wide holiday on top
                 * of a barber's day off - so removing one doesn't necessarily reopen the slot, and calling a
                 * booking reopened while a second closure covers it would be false. Run after the save, so
                 * the closure being deleted is already out of the picture.
                 *
                 * Closures only - deliberately not "is anything at all wrong with this booking". A departed
                 * barber or a stuck refund leaves its own note for the admin to read; suppressing the
                 * booking here would hide that the CLOSURE problem is resolved, which is what this list is
                 * for. Each list vouches for its own dimension, and none of them clears anything. */
                var reopened = new List<Booking>();
                foreach (var b in flagged)
                    if (!await BookingSlotGuard.IsSlotClosedAsync(_context, b))
                        reopened.Add(b);

                return Ok(new
                {
                    message = $"The event '{shopClosure.Reason}' was deleted",
                    noLongerClosed = await BookingSlotGuard.DescribeAsync(_context, reopened)
                });
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