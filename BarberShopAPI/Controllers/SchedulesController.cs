using BarberShopAPI.Common;
using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.Controllers
{
    // Admin management of per-barber, effective-dated weekly schedules. The customer picker and the
    // booking-create paths consume this data via barbers-with-bookings / ScheduleResolver; this
    // controller is the write side. Admin-only - barbers don't edit their own hours here.
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "ADMIN")]
    public class SchedulesController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public SchedulesController(BarberShopContext context)
        {
            _context = context;
        }

        // Shared shift validation: valid weekday, start before end, and no overlapping shifts on the same
        // day (split shifts must be disjoint). An empty list is allowed - it means the barber isn't working
        // at all under this version (a deliberate, if unusual, state).
        private static void ValidateShifts(List<ScheduleShiftViewModel> shifts)
        {
            foreach (var s in shifts)
            {
                if (s.DayOfWeek < 0 || s.DayOfWeek > 6)
                    throw new ValidationException("Invalid day of week");
                if (s.StartTime >= s.EndTime)
                    throw new ValidationException("Each shift must start before it ends");
            }
            foreach (var day in shifts.GroupBy(s => s.DayOfWeek))
            {
                var ordered = day.OrderBy(s => s.StartTime).ToList();
                for (int i = 1; i < ordered.Count; i++)
                    if (ordered[i].StartTime < ordered[i - 1].EndTime)
                        throw new ValidationException("Shifts on the same day can't overlap");
            }
        }

        private static List<BarberScheduleShift> ToShifts(List<ScheduleShiftViewModel> shifts) =>
            shifts.Select(s => new BarberScheduleShift
            {
                DayOfWeek = (DayOfWeek)s.DayOfWeek,
                StartTime = s.StartTime,
                EndTime = s.EndTime
            }).ToList();

        /* The inverse, for the delete path: the "proposed" hours there are an EXISTING version's (the prior
         * one that's about to be reopened), but FindOrphanedBookingsAsync takes the view-model shape the
         * edit/create paths receive from the client. */
        private static List<ScheduleShiftViewModel> ToShiftVms(IEnumerable<BarberScheduleShift> shifts) =>
            shifts.Select(s => new ScheduleShiftViewModel
            {
                DayOfWeek = (int)s.DayOfWeek,
                StartTime = s.StartTime,
                EndTime = s.EndTime
            }).ToList();

        // Confirmed (COMPLETED) future bookings that would fall outside a proposed set of hours, within the
        // date window the edited/created version governs. Returns the tracked Booking entities so callers can
        // both surface them (409) and flag them for review. Grandfathered - never cancelled. PENDING bookings
        // are intentionally excluded: they self-resolve (Phase 3 re-checks the schedule at confirmation, and
        // unconfirmed ones auto-expire).
        private async Task<List<Booking>> FindOrphanedBookingsAsync(
            int barberId, DateOnly windowFrom, DateOnly? windowTo, List<ScheduleShiftViewModel> proposedShifts)
        {
            var grace = await _context.ShopSettings.Select(s => s.GraceMinutesAfterClose).FirstAsync();
            var now = ShopClock.Now;
            var candidates = await _context.Bookings
                .Include(b => b.User)
                .Where(b => b.BarberId == barberId && b.Status == BookingStatus.COMPLETED && b.StartDateTime > now)
                .ToListAsync();

            // Resolve each booking against the PROPOSED hours (an in-memory version standing in for the one
            // being saved), reusing the same FitsWithinAShift the booking paths use.
            var proposed = new List<BarberSchedule>
            {
                new BarberSchedule { EffectiveFrom = windowFrom, EffectiveTo = windowTo, Shifts = ToShifts(proposedShifts) }
            };

            var orphaned = new List<Booking>();
            foreach (var b in candidates)
            {
                var date = DateOnly.FromDateTime(b.StartDateTime);
                if (date < windowFrom || (windowTo != null && date > windowTo)) continue; // outside this version's reign
                var start = TimeOnly.FromDateTime(b.StartDateTime);
                var end = TimeOnly.FromDateTime(b.StartDateTime.AddMinutes(b.DurationMin));
                if (!ScheduleResolver.FitsWithinAShift(proposed, date, start, end, grace))
                    orphaned.Add(b);
            }
            return orphaned;
        }

        /* The mirror of FindOrphanedBookingsAsync: bookings ALREADY in the worklist that these hours bring
         * back inside the schedule. Widening hours saves without a confirmation (only narrowing stops to
         * ask), so an admin undoing a mistake gets no hint that the bookings their mistake flagged are now
         * fine, and those notes sit there describing a schedule the booking no longer falls outside.
         *
         * Two filters, and a booking has to pass both:
         *   - it was flagged BY THIS PATH (its note carries OutsideHoursNote). Otherwise a booking flagged
         *     over a stuck refund, which happens to sit inside the hours, would be announced as though the
         *     schedule change had resolved something;
         *   - the change actually moved it: outside the old shifts, inside the new ones. So each booking is
         *     mentioned once, at the edit that rescued it, rather than at every later edit.
         *
         * The result is INFORMATION. It must never become a "clear them all" button: notes accumulate now
         * (see BookingReview), so the same booking may also be carrying a failed refund or a customer
         * nobody has phoned, and widening the hours does nothing about those. The admin reads each note and
         * clears it by hand. */
        private async Task<List<Booking>> FindBackInsideHoursAsync(
            int barberId, DateOnly windowFrom, DateOnly? windowTo,
            List<ScheduleShiftViewModel> previousShifts, List<ScheduleShiftViewModel> proposedShifts)
        {
            var grace = await _context.ShopSettings.Select(s => s.GraceMinutesAfterClose).FirstAsync();
            var now = ShopClock.Now;
            var flagged = await _context.Bookings
                .Include(b => b.User)
                .Where(b => b.BarberId == barberId && b.Status == BookingStatus.COMPLETED
                            && b.StartDateTime > now && b.NeedsReview
                            && b.ReviewReason != null && b.ReviewReason.Contains(OutsideHoursNote))
                .ToListAsync();
            if (flagged.Count == 0) return new List<Booking>();

            var before = new List<BarberSchedule>
            {
                new BarberSchedule { EffectiveFrom = windowFrom, EffectiveTo = windowTo, Shifts = ToShifts(previousShifts) }
            };
            var after = new List<BarberSchedule>
            {
                new BarberSchedule { EffectiveFrom = windowFrom, EffectiveTo = windowTo, Shifts = ToShifts(proposedShifts) }
            };

            var rescued = new List<Booking>();
            foreach (var b in flagged)
            {
                var date = DateOnly.FromDateTime(b.StartDateTime);
                if (date < windowFrom || (windowTo != null && date > windowTo)) continue;
                var start = TimeOnly.FromDateTime(b.StartDateTime);
                var end = TimeOnly.FromDateTime(b.StartDateTime.AddMinutes(b.DurationMin));
                if (!ScheduleResolver.FitsWithinAShift(before, date, start, end, grace)
                    && ScheduleResolver.FitsWithinAShift(after, date, start, end, grace))
                    rescued.Add(b);
            }
            return rescued;
        }

        private static OrphanedBookingViewModel ToOrphanVm(Booking b) => new OrphanedBookingViewModel
        {
            Id = b.Id,
            Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
            Time = b.StartDateTime.ToString("h:mm tt"),
            Customer = b.User != null ? $"{b.User.Name} {b.User.Surname}".Trim() : null,
            Email = b.ContactEmail,
            Phone = b.User != null ? b.User.Phone : null
        };

        // Flag stranded bookings for the admin's Needs Review worklist so they aren't forgotten once the
        // confirmation modal closes. They stay COMPLETED (grandfathered) - this is a reminder to act, not a
        // cancellation. Mutates the tracked entities; the caller persists them with the schedule change.
        // The options list deliberately says "cancel it" rather than "refund it": these are cash bookings as
        // often as card ones (nothing collected yet on cash), and even on a card booking the 24h policy
        // decides whether a refund is due. Cancelling is the action; the cancel flow works the money out.
        /* The note this path writes, as a constant, because two things depend on its exact text: the flag
         * itself, and CountBackInsideHoursAsync recognising ITS OWN notes later. Matching on a sentence is
         * only safe while the sentence has one author - keep it that way. */
        private const string OutsideHoursNote =
            "The barber's working hours changed and this booking now falls outside their "
            + "schedule - honour it, reschedule it, or cancel it.";

        private static void FlagOrphanedForReview(IEnumerable<Booking> orphaned)
        {
            foreach (var b in orphaned) b.FlagForReview(OutsideHoursNote);
        }

        // `hoursLabel` because the delete path isn't proposing new hours - it's restoring the previous
        // version's, and telling the admin those bookings fall outside "the new hours" would misdescribe it.
        private static object OrphanConflictPayload(List<Booking> orphaned, string hoursLabel = "the new hours") => new
        {
            requiresConfirmation = true,
            // Same three options as the worklist note, and for the same reason: "refund" would be wrong on a
            // cash booking, where nothing has been collected to give back.
            message = $"{orphaned.Count} confirmed booking(s) fall outside {hoursLabel}. They won't be "
                + "cancelled - save to keep them (they'll appear in Needs Review), then honour, reschedule or "
                + "cancel each one by hand.",
            affected = orphaned.Select(ToOrphanVm).ToList()
        };

        // All of a barber's versions (past, current, future), oldest first, so the editor can show
        // history plus the current and any upcoming seasonal changes.
        [HttpGet("barber/{barberId}")]
        public async Task<IActionResult> GetBarberSchedule(int barberId)
        {
            if (!await _context.Barbers.AnyAsync(b => b.Id == barberId))
                return NotFound(new { message = "Barber not found" });

            var versions = await _context.BarberSchedules
                .Where(s => s.BarberId == barberId)
                .OrderBy(s => s.EffectiveFrom)
                .Select(s => new AdminScheduleVersionViewModel
                {
                    Id = s.Id,
                    EffectiveFrom = s.EffectiveFrom,
                    EffectiveTo = s.EffectiveTo,
                    Shifts = s.Shifts
                        .OrderBy(sh => sh.DayOfWeek).ThenBy(sh => sh.StartTime)
                        .Select(sh => new ScheduleShiftViewModel
                        {
                            DayOfWeek = (int)sh.DayOfWeek,
                            StartTime = sh.StartTime,
                            EndTime = sh.EndTime
                        }).ToList()
                }).ToListAsync();

            return Ok(versions);
        }

        // Replace a version's shifts. Past versions (already ended) are read-only history.
        [HttpPut("version/{scheduleId}")]
        public async Task<IActionResult> UpdateVersionShifts(int scheduleId, [FromBody] SaveScheduleShiftsViewModel model)
        {
            try
            {
                ValidateShifts(model.Shifts);

                var version = await _context.BarberSchedules
                    .Include(s => s.Shifts)
                    .FirstOrDefaultAsync(s => s.Id == scheduleId);
                if (version == null) return NotFound(new { message = "Schedule version not found" });

                if (version.EffectiveTo != null && version.EffectiveTo < ShopClock.Today)
                    return BadRequest(new { message = "Past schedules can't be edited" });

                // Confirmed future bookings the new hours would strand, within this version's date window.
                // Warn once; the admin confirms to proceed. Nothing is cancelled - they're grandfathered and
                // flagged for Needs Review instead.
                var orphaned = await FindOrphanedBookingsAsync(
                    version.BarberId, version.EffectiveFrom, version.EffectiveTo, model.Shifts);
                if (orphaned.Count > 0 && !model.ConfirmOrphaned)
                    return Conflict(OrphanConflictPayload(orphaned));

                // Snapshot the old hours before Clear() empties the collection - the count below compares
                // against them.
                var previousShifts = ToShiftVms(version.Shifts);
                var backInside = await FindBackInsideHoursAsync(
                    version.BarberId, version.EffectiveFrom, version.EffectiveTo, previousShifts, model.Shifts);

                // Clear + re-add rather than diffing; cascade delete removes the orphaned old shifts.
                version.Shifts.Clear();
                foreach (var sh in ToShifts(model.Shifts)) version.Shifts.Add(sh);
                FlagOrphanedForReview(orphaned);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Schedule updated", backInsideHours = backInside.Select(ToOrphanVm).ToList() });
            }
            catch (ValidationException ex) { return BadRequest(new { message = ex.Message }); }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }

        // Start a new seasonal version from a date. Chaining: the current open-ended version is closed the
        // day before, so versions stay contiguous and non-overlapping with exactly one open-ended row.
        [HttpPost("barber/{barberId}")]
        public async Task<IActionResult> CreateVersion(int barberId, [FromBody] CreateScheduleVersionViewModel model)
        {
            try
            {
                ValidateShifts(model.Shifts);

                if (!await _context.Barbers.AnyAsync(b => b.Id == barberId))
                    return NotFound(new { message = "Barber not found" });

                if (model.EffectiveFrom < ShopClock.Today)
                    return BadRequest(new { message = "A new schedule can't start in the past" });

                var current = await _context.BarberSchedules
                    .Include(s => s.Shifts)   // needed by CountBackInsideHoursAsync below
                    .FirstOrDefaultAsync(s => s.BarberId == barberId && s.EffectiveTo == null);
                if (current == null)
                    return BadRequest(new { message = "This barber has no active schedule to supersede" });
                if (model.EffectiveFrom <= current.EffectiveFrom)
                    return BadRequest(new { message = "The new schedule must start after the current one began" });

                // The new version reigns from EffectiveFrom onward - confirmed future bookings on or after that
                // date the new hours would strand. Warn once; admin confirms to proceed. Grandfathered and
                // flagged for Needs Review, never cancelled.
                /* this is for when users made a booking for a particular schedule but the admin created a new schedule that now differs from the original
                 * one. You have to ask if any bookings land within the new schedule and if those bookings actually fall within a shift. */
                var orphaned = await FindOrphanedBookingsAsync(barberId, model.EffectiveFrom, null, model.Shifts);
                if (orphaned.Count > 0 && !model.ConfirmOrphaned)
                    return Conflict(OrphanConflictPayload(orphaned));

                // The hours the new version replaces, for dates it governs, are the current version's.
                var backInside = await FindBackInsideHoursAsync(
                    barberId, model.EffectiveFrom, null, ToShiftVms(current.Shifts), model.Shifts);

                // Close the current version the day before the new one starts, THEN add the new open-ended
                // version - two saves in a transaction so the filtered unique index (one EffectiveTo==null
                // per barber) never sees two open-ended rows at once.
                await using var tx = await _context.Database.BeginTransactionAsync();
                current.EffectiveTo = model.EffectiveFrom.AddDays(-1);
                await _context.SaveChangesAsync();

                var version = new BarberSchedule
                {
                    BarberId = barberId,
                    EffectiveFrom = model.EffectiveFrom,
                    EffectiveTo = null,
                    Shifts = ToShifts(model.Shifts)
                };
                _context.BarberSchedules.Add(version);
                // Flag the stranded bookings for review in the same transaction as the schedule change.
                FlagOrphanedForReview(orphaned);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();

                return Ok(new AdminScheduleVersionViewModel
                {
                    Id = version.Id,
                    EffectiveFrom = version.EffectiveFrom,
                    EffectiveTo = version.EffectiveTo,
                    Shifts = model.Shifts,
                    BackInsideHours = backInside.Select(ToOrphanVm).ToList()
                });
            }
            catch (ValidationException ex) { return BadRequest(new { message = ex.Message }); }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }

        // Remove the current (open-ended) version and reopen the one before it, e.g. to undo a seasonal
        // change. Only the latest version can be removed, and never the barber's only one (that would leave
        // them with no schedule = unbookable).
        [HttpDelete("version/{scheduleId}")]
        public async Task<IActionResult> DeleteVersion(int scheduleId, [FromQuery] bool confirmOrphaned = false)
        {
            try
            {
                var version = await _context.BarberSchedules
                    .Include(s => s.Shifts)   // needed by CountBackInsideHoursAsync below
                    .FirstOrDefaultAsync(s => s.Id == scheduleId);
                if (version == null) return NotFound(new { message = "Schedule version not found" });
                if (version.EffectiveTo != null)
                    return BadRequest(new { message = "Only the current schedule can be removed; edit superseded ones instead" });

                var prior = await _context.BarberSchedules
                    .Include(s => s.Shifts)
                    .Where(s => s.BarberId == version.BarberId && s.Id != version.Id)
                    .OrderByDescending(s => s.EffectiveFrom)
                    .FirstOrDefaultAsync();
                if (prior == null)
                    return BadRequest(new { message = "A barber must always have a schedule - this is their only one" });

                /* Removing a version is a schedule change like any other: the prior version's (usually
                 * narrower) hours snap back over everything this one governed, so bookings taken under these
                 * hours can be left outside the barber's schedule. Undo is exactly when that's easiest to
                 * forget, so it gets the same treatment as the edit/create paths - warn once, then flag on
                 * confirm - rather than silently stranding them. Bookings BEFORE this version's EffectiveFrom
                 * were already governed by the prior one, so the window starts there. */
                var orphaned = await FindOrphanedBookingsAsync(
                    version.BarberId, version.EffectiveFrom, null, ToShiftVms(prior.Shifts));
                if (orphaned.Count > 0 && !confirmOrphaned)
                    return Conflict(OrphanConflictPayload(orphaned, "the hours this restores"));

                // Undoing a version can widen as easily as narrow - the prior hours may be the roomier ones.
                var backInside = await FindBackInsideHoursAsync(
                    version.BarberId, version.EffectiveFrom, null, ToShiftVms(version.Shifts), ToShiftVms(prior.Shifts));
                var backInsideVms = backInside.Select(ToOrphanVm).ToList();

                // Delete the open-ended row first, THEN reopen the prior (set its EffectiveTo null) - keeps
                // the one-open-ended-per-barber index satisfied at every step. Transaction so a failure can't
                // leave the barber with no current version.
                await using var tx = await _context.Database.BeginTransactionAsync();
                _context.BarberSchedules.Remove(version); // cascade removes its shifts
                await _context.SaveChangesAsync();
                prior.EffectiveTo = null;
                // Flagged in the same transaction as the schedule change, as the edit/create paths do.
                FlagOrphanedForReview(orphaned);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();

                return Ok(new { message = "Schedule removed", backInsideHours = backInsideVms });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }
    }
}
