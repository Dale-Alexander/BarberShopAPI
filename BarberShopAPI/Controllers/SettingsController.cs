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
    // stays ADMIN-only via the extra [Authorize] on the PUT below - the two attributes AND together, so
    // an update requires ADMIN even though the class grants read to both roles.
    [Authorize(Roles = "ADMIN,BARBER")]
    public class SettingsController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public SettingsController(BarberShopContext context)
        {
            _context = context;
        }

        // The settings row is seeded (Id == 1) in BarberShopContext, so it always exists.
        [HttpGet]
        public async Task<IActionResult> GetSettings()
        {
            var settings = await _context.ShopSettings.FirstOrDefaultAsync();
            if (settings == null) return NotFound(new { message = "Shop settings not found" });
            return Ok(new
            {
                bufferMin = settings.BufferMin,
                defaultAdminBookingDurationMin = settings.DefaultAdminBookingDurationMin,
                graceMinutesAfterClose = settings.GraceMinutesAfterClose,
                minAdvanceBookingMinutes = settings.MinAdvanceBookingMinutes,
                maxAdvanceBookingDays = settings.MaxAdvanceBookingDays,
                refundCutoffHours = settings.RefundCutoffHours
            });
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
            await _context.SaveChangesAsync();
            return Ok(new
            {
                bufferMin = settings.BufferMin,
                defaultAdminBookingDurationMin = settings.DefaultAdminBookingDurationMin,
                graceMinutesAfterClose = settings.GraceMinutesAfterClose,
                minAdvanceBookingMinutes = settings.MinAdvanceBookingMinutes,
                maxAdvanceBookingDays = settings.MaxAdvanceBookingDays,
                refundCutoffHours = settings.RefundCutoffHours
            });
        }
    }
}
