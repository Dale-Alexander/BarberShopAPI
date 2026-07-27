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

        // Confirmed (COMPLETED) future bookings that would fall outside a proposed set of hours, within the
        // date window the edited/created version governs. Grandfathered - never cancelled - so the admin is
        // shown these to reschedule/refund/honour by hand. PENDING bookings are intentionally excluded: they
        // self-resolve (Phase 3 re-checks the schedule at confirmation, and unconfirmed ones auto-expire).
        private async Task<List<OrphanedBookingViewModel>> FindOrphanedBookingsAsync(
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

            var orphaned = new List<OrphanedBookingViewModel>();
            foreach (var b in candidates)
            {
                var date = DateOnly.FromDateTime(b.StartDateTime);
                if (date < windowFrom || (windowTo != null && date > windowTo)) continue; // outside this version's reign
                var start = TimeOnly.FromDateTime(b.StartDateTime);
                var end = TimeOnly.FromDateTime(b.StartDateTime.AddMinutes(b.DurationMin));
                if (!ScheduleResolver.FitsWithinAShift(proposed, date, start, end, grace))
                {
                    orphaned.Add(new OrphanedBookingViewModel
                    {
                        Id = b.Id,
                        Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                        Time = b.StartDateTime.ToString("h:mm tt"),
                        Customer = b.User != null ? $"{b.User.Name} {b.User.Surname}".Trim() : null,
                        Email = b.ContactEmail,
                        Phone = b.User != null ? b.User.Phone : null
                    });
                }
            }
            return orphaned;
        }

        private static object OrphanConflictPayload(List<OrphanedBookingViewModel> orphaned) => new
        {
            requiresConfirmation = true,
            message = $"{orphaned.Count} confirmed booking(s) fall outside the new hours. They won't be "
                + "cancelled - save to keep them, then reschedule, refund or honour each one by hand.",
            affected = orphaned
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

                // Warn (once) about confirmed future bookings the new hours would strand, within this
                // version's date window. Grandfathered - the admin confirms to proceed, nothing is cancelled.
                if (!model.ConfirmOrphaned)
                {
                    var orphaned = await FindOrphanedBookingsAsync(
                        version.BarberId, version.EffectiveFrom, version.EffectiveTo, model.Shifts);
                    if (orphaned.Count > 0) return Conflict(OrphanConflictPayload(orphaned));
                }

                // Clear + re-add rather than diffing; cascade delete removes the orphaned old shifts.
                version.Shifts.Clear();
                foreach (var sh in ToShifts(model.Shifts)) version.Shifts.Add(sh);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Schedule updated" });
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
                    .FirstOrDefaultAsync(s => s.BarberId == barberId && s.EffectiveTo == null);
                if (current == null)
                    return BadRequest(new { message = "This barber has no active schedule to supersede" });
                if (model.EffectiveFrom <= current.EffectiveFrom)
                    return BadRequest(new { message = "The new schedule must start after the current one began" });

                // The new version reigns from EffectiveFrom onward - warn about confirmed future bookings on
                // or after that date the new hours would strand. Grandfathered; admin confirms to proceed.
                if (!model.ConfirmOrphaned)
                {
                    var orphaned = await FindOrphanedBookingsAsync(barberId, model.EffectiveFrom, null, model.Shifts);
                    if (orphaned.Count > 0) return Conflict(OrphanConflictPayload(orphaned));
                }

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
                await _context.SaveChangesAsync();
                await tx.CommitAsync();

                return Ok(new AdminScheduleVersionViewModel
                {
                    Id = version.Id,
                    EffectiveFrom = version.EffectiveFrom,
                    EffectiveTo = version.EffectiveTo,
                    Shifts = model.Shifts
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
        public async Task<IActionResult> DeleteVersion(int scheduleId)
        {
            try
            {
                var version = await _context.BarberSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId);
                if (version == null) return NotFound(new { message = "Schedule version not found" });
                if (version.EffectiveTo != null)
                    return BadRequest(new { message = "Only the current schedule can be removed; edit superseded ones instead" });

                var prior = await _context.BarberSchedules
                    .Where(s => s.BarberId == version.BarberId && s.Id != version.Id)
                    .OrderByDescending(s => s.EffectiveFrom)
                    .FirstOrDefaultAsync();
                if (prior == null)
                    return BadRequest(new { message = "A barber must always have a schedule - this is their only one" });

                // Delete the open-ended row first, THEN reopen the prior (set its EffectiveTo null) - keeps
                // the one-open-ended-per-barber index satisfied at every step. Transaction so a failure can't
                // leave the barber with no current version.
                await using var tx = await _context.Database.BeginTransactionAsync();
                _context.BarberSchedules.Remove(version); // cascade removes its shifts
                await _context.SaveChangesAsync();
                prior.EffectiveTo = null;
                await _context.SaveChangesAsync();
                await tx.CommitAsync();

                return Ok(new { message = "Schedule removed" });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }
    }
}
