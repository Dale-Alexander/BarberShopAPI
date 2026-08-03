using BarberShopAPI.Data;
using BarberShopAPI.Services;
using BarberShopAPI.Migrations;
using BarberShopAPI.Models;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.ViewModels;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PhoneNumbers;
using SixLabors.ImageSharp;
using Stripe;
using System.Net.WebSockets;
using Microsoft.IdentityModel.Tokens;
using BarberShopAPI.Common;
namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController : ControllerBase
    {
        /* Customer-only lead-time / horizon rule. The bounds come from ShopSettings (minAdvanceMinutes,
         * maxAdvanceDays) so the shop can tune them; the customer slot picker mirrors the same values. */
        private static (bool IsValid, string? Error) ValidateBookingTime(DateTime startDateTime, int minAdvanceMinutes, int maxAdvanceDays)
        {
            DateTime now = ShopClock.Now;
            if (startDateTime < now) return (false, "Cannot book slots in the past");
            if (startDateTime < now.AddMinutes(minAdvanceMinutes)) return (false, $"Bookings must be made at least {minAdvanceMinutes} minutes in advance");
            if (startDateTime > now.AddDays(maxAdvanceDays)) return (false, $"Bookings cannot be made more than {maxAdvanceDays} days in advance");
            return (true, null);
        }


        private readonly BarberShopContext _context;
        private readonly IEmailService _emailService;
        public BookingsController(BarberShopContext context, IEmailService emailService)
        {
            _context = context;
            _emailService = emailService;
        }

        private bool IsValidPhoneNumber(string phone)
        {
            try
            {
                if (!phone.StartsWith("+"))
                    return false;

                var phoneUtil = PhoneNumberUtil.GetInstance();
                var parsed = phoneUtil.Parse(phone, null);
                return phoneUtil.IsValidNumber(parsed);
            }
            catch (NumberParseException)
            {
                return false;
            }
        }

        private bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Trim().Length < 2) return false;//at least 2 chars long
            if (name.Any(c => char.IsDigit(c))) return false;//no digits
            // The name is split into first/last and stored in User.Name/User.Surname, each nvarchar(50).
            // Validate against the same split so an over-long part gets a clean 400 here instead of a
            // truncation error on save.
            var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var firstName = parts.Length > 0 ? parts[0] : "";
            var lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
            if (firstName.Length > 50 || lastName.Length > 50) return false;
            return true;
        }

        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPost("create-admin-booking")]
        public async Task<IActionResult> CreateAdminBookings([FromBody] AdminCreateBookingCreateViewModel model)
        {
            // A barber may only place bookings on their own chair: ignore any BarberId in the body and
            // pin it to the caller. Admins may book for any barber.
            if (!User.IsInRole("ADMIN"))
            {
                var callerBarberId = await CallerBarberId();
                if (callerBarberId == null) return StatusCode(403, new { message = "You do not have a barber profile" });
                model.BarberId = callerBarberId.Value;
            }

            if (model.DefaultDurationMin == null || model.DefaultDurationMin <= 0)
                return BadRequest(new { message = "Default booking duration is required for admin bookings" });

            var barber = await _context.Barbers.Where(b => b.isActive == true && b.Id == model.BarberId).Select(b => new
            {
                b.Id,
                FullName = b.User.Name + " " + b.User.Surname
            }).FirstOrDefaultAsync();
            if (barber == null) return BadRequest(new { message = "Barber was not found" });
            if (model.StartDateTime < ShopClock.Now) return BadRequest(new { message = "Cannot book slots in the past" });
            int durationMinutes = model.DefaultDurationMin;

            var endDateTime = model.StartDateTime.AddMinutes(durationMinutes);
            var appointmentDate = DateOnly.FromDateTime(model.StartDateTime);
            var appointmentTime = TimeOnly.FromDateTime(model.StartDateTime);
            var endTime = TimeOnly.FromDateTime(endDateTime);
            var staffSettings = await _context.ShopSettings
                .Select(s => new { s.BufferMin, s.GraceMinutesAfterClose }).FirstAsync();

            /* Staff book inside the barber's working hours by default, same as customers and same as the
             * reschedule path. What staff KEEP unconditionally is the lead-time and horizon exemption
             * above: they can put someone in ten minutes from now or a year out.
             *
             * Outside those hours needs an explicit ConfirmOutsideHours - the shop staying open late for a
             * regular is real, but it has to be a decision someone made rather than something a request
             * drifted into. Having this apply on a reschedule but not on a create would have meant a slot
             * you could book from scratch but couldn't move a booking into. */
            var scheduleVersions = await _context.BarberSchedules
                .Include(s => s.Shifts)
                .Where(s => s.BarberId == model.BarberId
                            && s.EffectiveFrom <= appointmentDate
                            && (s.EffectiveTo == null || s.EffectiveTo >= appointmentDate))
                .ToListAsync();
            if (!ScheduleResolver.FitsWithinAShift(scheduleVersions, appointmentDate, appointmentTime, endTime, staffSettings.GraceMinutesAfterClose))
            {
                var refusal = OutsideHoursRefusal(model.ConfirmOutsideHours, barber.FullName);
                if (refusal != null) return refusal;
            }

            var closureDate = await _context.ShopClosures.FirstOrDefaultAsync(s =>
            s.IsActive == true && (s.BarberId == null || s.BarberId == model.BarberId) && ((s.EndDate == null && s.StartDate == appointmentDate) ||
            (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) && (s.IsFullDay || (s.StartTime <
            endTime && s.EndTime > appointmentTime))
            );
            if (closureDate != null) return BadRequest(new { message = "The chosen slot falls on an unavailable slot" });

            // The between-booking buffer still applies, so staff can't wedge a client into another's gap.
            var buffer = staffSettings.BufferMin;

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var overlap = await _context.Bookings.AnyAsync(b =>
                b.BarberId == model.BarberId &&
                b.Status != BookingStatus.CANCELLED &&
                b.StartDateTime < endDateTime.AddMinutes(buffer) &&
                b.StartDateTime.AddMinutes(b.DurationMin + buffer) > model.StartDateTime);
                if (overlap) return BadRequest(new { message = "The chosen slot overlaps with an existing booking" });


                // Name is optional for staff bookings (e.g. a walk-in known only by phone). Validate it
                // only when one was actually provided; a blank name is allowed through.
                var hasName = !string.IsNullOrWhiteSpace(model.FullName);
                if (hasName && !IsValidName(model.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(model.Phone))
                    return BadRequest(new { message = "Invalid phone number" });

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == model.Phone);
                var parts = model.FullName?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                var firstName = parts.Length > 0 ? parts[0] : "";
                var lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                if (user == null)
                {
                    user = new User
                    {
                        Name = firstName,
                        Surname = lastName,
                        Phone = model.Phone
                    };
                    _context.Users.Add(user);
                }
                else
                {
                    // Returning customer (matched by phone): always overwrite the name with whatever was
                    // entered on this booking - even a blank one - so the record reflects the latest input.
                    // A provided name is validated above; a blank name is allowed and clears it.
                    user.Name = firstName;
                    user.Surname = lastName;
                }
                await _context.SaveChangesAsync();
                var booking = new Booking
                {
                    BarberId = barber.Id,
                    StartDateTime = model.StartDateTime,
                    Status = BookingStatus.COMPLETED,
                    DurationMin = durationMinutes,
                    UserId = user.Id,
                    // Required (NOT NULL + unique) even though admin bookings aren't reached via a guest
                    // slug URL - without it this insert would violate the PublicId constraint.
                    PublicId = Guid.NewGuid().ToString("N")
                };
                _context.Bookings.Add(booking);
                await _context.SaveChangesAsync();

                var payment = new Payment
                {
                    BookingId = booking.Id,
                    Amount = null,
                };


                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
                return Ok(new
                {
                    booking.Id,
                    booking.StartDateTime,
                    booking.Status,
                    BarberId = barber.Id,
                    BarberName = barber.FullName,
                    booking.DurationMin
                });
            }
            catch(DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601))
            {
                // This guard sits over two saves: the customer (User.Phone unique) and the booking
                // ((BarberId, StartDateTime) unique). Both throw 2627/2601, so keep the message honest
                // for either race rather than claiming it was always the slot.
                await transaction.RollbackAsync();
                return BadRequest(new { message = "That slot or customer was just saved by someone else, please try again" });
            }
            catch(Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "An error occurred while creating the booking" });
            }
        }
        [HttpPost("create-pending")]
        public async Task<IActionResult> CreatePendingBookings([FromBody] PendingBookingCreateViewModel model)
        {
            if (model.ServicesIds.Count == 0)
                return BadRequest(new { message = "Services are required for user bookings" });
            // The customer's own checkout path, so it honours AcceptsNewBookings as well - the roster above
            // already hides a winding-down barber, this catches a stale page posting their id directly.
            // The staff-side CreateBooking above deliberately does NOT: staff may still book someone who's
            // working their notice.
            var barber = await _context.Barbers.Where(b => b.isActive == true && b.AcceptsNewBookings && b.Id == model.BarberId).Select(b => new
            {
                b.Id, FullName = b.User.Name + " " + b.User.Surname
            }).FirstOrDefaultAsync();
            if (barber == null) return BadRequest(new { message = "Barber was not found" });
            // Load the shop settings once - used for the lead-time/horizon rule, the working-hours grace
            // and the between-booking buffer.
            var settings = await _context.ShopSettings.FirstAsync();
            var (isValid, error) = ValidateBookingTime(model.StartDateTime, settings.MinAdvanceBookingMinutes, settings.MaxAdvanceBookingDays);
            if (!isValid) return BadRequest(new { message = error });
            var services = await _context.Services.Where(s => s.IsActive == true && model.ServicesIds.Contains(s.Id)).ToListAsync();
            if (services.Count == 0) return BadRequest(new { message = "The chosen services do not exist" });
            if (services.Count != model.ServicesIds.Count) return BadRequest(new { message = "One or more services were not found or are inactive" });
            int durationMinutes = services.Sum(s => s.DurationMin);
            /*if (model.IsAccessedByAdmin)
            {
                durationMinutes = model.DefaultBookingDurationMin.Value;
            }*/

            var endDateTime = model.StartDateTime.AddMinutes(durationMinutes);
            var appointmentDate = DateOnly.FromDateTime(model.StartDateTime);
            var appointmentTime = TimeOnly.FromDateTime(model.StartDateTime);
            var endTime = TimeOnly.FromDateTime(endDateTime);

            // Customer-facing path: the whole appointment must fall within the barber's scheduled
            // working hours for that date (up to GraceMinutesAfterClose past the day's last shift).
            // Load only the version(s) that could govern this date; fails closed if none exists.
            var scheduleVersions = await _context.BarberSchedules
                .Include(s => s.Shifts)
                .Where(s => s.BarberId == model.BarberId
                            && s.EffectiveFrom <= appointmentDate
                            && (s.EffectiveTo == null || s.EffectiveTo >= appointmentDate))
                .ToListAsync();
            if (!ScheduleResolver.FitsWithinAShift(scheduleVersions, appointmentDate, appointmentTime, endTime, settings.GraceMinutesAfterClose))
                return BadRequest(new { message = "Outside the barber's working hours" });
            var closureDate = await _context.ShopClosures.FirstOrDefaultAsync(s =>
            s.IsActive == true && (s.BarberId == null || s.BarberId == model.BarberId) && ((s.EndDate == null && s.StartDate == appointmentDate)||
            (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) && (s.IsFullDay || (s.StartTime <
            endTime && s.EndTime > appointmentTime))
            );
            if (closureDate != null) return BadRequest(new { message = "The chosen slot falls on an unavailable slot" });

            // Require a `buffer`-minute gap between consecutive bookings (see ShopSettings.BufferMin).
            // Adding buffer to both interval ends enforces exactly one gap regardless of order; an
            // exactly-buffer gap is allowed.
            var buffer = settings.BufferMin;
            var overlap = await _context.Bookings.AnyAsync(b =>
            b.BarberId == model.BarberId &&
            b.Status != BookingStatus.CANCELLED &&
            b.StartDateTime < endDateTime.AddMinutes(buffer) &&
            b.StartDateTime.AddMinutes(b.DurationMin + buffer) > model.StartDateTime);
            if (overlap) return BadRequest(new { message = "The chosen slot overlaps with an existing booking" });
            var booking = new Booking
            {
                BarberId = barber.Id,
                StartDateTime = model.StartDateTime,
                Status = BookingStatus.PENDING,
                DurationMin = durationMinutes,
                // Non-guessable public id for the guest URLs (see Booking.PublicId).
                PublicId = Guid.NewGuid().ToString("N")
            };

                booking.Services = services.Select(s => new BookingService
                {
                    Service = s,
                }).ToList();

            _context.Bookings.Add(booking);
            // No user is attached to a PENDING booking, so the only unique index this can hit is
            // (BarberId, StartDateTime). The overlap check above is a read, so two customers racing
            // for the same barber+time can both pass it; the DB index rejects the loser with 2627/2601.
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601))
            {
                return BadRequest(new { message = "This slot was just booked by someone else, please try again" });
            }
            catch(Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected server error occurred." });
            }

            return Ok(new
            {
                // PublicId is what the frontend puts in the /checkout URL; the int Id is not exposed.
                PublicId = booking.PublicId,
                booking.StartDateTime,
                booking.Status,
                BarberId = barber.Id,
                BarberName = barber.FullName,
                booking.DurationMin
            });
        }
        [HttpGet("alreadypaid/{bookingId}")]
        public async Task<IActionResult> GetAlreadyPaidBookingDetails(string bookingId)
        {
            try
            {
                var booking = await _context.Bookings
                    .Where(b => b.PublicId == bookingId)
                    .Select(b => new
                    {
                        b.Status,
                        // A booking cancelled while still PENDING never got a linked user or a payment,
                        // so User/Payment can be null here - keep these null-safe or the projection 500s.
                        CustomerName = b.User != null ? b.User.Name + " " + b.User.Surname : null,
                        BarberName = b.Barber.User.Name + " " + b.Barber.User.Surname,
                        ServiceNames = b.Services.Select(bs => bs.Service.Name).ToList(),
                        // Returned so the cancelled screen's "book again" can carry the same services into a
                        // fresh booking (the customer flow POSTs these to create-pending). Duration comes along
                        // so the date/time picker greys slots by the real appointment length.
                        ServiceIds = b.Services.Select(bs => bs.Service.Id).ToList(),
                        b.DurationMin,
                        AmountPaid = b.Services.Sum(bs => bs.Service.Price),
                        Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                        Time = b.StartDateTime.ToString("h:mm tt"),
                        PaymentMethod = b.Payment != null ? b.Payment.Method.ToString() : null,
                        // Drives the reason-specific cancelled-screen copy (barber unavailable vs closure vs generic).
                        b.CancellationReason,
                        BookingId = b.Id
                    })
                    .FirstOrDefaultAsync();

                if (booking == null)
                    return NotFound(new { message = "Booking not found" });

                if (booking.Status == BookingStatus.PENDING)
                    return BadRequest(new { message = "This booking is still pending" });

                return Ok(booking);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }

        [HttpPost("confirm-cash")]
        public async Task<IActionResult> ConfirmCashBooking([FromBody] ConfirmCashBookingViewModel model)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.PublicId == model.BookingId);
                if (booking == null) return BadRequest(new { message = "Booking not found" });
                if (booking.Status != BookingStatus.PENDING) return BadRequest(new { message = "Only Pending Bookings can be confirmed" });

                // A closure or schedule change can strand this still-PENDING booking between creation and
                // confirmation. Cancel it and reject: nothing has been charged on the cash path, so there's
                // no refund and no email (the customer is here and gets the cancelled screen synchronously) -
                // any in-flight card attempt is voided below so it can't become one either. Cancelling
                // - rather than just 400ing and leaving it PENDING - is what lets the frontend's "no longer
                // pending" redirect land on a proper cancelled screen (the alreadypaid endpoint 400s while a
                // booking is still PENDING). Mirrors how a closure pre-cancels conflicting bookings.
                async Task<IActionResult> CancelAndReject(CancellationReason reason, string message)
                {
                    /* Void the in-flight card payment BEFORE killing the booking - the same order the closure
                     * sweep (BookingConflictCanceller) and the expiry job use. The customer may have clicked
                     * Pay Online earlier and abandoned it, leaving a live PaymentIntent on this booking;
                     * cancelling the booking without voiding it lets that charge still land, and the customer
                     * only gets their money back days later via the webhook's orphaned-charge refund. If
                     * Stripe won't void it (it's already succeeding), that guard is exactly the fallback -
                     * so we log and carry on rather than blocking the rejection the customer is waiting on. */
                    if (!string.IsNullOrWhiteSpace(booking.StripePaymentIntentId))
                    {
                        try { await new PaymentIntentService().CancelAsync(booking.StripePaymentIntentId); }
                        catch (StripeException ex)
                        {
                            Console.WriteLine($"Booking {booking.Id}: couldn't cancel PaymentIntent {booking.StripePaymentIntentId} on {reason} reject (likely already succeeding); webhook will refund if it lands: {ex.Message}");
                        }
                    }

                    booking.Status = BookingStatus.CANCELLED;
                    booking.CancellationReason = reason;
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                    return BadRequest(new { message });
                }

                // The assigned barber can be deactivated between creation and confirmation. Deactivation does
                // cancel the barber's future bookings, but BookingConflictCanceller deliberately LEAVES a
                // pending one alone when its in-flight PaymentIntent can't be voided, and a booking created in
                // the instant before isActive flipped is never in that sweep at all. Either way the customer
                // would be confirmed onto a barber who no longer works here, so it belongs in the same
                // "is this slot still real?" gate as the closure and schedule re-checks below.
                // Checked FIRST, matching the webhook: a departed barber is the only reason whose recovery is
                // "rebook with someone else" rather than "pick another time", so it must not be masked by a
                // closure or a schedule change that happens to apply as well.
                var barberIsActive = await _context.Barbers.AnyAsync(b => b.Id == booking.BarberId && b.isActive);
                if (!barberIsActive)
                    return await CancelAndReject(CancellationReason.BarberUnavailable, "The barber for this booking is no longer available, so it can no longer be confirmed");

                // Same closure re-check as the webhook: a PENDING booking must never be confirmed to
                // COMPLETED on a slot a closure now covers.
                var appointmentDate = DateOnly.FromDateTime(booking.StartDateTime);
                var appointmentTime = TimeOnly.FromDateTime(booking.StartDateTime);
                var endTime = TimeOnly.FromDateTime(booking.StartDateTime.AddMinutes(booking.DurationMin));
                var closure = await _context.ShopClosures.FirstOrDefaultAsync(s =>
                    s.IsActive == true && (s.BarberId == null || s.BarberId == booking.BarberId) &&
                    ((s.EndDate == null && s.StartDate == appointmentDate) ||
                     (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) &&
                    (s.IsFullDay || (s.StartTime < endTime && s.EndTime > appointmentTime)));

                if (closure != null)
                    return await CancelAndReject(CancellationReason.ShopClosure, "This slot now falls on a shop closure and can no longer be confirmed");
                //The closure checks is for when the admin created a closure between PENDING and COMPLETED stage

                // Same re-check for the barber's working-hours schedule: an admin could have changed the
                // schedule between PENDING and confirmation, leaving this slot outside the barber's hours.
                // Closures and schedule answer the same "is this slot still open?" question, so we re-check
                // both here and cancel the booking the same way.
                var graceMin = await _context.ShopSettings.Select(s => s.GraceMinutesAfterClose).FirstAsync();
                var scheduleVersions = await _context.BarberSchedules
                    .Include(s => s.Shifts)
                    .Where(s => s.BarberId == booking.BarberId
                                && s.EffectiveFrom <= appointmentDate
                                && (s.EffectiveTo == null || s.EffectiveTo >= appointmentDate))
                    .ToListAsync();
                // Cancelling rather than grandfathering is the deliberate choice documented at the webhook's
                // schedule-change guard - the two confirmation points must keep telling the same story.
                if (!ScheduleResolver.FitsWithinAShift(scheduleVersions, appointmentDate, appointmentTime, endTime, graceMin))
                    return await CancelAndReject(CancellationReason.ScheduleChange, "This slot now falls outside the barber's working hours and can no longer be confirmed");
                var existingPayment = await _context.Payments.FirstOrDefaultAsync(p => p.BookingId == booking.Id);
                if (existingPayment != null) return BadRequest(new { message = "A payment already exists for this booking" });

                if (!IsValidName(model.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(model.Phone))
                    return BadRequest(new { message = "Invalid phone number" });
                if (!isValidEmail(model.Email))
                {
                    return BadRequest(new { message = "Invalid email address"});
                }

                // Recorded total comes from the booking's own services, never from the request body, so the
                // amount that lands on the payment/receipt/dashboard can't be tampered with by the client.
                var realTotal = await _context.Bookings
                    .Where(b => b.Id == booking.Id)
                    .Select(b => b.Services.Sum(bs => bs.Service.Price))
                    .FirstOrDefaultAsync();
                if (realTotal <= 0)
                    return BadRequest(new { message = "This booking has no payable services" });

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == model.Phone);
                var parts = model.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                //the above splits by space and ignores extra spaces
                string firstName = parts.Length > 0 ? parts[0] : "";
                string lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                if (user == null)
                {
                    user = new User
                    {
                        Name = firstName,
                        Surname = lastName,
                        Phone = model.Phone,
                    };
                    _context.Users.Add(user);
                }
                else
                {
                    // Returning customer (matched by phone): refresh the name so a corrected spelling -
                    // or a different person sharing the phone - is reflected on this booking instead of
                    // silently keeping the name from their first ever booking. FullName is already
                    // validated above, so these are never empty.
                    user.Name = firstName;
                    user.Surname = lastName;
                }
                await _context.SaveChangesAsync();
                // The customer may have started a card payment earlier (clicked Pay Online) without finishing,
                // leaving a live PaymentIntent on this booking. Switching to cash doesn't void it, so if we
                // complete as cash without cancelling it, that card attempt could still succeed later (e.g. a
                // slow 3-D Secure or a dropped success response) and double-charge them. Cancel it now; if
                // Stripe won't (it's already succeeding), the webhook's orphaned-charge guard refunds it.
                if (!string.IsNullOrWhiteSpace(booking.StripePaymentIntentId))
                {
                    try { await new PaymentIntentService().CancelAsync(booking.StripePaymentIntentId); }
                    catch (StripeException ex)
                    {
                        Console.WriteLine($"Booking {booking.Id}: couldn't cancel PaymentIntent {booking.StripePaymentIntentId} on cash confirm (likely already succeeding); webhook will refund if it lands: {ex.Message}");
                    }
                }
                booking.UserId = user.Id;
                booking.Status = BookingStatus.COMPLETED;
                booking.ContactEmail = model.Email;

                    var reminderTime = booking.StartDateTime.AddHours(-2);
                    var delay = ShopClock.ToUtc(reminderTime) - DateTime.UtcNow;
                    if(delay > TimeSpan.Zero)
                    {
                        var jobId = BackgroundJob.Schedule<IEmailService>(service => service.sendBookingReminderEmailAsync(booking.Id), delay);
                        booking.ReminderJobId = jobId;
                    }


                var payment = new Payment
                {
                    BookingId = booking.Id,
                    Amount = realTotal
                };
                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                    BackgroundJob.Enqueue<IEmailService>(
                        service => service.sendBookingConfirmationEmailAsync(booking.Id));
                
                /* 
                 *there is a reason why the schedule is inside the and the enqueue is outside 
                 *the transaction
                 */
                return Ok(new
                {
                    bookingId = booking.Id,
                    status = booking.Status.ToString(),
                    paymentId = payment.Id,
                    amount = payment.Amount
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { message = "An error occurred while processing the payment"});
            }
        }
        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPatch("mark-cash-paid/{bookingId}")]
        public async Task<IActionResult> MarkCashPaymentAsPaid(int bookingId, [FromBody] MarkCashPaidViewModel? model = null)
        {
            try
            {
                // A barber may only mark their own bookings' cash payments; an admin may mark any.
                if (!User.IsInRole("ADMIN"))
                {
                    var callerBarberId = await CallerBarberId();
                    var ownsBooking = await _context.Bookings.AnyAsync(b => b.Id == bookingId && b.BarberId == callerBarberId);
                    if (!ownsBooking) return StatusCode(403, new { message = "You can only update your own bookings" });
                }
                var payment = await _context.Payments.FirstOrDefaultAsync(p => p.BookingId == bookingId);
                if (payment == null) return NotFound(new { message = "Payment not found for this booking" });
                if (payment.Method != Models.Enums.PaymentMethod.CASH)
                    return BadRequest(new { message = "Only cash payments can be marked as paid this way" });
                if (payment.Status == PaymentStatus.COMPLETED)
                    return BadRequest(new { message = "This payment has already been marked as paid" });

                // Admin (phone) bookings have no amount on file - it's only known once the customer pays in
                // person, so it's captured here at mark-paid time. Optional by design: an admin reconciling
                // later who doesn't remember the amount can still mark the booking collected with it left
                // unknown. Customer cash bookings already carry their amount and send nothing. Bounds mirror
                // ConfirmCashBooking / the CK_Payments_Amount_Max400 check constraint.
                if (model?.Amount is decimal amount)
                {
                    if (amount <= 0 || amount > 400)
                        return BadRequest(new { message = "Amount must be greater than zero and at most 400" });
                    payment.Amount = amount;
                }

                payment.Status = PaymentStatus.COMPLETED;
                payment.PaidAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = "Payment marked as paid",
                    paymentId = payment.Id,
                    amount = payment.Amount,
                    paidAt = payment.PaidAt
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }
        /* Corrects the collected amount on an already-paid CASH booking so the revenue chart / stat cards
         * (which sum Payment.Amount over COMPLETED payments) reflect what was actually taken - e.g. a
         * discount on the day, or services that differed from what was booked. Deliberately has no past
         * restriction: this is a bookkeeping correction on a completed booking. */
        // Admin-only: correcting the collected amount is a revenue/bookkeeping action the owner owns. A
        // barber's page shows service names and the collected amount but doesn't expose this edit, and the
        // endpoint enforces the same so the restriction isn't merely cosmetic.
        [Authorize(Roles = "ADMIN")]
        [HttpPatch("edit-amount/{bookingId}")]
        public async Task<IActionResult> EditCollectedAmount(int bookingId, [FromBody] EditPaymentAmountViewModel model)
        {
            try
            {
                // Bounds mirror ConfirmCashBooking / mark-cash-paid / the CK_Payments_Amount_Max400 constraint.
                if (model.Amount <= 0 || model.Amount > 400)
                    return BadRequest(new { message = "Amount must be greater than zero and at most 400" });

                var booking = await _context.Bookings.Include(b => b.Payment).FirstOrDefaultAsync(b => b.Id == bookingId);
                if (booking == null) return NotFound(new { message = "Booking not found" });
                var payment = booking.Payment;
                if (payment == null) return NotFound(new { message = "Payment not found for this booking" });

                // Cash only. A card payment's amount is whatever Stripe actually captured; hand-editing it
                // would desync the books from Stripe with no refund to back it, so card adjustments must go
                // through an actual Stripe refund instead of this endpoint.
                if (payment.Method != Models.Enums.PaymentMethod.CASH)
                    return BadRequest(new { message = "Only cash payment amounts can be edited" });

                // Only a collected payment on a confirmed booking is in the revenue figures at all. An
                // uncollected cash booking captures its amount through mark-as-paid instead, so send them there.
                if (booking.Status != BookingStatus.COMPLETED || payment.Status != PaymentStatus.COMPLETED)
                    return BadRequest(new { message = "Only collected bookings can have their amount edited. Mark it as paid first." });

                payment.Amount = model.Amount;
                await _context.SaveChangesAsync();

                return Ok(new { message = "Amount updated", paymentId = payment.Id, amount = payment.Amount });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected server error occurred" });
            }
        }

        [HttpPost("payment-intent")]
        public async Task<IActionResult> StartBookingCardFlow([FromBody] PaymentIntentViewModel request)
        {
            Console.WriteLine($"{request.BookingId}, {request.FullName}, {request.Phone}");
            try
            {
                var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.PublicId == request.BookingId);
                if (booking == null) return BadRequest(new { message = "Booking not found" });
                if (booking.Status != BookingStatus.PENDING)
                    return BadRequest(new { message = "This booking can no longer be paid for" });

                if (!IsValidName(request.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(request.Phone))
                    return BadRequest(new { message = "Invalid phone number" });
                if (!isValidEmail(request.Email))
                    return BadRequest(new { message = "Invalid Email Address" });

                // The amount is derived from the booking's own services here - NEVER from the request body -
                // so a customer can't POST a smaller amount and underpay for their appointment. Stripe works
                // in cents, hence *100.
                var realTotal = await _context.Bookings
                    .Where(b => b.Id == booking.Id)
                    .Select(b => b.Services.Sum(bs => bs.Service.Price))
                    .FirstOrDefaultAsync();
                if (realTotal <= 0)
                    return BadRequest(new { message = "This booking has no payable services" });

                var paymentIntentOptions = new PaymentIntentCreateOptions
                {
                    Amount = (long)(realTotal * 100m),
                    Currency = "eur",
                    // Let the Payment Element surface card + wallets (Apple Pay / Google Pay) + Revolut Pay.
                    // Wallets ride the card rail and only appear via automatic payment methods, not an
                    // explicit ["card"] list. AllowRedirects = "always" is required for Revolut Pay, which is
                    // redirect-based (the customer leaves to Revolut and returns to return_url). The webhook
                    // (WebHookController) and the return page (Summary.jsx) already handle the round-trip:
                    // slot-expired/closed during payment is auto-refunded, and the success page polls for the
                    // webhook before confirming. IMPORTANT: which methods actually show is governed by the
                    // Stripe Dashboard (per-mode) - keep it to Card/Apple Pay/Google Pay/Revolut Pay, or a
                    // redirect method like Klarna could surface now that redirects are allowed.
                    AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                    {
                        Enabled = true,
                        AllowRedirects = "always"
                    },
                    Metadata = new Dictionary<string, string> {
                            // The INTERNAL int id goes in the metadata (the webhook parses it as an int),
                            // not the public slug - keeps the webhook path unchanged.
                            {"BookingId", booking.Id.ToString() },
                            {"FullName", request.FullName },
                            {"Phone", request.Phone },
                            {"Email", request.Email }
                        }
                };

                var service = new PaymentIntentService();

                if (!string.IsNullOrWhiteSpace(booking.StripePaymentIntentId))
                {
                    try
                    {
                        // Void whatever PaymentIntent was created for this booking last time
                        // (e.g. the customer refreshed mid-checkout) so it can never succeed later
                        // and become an orphaned charge nothing in the system is tracking anymore.
                        await service.CancelAsync(booking.StripePaymentIntentId);
                    }
                    catch (StripeException)
                    {
                        // Couldn't cancel it - most likely it already succeeded or is mid-processing.
                        // Don't create a second PaymentIntent on top of an unresolved one; that risks
                        // charging the customer twice. Let the existing attempt run its course instead.
                        return BadRequest(new { message = "A payment is already being processed for this booking" });
                    }
                }

                var paymentIntent = await service.CreateAsync(paymentIntentOptions);

                booking.StripePaymentIntentId = paymentIntent.Id;
                await _context.SaveChangesAsync();

                return StatusCode(201, new
                {
                    message = "finish payment via Stripe",
                    clientSecret = paymentIntent.ClientSecret
                });
            }
            catch(StripeException ex) { 
            Console.WriteLine(ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
            catch(Exception ex){
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /* The two filter axes the admin/barber tables expose. They used to share one `status` param, which
         * made "All" a lie - it meant "all payment statuses of CONFIRMED bookings" and silently hid every
         * cancellation. They're orthogonal (a cancelled booking can still read as paid when the refund
         * failed, which is exactly the row worth finding), so they're filtered independently here.
         *
         * bookingStatus: CANCELLED -> cancellations only; ALL -> confirmed + cancelled; anything else
         * (including null) -> CONFIRMED, which stays the default so the table doesn't lead with cancellations.
         * ALL still excludes BookingStatus.PENDING - a checkout that's currently in flight isn't a booking yet.
         *
         * paymentStatus: PAID / UNPAID narrow by the payment row; null means any. */
        private static IQueryable<Booking> ApplyStatusFilters(IQueryable<Booking> query, string? bookingStatus, string? paymentStatus)
        {
            /* Only ever show bookings that actually became real. A booking gets its User and its Payment at
             * the moment it's confirmed - admin create, cash confirm, or the card webhook - so a null Payment
             * means the customer opened checkout and never finished. BookingExpiryJob marks those CANCELLED,
             * the same status a staff cancellation uses, so without this the Cancelled and All views fill up
             * with abandoned checkouts. They stay CANCELLED on purpose: that's what frees the slot again, and
             * every availability query keys off it. */
            query = query.Where(b => b.Payment != null);

            if (bookingStatus == "CANCELLED")
                query = query.Where(b => b.Status == BookingStatus.CANCELLED);
            else if (bookingStatus == "ALL")
                query = query.Where(b => b.Status != BookingStatus.PENDING);
            else
                query = query.Where(b => b.Status == BookingStatus.COMPLETED); // default

            if (paymentStatus == "PAID")
                query = query.Where(b => b.Payment.Status == PaymentStatus.COMPLETED);
            else if (paymentStatus == "UNPAID")
                query = query.Where(b => b.Payment.Status == PaymentStatus.PENDING);

            return query;
        }

        /* Shared filter for a single barber's bookings so the paginated list (barber-fetch) and the summary
         * tiles (barber-summary) always agree on which bookings they're describing - including when the
         * admin widens the filter to ALL, at which point the tiles count cancellations too. */
        private IQueryable<Booking> BuildBarberBookingsQuery(int barberId, DateTime? from, DateTime? to, string? bookingStatus, string? paymentStatus)
        {
            var query = ApplyStatusFilters(_context.Bookings.Where(b => b.BarberId == barberId), bookingStatus, paymentStatus);

            if (from.HasValue && to.HasValue)
                query = query.Where(b => b.StartDateTime >= from.Value && b.StartDateTime <= to.Value);

            return query;
        }

        /* Clamp paging so a bad/hostile query string can't ask for page 0 or a 100k-row page. */
        private static (int page, int pageSize) NormalisePaging(int page, int pageSize)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;
            return (page, pageSize);
        }

        /* Filter dates come from a date-only picker, so treat them as a full Malta day window: the From date
         * from its 00:00 through the To date's 23:59:59.999. Without this a same-day filter (e.g. the "Today"
         * shortcut, which sends the same date for both) collapses to a single instant and matches nothing.
         * Week/Month shortcuts are unaffected - they already send start-of-day / end-of-day. */
        private static (DateTime? from, DateTime? to) ToMaltaDayWindow(DateTime? fromDateUtc, DateTime? toDateUtc)
        {
            var from = fromDateUtc.HasValue ? ShopClock.FromUtc(fromDateUtc.Value).Date : (DateTime?)null;
            var to = toDateUtc.HasValue ? ShopClock.FromUtc(toDateUtc.Value).Date.AddDays(1).AddTicks(-1) : (DateTime?)null;
            return (from, to);
        }

        /* Shared by the two staff booking paths for a slot that falls outside the target barber's working
         * hours. Returns the response to send back, or null when the caller may proceed.
         *
         * The only question left is whether someone actually chose this. Without `confirmed` it's a 409
         * naming the barber, so the UI can ask "outside their hours - are you sure?" and resend. 409 rather
         * than 400 to match the closure and barber-deactivation flows, which use the same shape for "this
         * needs a human to agree first". A caller that never asks simply never gets through.
         *
         * There used to be a second check here - that a BARBER may only agree to this for their own chair,
         * not a colleague's. It's gone because it can no longer be reached: a barber creating a booking has
         * model.BarberId pinned to themselves, and a barber updating one is refused outright if they name a
         * different barber. Both guards run before this. Leaving a dead branch behind would have been worse
         * than removing it, since its message ("only an admin can book another barber outside their hours")
         * now understates the real rule - only an admin can put a booking on another barber at all.
         *
         * Deliberately does NOT flag the booking for review: it isn't a problem, it's a decision. If the
         * barber's hours later change, SchedulesController's sweep will surface it again like any other
         * booking left outside the new hours, which is the right moment to re-ask. */
        private IActionResult? OutsideHoursRefusal(bool confirmed, string? barberName)
        {
            if (confirmed) return null;

            var who = string.IsNullOrWhiteSpace(barberName) ? "this barber" : barberName;
            return Conflict(new
            {
                requiresConfirmation = true,
                outsideWorkingHours = true,
                message = $"This slot is outside {who}'s working hours."
            });
        }

        /* Ownership guard for the barber-scoped endpoints below. Both are open to ADMIN and
         * BARBER, but the {barberId} comes straight from the URL - without this, any logged-in
         * barber could read another barber's clients/phone numbers just by editing the id (IDOR).
         * Admins may view any barber; a barber may only view the barberId tied to their own user.
         *
         * The isActive predicate is defence in depth. A deactivated barber shouldn't be able to reach
         * these endpoints anyway - Login refuses to issue them a token and DeleteBarber's TokenVersion
         * bump kills any they still hold - but that leans entirely on login being the only place a token
         * is minted. This costs nothing (it's one more predicate on a query that already runs) and keeps
         * the guard honest if a refresh-token or SSO path is ever added. */
        private async Task<bool> BarberCanAccess(int barberId)
        {
            if (User.IsInRole("ADMIN")) return true;
            var callerUserId = int.Parse(User.FindFirst("id")?.Value ?? "0");
            return await _context.Barbers.AnyAsync(b => b.Id == barberId && b.UserId == callerUserId && b.isActive);
        }

        /* The caller's own Barber.Id when they're a BARBER, or null if they have no barber row - or if
         * that row is deactivated, same defence-in-depth reasoning as BarberCanAccess above. Returning
         * null makes the ownership checks in cancel / mark-cash-paid / update-booking fail closed, since
         * booking.BarberId can never equal null.
         * Used by the booking-mutation endpoints below to keep a barber's actions to their own
         * chair; ADMIN callers are checked with User.IsInRole and never rely on this. */
        private async Task<int?> CallerBarberId()
        {
            var callerUserId = int.Parse(User.FindFirst("id")?.Value ?? "0");
            return await _context.Barbers.Where(b => b.UserId == callerUserId && b.isActive).Select(b => (int?)b.Id).FirstOrDefaultAsync();
        }

        [Authorize(Roles ="ADMIN,BARBER")]
        [HttpGet("barber-fetch/{barberId}")]
        public async Task<IActionResult> GetBarberBookings([FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? bookingStatus,
            [FromQuery] string? paymentStatus,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            int barberId = 0)
        {
            if (!await BarberCanAccess(barberId)) return StatusCode(403, new { message = "You can only view your own bookings" });
            if (fromDate.HasValue != toDate.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
            if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value) return BadRequest(new { message = "From Date cannot be after To Date" });
            var (from, to) = ToMaltaDayWindow(fromDate, toDate);

            // Deliberately not filtered on isActive: a deactivated barber's past bookings are still real
            // history the admin needs to reach from the Inactive tab of the team screen. Access is gated
            // by BarberCanAccess above, and a deactivated barber can't obtain a session at all - Login
            // refuses to issue them a token and DeleteBarber's TokenVersion bump killed any they held -
            // so in practice this only ever serves the admin.
            var barber = await _context.Barbers.Where(b => b.Id == barberId)
                .Select(b => new { b.Id, b.User.Name, b.User.Surname, b.ImageUrl, b.isActive }).FirstOrDefaultAsync();
            if (barber == null) return NotFound(new { message = "Barber not found" });

            (page, pageSize) = NormalisePaging(page, pageSize);
            var query = BuildBarberBookingsQuery(barberId, from, to, bookingStatus, paymentStatus);
            var totalCount = await query.CountAsync();
            var bookings = await query
                .OrderByDescending(b => b.StartDateTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(b => new
                {
                    b.Id,
                    b.StartDateTime,
                    UserId = b.User.Id,
                    Name = b.User.Name,
                    Surname = b.User.Surname,
                    Phone = b.User.Phone,
                    // Booking lifecycle (CONFIRMED/CANCELLED), separate from the payment status below. The
                    // table badges each row with it so an ALL view reads correctly and cancelled rows don't
                    // offer actions the backend will reject.
                    Status = b.Status.ToString(),
                    // Payment info so a barber's own-bookings table can show status and drive
                    // mark-as-paid, mirroring the admin admin-fetch projection. Payment is a LEFT JOIN
                    // here, so these are null-safe for any booking without a payment row.
                    Amount = b.Payment.Amount,
                    PaymentMethod = b.Payment.Method.ToString(),
                    PaymentStatus = b.Payment.Status.ToString(),
                    Services = b.Services.Select(bs => new
                    {
                        ServiceName = bs.Service.Name
                    })
                }).ToListAsync();
            return Ok(new
            {
                barberName = barber.Name,
                barberSurname = barber.Surname,
                barberImageUrl = barber.ImageUrl,
                // Drives the page's read-mostly state (no Create Booking, deactivated notice). Sourced
                // from here rather than the navigation context so a deep link or refresh still knows.
                barberIsActive = barber.isActive,
                bookings,
                totalCount,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
        }

        /* Aggregate tiles for the barber bookings page (total bookings / distinct clients / distinct services).
         * Computed in SQL over the same filtered set as barber-fetch so the tiles match the table, instead of
         * the frontend counting a now-paginated page. */
        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpGet("barber-summary/{barberId}")]
        public async Task<IActionResult> GetBarberSummary(int barberId,
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? bookingStatus,
            [FromQuery] string? paymentStatus)
        {
            try
            {
                if (!await BarberCanAccess(barberId)) return StatusCode(403, new { message = "You can only view your own bookings" });
                if (fromDate.HasValue != toDate.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
                if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value) return BadRequest(new { message = "From Date cannot be after To Date" });
                var (from, to) = ToMaltaDayWindow(fromDate, toDate);

                var query = BuildBarberBookingsQuery(barberId, from, to, bookingStatus, paymentStatus);
                var totalBookings = await query.CountAsync();
                var distinctClients = await query.Select(b => b.UserId).Distinct().CountAsync();
                var distinctServices = await query.SelectMany(b => b.Services).Select(bs => bs.ServiceId).Distinct().CountAsync();

                return Ok(new { totalBookings, distinctClients, distinctServices });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        [Authorize(Roles = "ADMIN")]
        [HttpGet("admin-fetch")]
        public async Task<IActionResult> GetBookingsForAdmin(
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? bookingStatus,
            [FromQuery] string? paymentStatus,
            [FromQuery] bool needsReview = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            try
            {
                if (fromDate.HasValue != toDate.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
                if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value) return BadRequest(new { message = "From Date cannot be after To Date" });
                var (from, to) = ToMaltaDayWindow(fromDate, toDate);
                var query = _context.Bookings.AsQueryable();

                /* Needs-review is a cross-cutting worklist (a flagged booking can be any status), not a value
                 * on either filter axis - so it's its own flag and bypasses both rather than layering on top. */
                if (needsReview)
                    query = query.Where(b => b.NeedsReview);
                else
                    query = ApplyStatusFilters(query, bookingStatus, paymentStatus);

                if (from.HasValue && to.HasValue)
                {
                    query = query.Where(b =>
                        b.StartDateTime >= from.Value &&
                        b.StartDateTime <= to.Value
                    );
                }

                (page, pageSize) = NormalisePaging(page, pageSize);
                var totalCount = await query.CountAsync();
                var bookings = await query
                    .OrderByDescending(b => b.StartDateTime)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(b => new BookingsAdminViewModel
                    {
                        Id = b.Id,
                        FirstName = b.User.Name,
                        LastName = b.User.Surname,
                        StartDateTime = b.StartDateTime,
                        Status = b.Status.ToString(),
                        Amount = b.Payment.Amount,
                        PaymentMethod = b.Payment.Method.ToString(),//without .ToString the frontend would receive numbers like 0 or 1
                        PaymentStatus = b.Payment.Status.ToString(),
                        // Barber is required on a booking so this join is safe; User is nullable (a PENDING
                        // booking has no customer row yet), but this is a projection, so EF emits a LEFT
                        // JOIN and yields null rather than throwing - same as FirstName/LastName above.
                        BarberName = b.Barber.User.Name,
                        Phone = b.User.Phone,
                        NeedsReview = b.NeedsReview,
                        ReviewReason = b.ReviewReason
                    }).ToListAsync();
                /* The reason you dont do .Include() for Payment and User is because you are selecting(.Select()).
                 * When you use .Select() EF Core is smart enough to figure out exactly what data it
                 needs and writes a single SQL query with the necessary JOINS automatically.

                 When you do need .Include() is when you load the full entity and then access navigation
                properties outisde of a query - for example:
                var booking = await _context.Bookings
    .Include(b => b.User)
    .Include(b => b.Payment)
    .FirstOrDefaultAsync(b => b.Id == id);

// Then accessing it in C# code after the query
Console.WriteLine(booking.User.Name); // would be null without Include()*/
                return Ok(new
                {
                    bookings,
                    totalCount,
                    page,
                    pageSize,
                    totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
                });
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        /* Server-side aggregates for the admin dashboard's stat cards and performance chart. These used to be
         * computed in the browser over the full booking list - exactly the unbounded fetch pagination removes -
         * so they now come from SQL. Counts are over confirmed bookings and revenue over money actually
         * collected (see the two queries below); the month/day buckets read StartDateTime directly (Malta
         * wall-clock, same basis as ShopClock.Now). */
        [Authorize(Roles = "ADMIN")]
        [HttpGet("admin-summary")]
        public async Task<IActionResult> GetAdminSummary([FromQuery] int? year, [FromQuery] int? month)
        {
            try
            {
                // Guard the client-supplied year/month: an out-of-range year would blow up DateTime.DaysInMonth
                // below (valid 1-9999), and month must be a real month for the daily drill-down.
                if (year.HasValue && (year.Value < 1 || year.Value > 9999))
                    return BadRequest(new { message = "Year is out of range" });
                if (month.HasValue && (month.Value < 1 || month.Value > 12))
                    return BadRequest(new { message = "Month must be between 1 and 12" });

                // A confirmed booking (Status == COMPLETED) counts toward the "Bookings" metric whether or
                // not its money is in yet - this covers upcoming bookings and cash bookings not yet collected,
                // matching the bookings table. Revenue is stricter: only bookings whose payment is also
                // COMPLETED, so uncollected cash never inflates the euros. `paid` is a subset of `booked`.
                var booked = _context.Bookings.Where(b => b.Status == BookingStatus.COMPLETED);
                var paid = booked.Where(b => b.Payment.Status == PaymentStatus.COMPLETED);

                // Stat cards: this month vs last month.
                var nowMalta = ShopClock.Now;
                var thisMonthStart = new DateTime(nowMalta.Year, nowMalta.Month, 1);
                var nextMonthStart = thisMonthStart.AddMonths(1);
                var lastMonthStart = thisMonthStart.AddMonths(-1);

                // Bookings count over all confirmed bookings; revenue over money actually collected only.
                var thisMonthBookings = await booked.CountAsync(b => b.StartDateTime >= thisMonthStart && b.StartDateTime < nextMonthStart);
                var lastMonthBookings = await booked.CountAsync(b => b.StartDateTime >= lastMonthStart && b.StartDateTime < thisMonthStart);
                var thisMonthRevenue = await paid.Where(b => b.StartDateTime >= thisMonthStart && b.StartDateTime < nextMonthStart).SumAsync(b => (decimal?)b.Payment.Amount) ?? 0m;
                var lastMonthRevenue = await paid.Where(b => b.StartDateTime >= lastMonthStart && b.StartDateTime < thisMonthStart).SumAsync(b => (decimal?)b.Payment.Amount) ?? 0m;

                // Year dropdown covers any year that has confirmed bookings (the count-oriented view).
                var availableYears = await booked.Select(b => b.StartDateTime.Year).Distinct().OrderBy(y => y).ToListAsync();

                // Monthly series for the requested year (default: current year). count = every confirmed
                // booking that month; revenue = only the ones whose payment is collected.
                var targetYear = year ?? nowMalta.Year;
                var monthlyRaw = await booked
                    .Where(b => b.StartDateTime.Year == targetYear)
                    .GroupBy(b => b.StartDateTime.Month)
                    .Select(g => new { Month = g.Key, Count = g.Count(), Revenue = g.Sum(b => b.Payment.Status == PaymentStatus.COMPLETED ? b.Payment.Amount : 0m) })
                    .ToListAsync();
                var monthly = Enumerable.Range(1, 12).Select(m =>
                {
                    var bucket = monthlyRaw.FirstOrDefault(x => x.Month == m);
                    return new { count = bucket?.Count ?? 0, revenue = bucket?.Revenue ?? 0m };
                }).ToList();

                // Daily series only when a month is selected (chart drill-down). month is 1-based, validated above.
                object daily = null;
                if (month.HasValue)
                {
                    var dailyRaw = await booked
                        .Where(b => b.StartDateTime.Year == targetYear && b.StartDateTime.Month == month.Value)
                        .GroupBy(b => b.StartDateTime.Day)
                        .Select(g => new { Day = g.Key, Count = g.Count(), Revenue = g.Sum(b => b.Payment.Status == PaymentStatus.COMPLETED ? b.Payment.Amount : 0m) })
                        .ToListAsync();
                    var daysInMonth = DateTime.DaysInMonth(targetYear, month.Value);
                    daily = Enumerable.Range(1, daysInMonth).Select(d =>
                    {
                        var bucket = dailyRaw.FirstOrDefault(x => x.Day == d);
                        return new { count = bucket?.Count ?? 0, revenue = bucket?.Revenue ?? 0m };
                    }).ToList();
                }

                return Ok(new
                {
                    stats = new { thisMonthBookings, thisMonthRevenue, lastMonthBookings, lastMonthRevenue },
                    availableYears,
                    year = targetYear,
                    monthly,
                    daily
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        // Clears the review flag after a human has reconciled the money/status by hand (refunded in Stripe,
        // set the booking/payment to match). Purely a bookkeeping toggle - it doesn't touch Stripe or the
        // booking status, so the admin is responsible for making those right before marking it reviewed.
        [Authorize(Roles = "ADMIN")]
        [HttpPatch("mark-reviewed/{bookingId}")]
        public async Task<IActionResult> MarkBookingReviewed(int bookingId)
        {
            try
            {
                var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId);
                if (booking == null) return NotFound(new { message = "This booking was not found" });
                if (!booking.NeedsReview) return BadRequest(new { message = "This booking is not flagged for review" });

                booking.NeedsReview = false;
                booking.ReviewReason = null;
                await _context.SaveChangesAsync();

                return Ok(new { message = "Booking marked as reviewed" });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        // Standalone count so the dashboard can show a "needs review" alert badge that stays accurate no
        // matter what date/status filter the admin currently has applied to the table.
        [Authorize(Roles = "ADMIN")]
        [HttpGet("needs-review-count")]
        public async Task<IActionResult> GetNeedsReviewCount()
        {
            try
            {
                var count = await _context.Bookings.CountAsync(b => b.NeedsReview);
                return Ok(new { count });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpGet("admin/{bookingId}")]

        public async Task<IActionResult> GetBooking(int bookingId)
        {
            try
            {
                var booking = await _context.Bookings.Where(b => b.Id == bookingId).FirstOrDefaultAsync();
                if (booking == null) return NotFound("The booking to edit was not found");
                // A barber may only edit their own bookings; an admin may edit any.
                if (!User.IsInRole("ADMIN") && booking.BarberId != await CallerBarberId())
                    return StatusCode(403, new { message = "You can only edit your own bookings" });
                if (booking.Status == BookingStatus.PENDING) return BadRequest(new { message = "Cannot edit a pending booking"});
                if (booking.Status == BookingStatus.CANCELLED) return BadRequest(new { message = "Cannot edit a cancelled booking"});
                if (booking.StartDateTime <= ShopClock.Now) return BadRequest(new {message = "Cannot edit a booking that has passed"});

                return Ok(new EditBookingViewModel
                {
                    BarberId = booking.BarberId,
                    StartDateTime = booking.StartDateTime,
                    DurationMin = booking.DurationMin,
                });
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }

        [HttpGet("checkout/{bookingId}")]
        public async Task<IActionResult> GetBookingDetailsForCheckout(string bookingId)
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            /* The reason we add no cache is because when we were confirming a payment we were getting redirected to /booking/success but 
             * we could go back via the back button which should've redirected us to /cancelledorcompleted because the booking was already completed
             beforehand, but it isnt, we are staying on /checkout, this is because when we were navogating back, we were getting a cached response. This
            forces the browser to always hit the endpoint instead of serving a cached response*/
            try
            {
                var booking = await _context.Bookings
                    .Where(b => b.PublicId == bookingId)
                    .Select(b => new
                    {
                        b.Status,
                        BarberName = b.Barber.User.Name,
                        BarberSurname = b.Barber.User.Surname,
                        ImageUrl = b.Barber.ImageUrl,
                        ServiceNames = b.Services.Select(bs => bs.Service.Name).ToList(),
                        b.StartDateTime,
                        b.DurationMin,
                        Price = b.Services.Sum(bs => bs.Service.Price)
                    })
                    .FirstOrDefaultAsync();

                if (booking == null)
                    return NotFound(new { message = "This booking was not found" });

                if (booking.Status != BookingStatus.PENDING)
                    return BadRequest(new { message = "This booking is either already completed or cancelled" });

                return Ok(booking);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new { message = "Unexpected Server error occurred" });
            }
        }
        /* refundAnyway lets staff override the 24h no-refund policy and force a full refund - for goodwill,
         * or a shop-side cancel (e.g. barber off sick) that isn't a formal ShopClosure. Defaults to false,
         * so a normal cancel inside 24h withholds the refund. */
        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPatch("cancel/{bookingId}")]
        public async Task<IActionResult>CancelBooking(int bookingId, [FromQuery] bool refundAnyway = false)
        {
            try
            {
                // A barber may only cancel their own bookings; an admin may cancel any.
                if (!User.IsInRole("ADMIN"))
                {
                    var callerBarberId = await CallerBarberId();
                    var ownsBooking = await _context.Bookings.AnyAsync(b => b.Id == bookingId && b.BarberId == callerBarberId);
                    if (!ownsBooking) return StatusCode(403, new { message = "You can only cancel your own bookings" });
                }
                /* The refund + reminder-job-delete + cancel + email logic lives in BookingCanceller so
                 * the shop-closure flow (which cancels the same way) shares one code path. We just map
                 * its Outcome to the right HTTP response here.*/
                /* Why the booking is being cancelled is decided from the slot's CURRENT state, not from who
                 * clicked. Deactivating a barber deliberately leaves their confirmed bookings live and
                 * flagged ("reassign it or cancel it"), so following that instruction landed here and sent
                 * the customer the generic notice - never telling them their barber had gone - and, inside
                 * the 24h cutoff, withheld a refund for something the shop caused. The same applies to a
                 * booking left stranded by a closure or an hours change. Nothing wrong with the slot means
                 * it really is an ordinary admin cancellation. */
                var target = await _context.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId);
                if (target == null) return NotFound(new { message = "This booking was not found" });

                var blockingReason = target.Status == BookingStatus.COMPLETED
                    ? await BookingSlotGuard.ResolveBlockingReasonAsync(_context, target)
                    : null;

                /* An hours change takes BOTH halves to identify, and neither works alone.
                 *
                 * The note says the schedule flow stranded this booking - but nothing ever withdraws a note,
                 * so it may describe hours that have since been put back, and acting on it alone would tell a
                 * customer their barber doesn't work at that time when he does (and hand back money the 24h
                 * policy says they don't get). The live fit check says whether it's outside the hours right
                 * now - but staff book outside a barber's hours on purpose, so that alone would blame the shop
                 * for an appointment it deliberately made late.
                 *
                 * Together they're exact: flagged by the schedule flow AND still outside the hours. That
                 * leaves every note in place, identically for all three reasons, and still gets the refund
                 * right - which is why nothing here tries to tidy the note up afterwards (see BookingReview).
                 *
                 * Last, so a live problem always wins: a booking flagged over the hours whose barber has since
                 * been deactivated is a departure, not a schedule change, and the customer needs to be told to
                 * rebook with someone else rather than to pick another time. */
                if (blockingReason == null && target.Status == BookingStatus.COMPLETED
                    && target.NeedsReview
                    && target.ReviewReason?.Contains(ReviewMarkers.OutsideHours) == true
                    && !await BookingSlotGuard.FitsBarbersHoursAsync(_context, target))
                    blockingReason = CancellationReason.ScheduleChange;

                var outcome = await BookingCanceller.CancelAsync(_context, bookingId, dueToClosure: false,
                    forceRefund: refundAnyway,
                    reason: blockingReason ?? CancellationReason.AdminCancelled,
                    // Shop-initiated: full refund regardless of the customer-only 24h penalty.
                    shopInitiated: blockingReason != null);
                // A flagged booking stays flagged through a cancellation - see the note in UpdateBooking.
                return outcome switch
                {
                    BookingCanceller.Outcome.NotFound => NotFound(new { message = "This booking was not found" }),
                    BookingCanceller.Outcome.AlreadyCancelled => BadRequest(new { message = "Booking is already cancelled" }),
                    BookingCanceller.Outcome.Pending => BadRequest(new { message = "Cannot cancel a pending booking" }),
                    BookingCanceller.Outcome.RefundFailed => StatusCode(502, new { message = "Could not process the refund with Stripe. The booking was not cancelled - please try again." }),
                    BookingCanceller.Outcome.CancelledNoRefund => Ok(new { message = "Booking cancelled. No refund was issued as it is within 24 hours of the appointment." }),
                    _ => Ok(new { message = "Booking cancelled successfully." })
                };
            }
            catch(DbUpdateException ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new {message =  "An error occurred while updating the database"});
            }/* catches EF Core specific failures like constraint violations,
              * connection drops during save*/
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new {message =  "An unexpected error occurred"});
            }
        }
        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPatch("update-booking/{bookingId}")]
        public async Task<IActionResult> UpdateBooking([FromBody] UpdateBookingViewModel request, int bookingId)
        {
            try
            {
                var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId);
                if (booking == null) return NotFound(new {message = "Booking not found"});
                /* A barber may only edit a booking that is currently their own, and may only move it around
                 * their own day - never onto a colleague. Reassignment is an admin action because it puts
                 * work on someone else's schedule, which is the owner's call, and because letting a barber
                 * do it here would have contradicted CreatePendingBookings, where a barber creating a
                 * booking is pinned to their own chair and can't give a colleague new work either.
                 * Admins are unrestricted. */
                if (!User.IsInRole("ADMIN"))
                {
                    var callerBarberId = await CallerBarberId();
                    if (booking.BarberId != callerBarberId)
                        return StatusCode(403, new { message = "You can only edit your own bookings" });
                    if (request.BarberId != null && request.BarberId != callerBarberId)
                        return StatusCode(403, new { message = "Only an admin can move a booking to another barber" });
                }
                if (booking.Status != BookingStatus.COMPLETED) return BadRequest(new { message = "Only confirmed bookings can be updated" });


                int barberId = request.BarberId ?? booking.BarberId;
                DateTime startDateTime = request.StartDateTime ?? booking.StartDateTime;
                if(request.BarberId != null)
                {
                    var barber = await _context.Barbers.FirstOrDefaultAsync(b => b.Id == request.BarberId);
                    if (barber == null) return NotFound(new {message =  "Barber not found"});
                    if (!barber.isActive) return BadRequest(new {message =  "This barber is no longer active"});
                }
                /* Reschedule is staff-only (ADMIN/BARBER), so the 90-min lead-time buffer and 60-day horizon
                 * that ValidateBookingTime enforces on customers don't apply here - staff can move a booking
                 * to any future slot. We only guard against moving it into the past. */
                if(request.StartDateTime.HasValue && request.StartDateTime.Value < ShopClock.Now)
                {
                    return BadRequest(new { message = "Cannot book slots in the past" });
                }
                var appointmentDate = DateOnly.FromDateTime(startDateTime);
                var appointmentTime = TimeOnly.FromDateTime(startDateTime);
                var endDateTime = startDateTime.AddMinutes(booking.DurationMin);
                var endTime = TimeOnly.FromDateTime(endDateTime);
                var settings = await _context.ShopSettings
                    .Select(s => new { s.BufferMin, s.GraceMinutesAfterClose }).FirstAsync();

                /* The whole appointment must land inside one of the target barber's shifts for that date
                 * (grace minutes past the day's last shift allowed), same rule and same resolver the
                 * customer create path, the cash confirmation and the webhook guard already use.
                 *
                 * Staff used to be exempt here. That exemption undermined the schedule worklist: a booking
                 * flagged "the barber's hours changed and this now falls outside their schedule" could be
                 * rescheduled into another out-of-hours slot and look dealt with while still being exactly
                 * the problem it was flagged for. It matters twice over on a reassignment, where `barberId`
                 * is the NEW barber - a 7pm booking must not survive being moved to someone who finishes
                 * at 5. Fails closed: a barber with no schedule version covering the date is unbookable. */
                var scheduleVersions = await _context.BarberSchedules
                    .Include(s => s.Shifts)
                    .Where(s => s.BarberId == barberId
                                && s.EffectiveFrom <= appointmentDate
                                && (s.EffectiveTo == null || s.EffectiveTo >= appointmentDate))
                    .ToListAsync();
                if (!ScheduleResolver.FitsWithinAShift(scheduleVersions, appointmentDate, appointmentTime, endTime, settings.GraceMinutesAfterClose))
                {
                    var barberName = await _context.Barbers.Where(b => b.Id == barberId)
                        .Select(b => b.User.Name + " " + b.User.Surname).FirstOrDefaultAsync();
                    var refusal = OutsideHoursRefusal(request.ConfirmOutsideHours, barberName);
                    if (refusal != null) return refusal;
                }

                var closureDate = await _context.ShopClosures.FirstOrDefaultAsync(s =>
                s.IsActive == true && (s.BarberId == null || s.BarberId == barberId) && ((
                s.EndDate == null && s.StartDate == appointmentDate
                ) || (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) &&
                (s.IsFullDay || (s.StartTime < endTime && s.EndTime > appointmentTime))
                );
                if (closureDate != null) return BadRequest(new { message = "The chosen slot falls on an unavailable date" });

                // Must also honour the between-booking gap (see ShopSettings.BufferMin).
                var buffer = settings.BufferMin;
                var overlap = await _context.Bookings.AnyAsync(b =>
                b.Id != bookingId &&//exclude current booking
                b.BarberId == barberId &&
                b.Status != BookingStatus.CANCELLED &&
                b.StartDateTime < endDateTime.AddMinutes(buffer) && b.StartDateTime.AddMinutes(b.DurationMin + buffer) > startDateTime
                );
                if (overlap) return BadRequest(new { message = "The chosen slot overlaps with an existing booking" });

                var oldStartDateTime = booking.StartDateTime;

                booking.StartDateTime = startDateTime;
                booking.BarberId = barberId;

                /* NeedsReview is deliberately NOT cleared here (nor in CancelBooking). The flag means "a
                 * human still owes someone something", and only some of the nine things that raise it are
                 * settled by editing the booking - a failed cancellation email or a refund Stripe refused
                 * survives any amount of rescheduling. Rather than have the flag sometimes clear itself and
                 * sometimes not, which leaves staff unable to tell which rows they still have to work,
                 * mark-reviewed is the ONLY way out of the worklist. The frontend marks the row the admin
                 * just touched so the one they need to clear is easy to spot.
                 *
                 * No exceptions, including the hours note - which the app COULD disprove here, since this
                 * booking has just been moved somewhere that fits. Withdrawing it would make the hours the
                 * only reason that ever cleaned up after itself, and an admin who watched that happen would
                 * fairly expect the same after reviving a barber or deleting a closure, neither of which can
                 * do it. See the note in BookingReview for the full reasoning and what it costs. */

                var timeChanged = request.StartDateTime.HasValue;
                if (timeChanged)
                {
                    //cancel existing reminder if any
                    if (!string.IsNullOrWhiteSpace(booking.ReminderJobId))
                    {
                        BackgroundJob.Delete(booking.ReminderJobId);
                        booking.ReminderJobId = null;
                        booking.ReminderSentAt = null;
                    }
                    //schedule new reminder if the booking has a contact email and new time is >2h away
                    if (!string.IsNullOrWhiteSpace(booking.ContactEmail))
                    {
                        var reminderTime = startDateTime.AddHours(-2);
                        var delay = ShopClock.ToUtc(reminderTime) - DateTime.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            var jobId = BackgroundJob.Schedule<IEmailService>(service => service.sendBookingReminderEmailAsync(bookingId), delay);
                            booking.ReminderJobId = jobId;
                        }

                        BackgroundJob.Enqueue<IEmailService>(service => service.sendBookingRescheduledEmailAsync(bookingId, oldStartDateTime));
                    }
                    
                }
                await _context.SaveChangesAsync();
                /* you might be thinking that you have to set the booking record
                 * inside the right barber record so that the original booking will be 
                 cancelled and the new booking is reflected by replacing the barber and adding 
                the booking to the new barber however Bookings in the barber class is only
                a virtual class.
                
                 Before Update:
                Id    BarberId     StartDateTime    Status   
                12      3          2026-04-01 10:00 COMPLETED

                After Update:
                Id    BarberId     StartDateTime    Status   
                12      7          2026-04-01 10:00 COMPLETED

                The barbers table is completely untouched. Only the barberId foreign 
                key column on the booking row changes. Barber 3 doesnt "lose" a record
                and Barber 7 doesnt "gain" one- the relationship is just a number pointing
                to a row in the Barbers table
                */
                return Ok(new
                {
                    booking.Id,
                    booking.StartDateTime,
                    booking.Status,
                    booking.BarberId,
                    booking.DurationMin
                }); 
            }
            catch(DbUpdateException ex)
            {
                Console.WriteLine($"Could not update this booking, {ex.Message}");
                return StatusCode(500, new {message =   "Database Update error"});
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return StatusCode(500, new {message = "Unexpected Server error occurred"});
            }
        }
        private bool isValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return false;
            }
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch(Exception ex)
            {
                return false;
            }
        }
    }
}

