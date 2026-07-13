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
    [Authorize(Roles = "ADMIN")]
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
                graceMinutesAfterClose = settings.GraceMinutesAfterClose
            });
        }

        [HttpPut]
        public async Task<IActionResult> UpdateSettings([FromBody] UpdateShopSettingsViewModel model)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var settings = await _context.ShopSettings.FirstOrDefaultAsync();
            if (settings == null) return NotFound(new { message = "Shop settings not found" });

            settings.BufferMin = model.BufferMin;
            settings.DefaultAdminBookingDurationMin = model.DefaultAdminBookingDurationMin;
            settings.GraceMinutesAfterClose = model.GraceMinutesAfterClose;
            await _context.SaveChangesAsync();
            return Ok(new
            {
                bufferMin = settings.BufferMin,
                defaultAdminBookingDurationMin = settings.DefaultAdminBookingDurationMin,
                graceMinutesAfterClose = settings.GraceMinutesAfterClose
            });
        }
    }
}
