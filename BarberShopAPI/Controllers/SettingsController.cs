using BarberShopAPI.Common;
using BarberShopAPI.Data;
using BarberShopAPI.Models;
using BarberShopAPI.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    // Reading settings is allowed for staff (ADMIN or BARBER): a barber needs the booking-policy values
    // (e.g. the refund cutoff on their own bookings table, the admin-booking duration default). Writing
    // stays ADMIN-only via the extra [Authorize] on the PUTs below - the two attributes AND together, so
    // an update requires ADMIN even though the class grants read to both roles.
    [Authorize(Roles = "ADMIN,BARBER")]
    public class SettingsController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public SettingsController(BarberShopContext context)
        {
            _context = context;
        }

        /* One shape for both the GET and the settings PUT response, so a field added to one can't be
         * forgotten on the other. Shop hours are added to the GET as an extra key ALONGSIDE these, not
         * nested under a "settings" object: four callers already read these fields flat off the response
         * (Settings, BookingsTable, BarberBookings and the booking picker), and re-nesting them to make
         * room would have broken all four for the sake of tidiness. */
        private static Dictionary<string, object?> SettingsPayload(ShopSettings s) => new()
        {
            ["bufferMin"] = s.BufferMin,
            ["defaultAdminBookingDurationMin"] = s.DefaultAdminBookingDurationMin,
            ["graceMinutesAfterClose"] = s.GraceMinutesAfterClose,
            ["minAdvanceBookingMinutes"] = s.MinAdvanceBookingMinutes,
            ["maxAdvanceBookingDays"] = s.MaxAdvanceBookingDays,
            ["refundCutoffHours"] = s.RefundCutoffHours,
            ["slotStepMin"] = s.SlotStepMin
        };

        private static object HoursPayload(IEnumerable<ShopHours> hours) =>
            hours.OrderBy(h => h.DayOfWeek).Select(h => new
            {
                dayOfWeek = (int)h.DayOfWeek,
                openTime = h.OpenTime.ToString("HH\\:mm"),
                closeTime = h.CloseTime.ToString("HH\\:mm"),
                isClosed = h.IsClosed
            }).ToList();

        /* Opening hours ONLY, and open to anyone: the footer shows them on every page, including to a
         * signed-out visitor, and it has no business pulling the staff settings blob (buffers, refund
         * cutoffs, lead times) to render one line of text. Opening hours are the most public fact about a
         * shop - they're on the door - so there's nothing here to protect; what needed protecting was the
         * rest of the payload, which is why this is a separate endpoint rather than [AllowAnonymous] on
         * the one above. */
        [AllowAnonymous]
        [HttpGet("public-hours")]
        public async Task<IActionResult> GetPublicHours()
        {
            var hours = await _context.ShopHours.ToListAsync();
            return Ok(HoursPayload(hours));
        }

        // The settings row is seeded (Id == 1) in BarberShopContext, so it always exists. Shop hours ride
        // along on the same GET rather than a second round trip - the settings page shows both.
        [HttpGet]
        public async Task<IActionResult> GetSettings()
        {
            var settings = await _context.ShopSettings.FirstOrDefaultAsync();
            if (settings == null) return NotFound(new { message = "Shop settings not found" });
            var hours = await _context.ShopHours.ToListAsync();
            var payload = SettingsPayload(settings);
            payload["shopHours"] = HoursPayload(hours);
            return Ok(payload);
        }

        [HttpPut]
        [Authorize(Roles = "ADMIN")]
        public async Task<IActionResult> UpdateSettings([FromBody] UpdateShopSettingsViewModel model)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var settings = await _context.ShopSettings.FirstOrDefaultAsync();
            if (settings == null) return NotFound(new { message = "Shop settings not found" });

            settings.BufferMin = model.BufferMin;
            settings.DefaultAdminBookingDurationMin = model.DefaultAdminBookingDurationMin;
            settings.GraceMinutesAfterClose = model.GraceMinutesAfterClose;
            settings.MinAdvanceBookingMinutes = model.MinAdvanceBookingMinutes;
            settings.MaxAdvanceBookingDays = model.MaxAdvanceBookingDays;
            settings.RefundCutoffHours = model.RefundCutoffHours;
            settings.SlotStepMin = model.SlotStepMin;
            await _context.SaveChangesAsync();
            return Ok(SettingsPayload(settings));
        }

        /* Req 2 from the hours side: shop hours are a ceiling over every barber shift, so narrowing them
         * has to be refused while any shift still sticks out - and has to say WHOSE, because "adjust the
         * schedules first" is only actionable if the admin knows which ones.
         *
         * Refusing rather than clamping the shifts, and rather than letting the hours through and flagging
         * the fallout: clamping silently rewrites someone's roster, and letting it through would strand
         * bookings on an hours change, which is the thing this ordering exists to prevent. Made in the
         * right order, the hours change can't strand anything - it forces the admin to narrow the SHIFTS
         * first, and that path already warns about affected bookings and flags them (FindOrphanedBookings).
         *
         * All seven days in one request, because the check is a whole-week question: moving a barber's
         * Monday shift into Tuesday's wider window is valid as one edit and invalid as two. */
        [HttpPut("hours")]
        [Authorize(Roles = "ADMIN")]
        public async Task<IActionResult> UpdateShopHours([FromBody] UpdateShopHoursRequest model)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var rows = await _context.ShopHours.ToListAsync();
            if (rows.Count == 0) return NotFound(new { message = "Shop hours not found" });

            // The proposal as ShopHours objects, so the same ShopHoursResolver the booking paths use can
            // judge it - rather than a second, hand-rolled comparison that could disagree with it.
            var proposed = model.Days.Select(d => new ShopHours
            {
                DayOfWeek = (DayOfWeek)d.DayOfWeek,
                OpenTime = d.OpenTime,
                CloseTime = d.CloseTime,
                IsClosed = d.IsClosed
            }).ToList();

            /* Current and future versions only. An expired version governs no date from today on, so a
             * shift inside it can't be violated by hours that only apply going forward - and refusing an
             * hours change over a schedule nobody works any more would be unfixable, since superseded
             * versions are edited, never deleted. */
            var today = ShopClock.Today;
            var versions = await _context.BarberSchedules
                .Include(s => s.Shifts)
                .Where(s => s.EffectiveTo == null || s.EffectiveTo >= today)
                .Select(s => new
                {
                    s.BarberId,
                    BarberName = s.Barber!.User.Name + " " + s.Barber.User.Surname,
                    Shifts = s.Shifts.Select(sh => new { sh.DayOfWeek, sh.StartTime, sh.EndTime }).ToList()
                })
                .ToListAsync();

            var conflicts = new List<ConflictingShiftViewModel>();
            foreach (var v in versions)
                foreach (var sh in v.Shifts)
                {
                    var reason = ShopHoursResolver.ShiftViolation(
                        ShopHoursResolver.ForDay(proposed, sh.DayOfWeek), sh.StartTime, sh.EndTime);
                    if (reason == null) continue;
                    conflicts.Add(new ConflictingShiftViewModel
                    {
                        BarberId = v.BarberId,
                        BarberName = v.BarberName?.Trim(),
                        Day = sh.DayOfWeek.ToString(),
                        Shift = $"{sh.StartTime:HH\\:mm}-{sh.EndTime:HH\\:mm}",
                        Reason = reason
                    });
                }

            if (conflicts.Count > 0)
                return BadRequest(new
                {
                    message = "These hours would leave barber schedules outside the shop's opening hours. "
                            + "Adjust the schedules below first, then change the hours.",
                    conflicts = conflicts
                        .OrderBy(c => c.BarberName).ThenBy(c => c.Day)
                        .DistinctBy(c => new { c.BarberId, c.Day, c.Shift })
                        .ToList()
                });

            foreach (var row in rows)
            {
                var day = model.Days.First(d => d.DayOfWeek == (int)row.DayOfWeek);
                row.IsClosed = day.IsClosed;
                /* Times are written even for a closed day. They are ignored while it's closed, and keeping
                 * them means reopening the day restores the hours it had rather than an empty form. */
                row.OpenTime = day.OpenTime;
                row.CloseTime = day.CloseTime;
            }
            await _context.SaveChangesAsync();
            return Ok(HoursPayload(rows));
        }
    }
}
