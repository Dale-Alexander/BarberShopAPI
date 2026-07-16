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
        private static readonly TimeSpan MinAdvanceBooking = TimeSpan.FromMinutes(90);
        private static readonly TimeSpan MaxAdvanceBooking = TimeSpan.FromDays(60);

        /* Shop working-hours window, kept in sync with the frontend TIME_SLOTS array in
         * BarberDateAndTime.jsx (09:00 first slot .. 17:30 last slot start). A booking is valid only
         * if it starts at/after open and ends at/before close + grace - checked against the REAL end
         * (start + duration), never the buffered end. graceMin lets the last client run past close
         * (0 = must finish by closing; from ShopSettings.GraceMinutesAfterClose). */
        private static readonly TimeOnly ShopOpen = new(9, 0);
        private static readonly TimeOnly ShopClose = new(17, 30);

        private static bool WithinWorkingHours(DateTime start, DateTime end, int graceMin) =>
            TimeOnly.FromDateTime(start) >= ShopOpen && TimeOnly.FromDateTime(end) <= ShopClose.AddMinutes(graceMin);

        private static (bool IsValid, string? Error) ValidateBookingTime(DateTime startDateTime)
        {
            DateTime now = ShopClock.Now;
            if (startDateTime < now) return (false, "Cannot book slots in the past");
            if (startDateTime < now.Add(MinAdvanceBooking)) return (false, $"Bookings must be made at least {MinAdvanceBooking.TotalMinutes} minutes in advance");
            if (startDateTime > now.Add(MaxAdvanceBooking)) return (false, $"Bookings cannot be made more than {MaxAdvanceBooking.TotalDays} days in advance");
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
            return true;
        }

        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPost("create-admin-booking")]
        public async Task<IActionResult> CreateAdminBookings([FromBody] AdminCreateBookingCreateViewModel model)
        {
            Console.WriteLine($"{model.StartDateTime}");
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
            var closureDate = await _context.ShopClosures.FirstOrDefaultAsync(s =>
            s.IsActive == true && (s.BarberId == null || s.BarberId == model.BarberId) && ((s.EndDate == null && s.StartDate == appointmentDate) ||
            (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) && (s.IsFullDay || (s.StartTime <
            endTime && s.EndTime > appointmentTime))
            );
            if (closureDate != null) return BadRequest(new { message = "The chosen slot falls on an unavailable slot" });

            // Staff booking is exempt from working hours (like the lead-time/horizon rules), but the
            // between-booking buffer still applies so staff can't wedge a client into another's gap.
            var buffer = await _context.ShopSettings.Select(s => s.BufferMin).FirstAsync();

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var overlap = await _context.Bookings.AnyAsync(b =>
                b.BarberId == model.BarberId &&
                b.Status != BookingStatus.CANCELLED &&
                b.StartDateTime < endDateTime.AddMinutes(buffer) &&
                b.StartDateTime.AddMinutes(b.DurationMin + buffer) > model.StartDateTime);
                if (overlap) return BadRequest(new { message = "The chosen slot overlaps with an existing booking" });


                if (!IsValidName(model.FullName))
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
                    // Returning customer (matched by phone): refresh the name so a corrected spelling -
                    // or a different person sharing the phone - is reflected on this booking instead of
                    // silently keeping the name from their first ever booking. FullName is already
                    // validated above, so these are never empty.
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
                    UserId = user.Id
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
            var barber = await _context.Barbers.Where(b => b.isActive == true && b.Id == model.BarberId).Select(b => new
            {
                b.Id, FullName = b.User.Name + " " + b.User.Surname
            }).FirstOrDefaultAsync();
            if (barber == null) return BadRequest(new { message = "Barber was not found" });
            var (isValid, error) = ValidateBookingTime(model.StartDateTime);
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
            // Load the shop settings once - used for the working-hours grace and the between-booking buffer.
            var settings = await _context.ShopSettings.FirstAsync();
            // Customer-facing path: the whole appointment must fall inside shop working hours
            // (allowing up to GraceMinutesAfterClose past closing).
            if (!WithinWorkingHours(model.StartDateTime, endDateTime, settings.GraceMinutesAfterClose))
                return BadRequest(new { message = "Outside shop working hours" });
            var appointmentDate = DateOnly.FromDateTime(model.StartDateTime);
            var appointmentTime = TimeOnly.FromDateTime(model.StartDateTime);
            var endTime = TimeOnly.FromDateTime(endDateTime);
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
                DurationMin = durationMinutes
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
        [HttpGet("alreadypaid/{bookingId}")]
        public async Task<IActionResult> GetAlreadyPaidBookingDetails(int bookingId)
        {
            try
            {
                var booking = await _context.Bookings
                    .Where(b => b.Id == bookingId)
                    .Select(b => new
                    {
                        b.Status,
                        // A booking cancelled while still PENDING never got a linked user or a payment,
                        // so User/Payment can be null here - keep these null-safe or the projection 500s.
                        CustomerName = b.User != null ? b.User.Name + " " + b.User.Surname : null,
                        BarberName = b.Barber.User.Name + " " + b.Barber.User.Surname,
                        ServiceNames = b.Services.Select(bs => bs.Service.Name).ToList(),
                        AmountPaid = b.Services.Sum(bs => bs.Service.Price),
                        Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                        Time = b.StartDateTime.ToString("h:mm tt"),
                        PaymentMethod = b.Payment != null ? b.Payment.Method.ToString() : null,
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
                var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.Id == model.BookingId);
                if (booking == null) return BadRequest(new { message = "Booking not found" });
                if (booking.Status != BookingStatus.PENDING) return BadRequest(new { message = "Only Pending Bookings can be confirmed" });

                // Same closure re-check as the webhook: a PENDING booking must never be confirmed to
                // COMPLETED on a slot a closure now covers. Unlike the card path, no money has moved and
                // the admin is here, so we just refuse and let them handle it - nothing to refund.
                var appointmentDate = DateOnly.FromDateTime(booking.StartDateTime);
                var appointmentTime = TimeOnly.FromDateTime(booking.StartDateTime);
                var endTime = TimeOnly.FromDateTime(booking.StartDateTime.AddMinutes(booking.DurationMin));
                var closure = await _context.ShopClosures.FirstOrDefaultAsync(s =>
                    s.IsActive == true && (s.BarberId == null || s.BarberId == booking.BarberId) &&
                    ((s.EndDate == null && s.StartDate == appointmentDate) ||
                     (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) &&
                    (s.IsFullDay || (s.StartTime < endTime && s.EndTime > appointmentTime)));

                if (closure != null) return BadRequest(new { message = "This slot now falls on a shop closure and can no longer be confirmed" });
                //The closure checks is for when the admin created a closure between PENDING and COMPLETED stage
                var existingPayment = await _context.Payments.FirstOrDefaultAsync(p => p.BookingId == model.BookingId);
                if (existingPayment != null) return BadRequest(new { message = "A payment already exists for this booking" });

                if (!IsValidName(model.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(model.Phone))
                    return BadRequest(new { message = "Invalid phone number" });
                if (!isValidEmail(model.Email))
                {
                    return BadRequest(new { message = "Invalid email address"});
                }
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
                    Amount = model.Amount
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
        public async Task<IActionResult> MarkCashPaymentAsPaid(int bookingId)
        {
            try
            {
                var payment = await _context.Payments.FirstOrDefaultAsync(p => p.BookingId == bookingId);
                if (payment == null) return NotFound(new { message = "Payment not found for this booking" });
                if (payment.Method != Models.Enums.PaymentMethod.CASH)
                    return BadRequest(new { message = "Only cash payments can be marked as paid this way" });
                if (payment.Status == PaymentStatus.COMPLETED)
                    return BadRequest(new { message = "This payment has already been marked as paid" });

                payment.Status = PaymentStatus.COMPLETED;
                payment.PaidAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = "Payment marked as paid",
                    paymentId = payment.Id,
                    paidAt = payment.PaidAt
                });
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
            Console.WriteLine($"{request.BookingId}, {request.Amount}, {request.FullName}, {request.Phone}");
            try
            {
                var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.Id == request.BookingId);
                if (booking == null) return BadRequest(new { message = "Booking not found" });
                if (booking.Status != BookingStatus.PENDING)
                    return BadRequest(new { message = "This booking can no longer be paid for" });

                if (!IsValidName(request.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(request.Phone))
                    return BadRequest(new { message = "Invalid phone number" });
                if (!isValidEmail(request.Email))
                    return BadRequest(new { message = "Invalid Email Address" });
                var paymentIntentOptions = new PaymentIntentCreateOptions
                {
                    Amount = request.Amount,
                    Currency = "eur",
                    PaymentMethodTypes = new List<string> { "card" },
                    Metadata = new Dictionary<string, string> {
                            {"BookingId", request.BookingId.ToString() },
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

        /* Shared filter for a single barber's bookings so the paginated list (barber-fetch) and the summary
         * tiles (barber-summary) always agree on which bookings they're describing. Mirrors the admin filter:
         * defaults to COMPLETED unless CANCELLED is asked for, with optional PAID/PENDING payment narrowing
         * and an optional date window. */
        private IQueryable<Booking> BuildBarberBookingsQuery(int barberId, DateTime? from, DateTime? to, string? status)
        {
            var query = _context.Bookings.Where(b => b.BarberId == barberId);
            if (status == "CANCELLED")
                query = query.Where(b => b.Status == BookingStatus.CANCELLED);
            else
                query = query.Where(b => b.Status == BookingStatus.COMPLETED); // default

            if (status == "PAID")
                query = query.Where(b => b.Payment.Status == PaymentStatus.COMPLETED);
            else if (status == "PENDING")
                query = query.Where(b => b.Payment.Status == PaymentStatus.PENDING);

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

        [Authorize(Roles ="ADMIN,BARBER")]
        [HttpGet("barber-fetch/{barberId}")]
        public async Task<IActionResult> GetBarberBookings([FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            int barberId = 0)
        {
            if (fromDate.HasValue != toDate.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
            if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value) return BadRequest(new { message = "From Date cannot be after To Date" });
            var (from, to) = ToMaltaDayWindow(fromDate, toDate);

            var barber = await _context.Barbers.Where(b => b.Id == barberId && b.isActive == true)
                .Select(b => new { b.Id, b.User.Name, b.User.Surname, b.ImageUrl }).FirstOrDefaultAsync();
            if (barber == null) return NotFound(new { message = "Barber not found" });

            (page, pageSize) = NormalisePaging(page, pageSize);
            var query = BuildBarberBookingsQuery(barberId, from, to, status);
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
                    Services = b.Services.Select(bs => new
                    {
                        ServiceName = bs.Service.Name,
                        bs.Service.Price
                    })
                }).ToListAsync();
            return Ok(new
            {
                barberName = barber.Name,
                barberSurname = barber.Surname,
                barberImageUrl = barber.ImageUrl,
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
            [FromQuery] string? status)
        {
            try
            {
                if (fromDate.HasValue != toDate.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
                if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value) return BadRequest(new { message = "From Date cannot be after To Date" });
                var (from, to) = ToMaltaDayWindow(fromDate, toDate);

                var query = BuildBarberBookingsQuery(barberId, from, to, status);
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
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            try
            {
                if (fromDate.HasValue != toDate.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
                if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value) return BadRequest(new { message = "From Date cannot be after To Date" });
                var (from, to) = ToMaltaDayWindow(fromDate, toDate);
                var query = _context.Bookings.AsQueryable();

                // NEEDS_REVIEW is a cross-status worklist (a flagged booking can be any status), so it
                // bypasses the status/payment defaulting below rather than layering on top of it.
                if (status == "NEEDS_REVIEW")
                {
                    query = query.Where(b => b.NeedsReview);
                }
                else
                {
                    // Default to COMPLETED unless bookingStatus is explicitly passed
                    if (status == "CANCELLED")
                        query = query.Where(b => b.Status == BookingStatus.CANCELLED);
                    else
                        query = query.Where(b => b.Status == BookingStatus.COMPLETED); // default

                    if (status == "PAID")
                        query = query.Where(b => b.Payment.Status == PaymentStatus.COMPLETED);
                    else if (status == "PENDING")
                        query = query.Where(b => b.Payment.Status == PaymentStatus.PENDING);
                }

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
                        Amount = b.Payment.Amount,
                        PaymentMethod = b.Payment.Method.ToString(),//without .ToString the frontend would receive numbers like 0 or 1
                        PaymentStatus = b.Payment.Status.ToString(),
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
         * so they now come from SQL. Everything is over COMPLETED bookings, and the month/day buckets read
         * StartDateTime directly (Malta wall-clock, same basis as ShopClock.Now). */
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

                var completed = _context.Bookings.Where(b => b.Status == BookingStatus.COMPLETED);

                // Stat cards: this month vs last month.
                var nowMalta = ShopClock.Now;
                var thisMonthStart = new DateTime(nowMalta.Year, nowMalta.Month, 1);
                var nextMonthStart = thisMonthStart.AddMonths(1);
                var lastMonthStart = thisMonthStart.AddMonths(-1);

                var thisMonthQuery = completed.Where(b => b.StartDateTime >= thisMonthStart && b.StartDateTime < nextMonthStart);
                var lastMonthQuery = completed.Where(b => b.StartDateTime >= lastMonthStart && b.StartDateTime < thisMonthStart);

                var thisMonthBookings = await thisMonthQuery.CountAsync();
                var thisMonthRevenue = await thisMonthQuery.SumAsync(b => (decimal?)b.Payment.Amount) ?? 0m;
                var lastMonthBookings = await lastMonthQuery.CountAsync();
                var lastMonthRevenue = await lastMonthQuery.SumAsync(b => (decimal?)b.Payment.Amount) ?? 0m;

                var availableYears = await completed.Select(b => b.StartDateTime.Year).Distinct().OrderBy(y => y).ToListAsync();

                // Monthly series for the requested year (default: current year).
                var targetYear = year ?? nowMalta.Year;
                var monthlyRaw = await completed
                    .Where(b => b.StartDateTime.Year == targetYear)
                    .GroupBy(b => b.StartDateTime.Month)
                    .Select(g => new { Month = g.Key, Count = g.Count(), Revenue = g.Sum(b => (decimal?)b.Payment.Amount) ?? 0m })
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
                    var dailyRaw = await completed
                        .Where(b => b.StartDateTime.Year == targetYear && b.StartDateTime.Month == month.Value)
                        .GroupBy(b => b.StartDateTime.Day)
                        .Select(g => new { Day = g.Key, Count = g.Count(), Revenue = g.Sum(b => (decimal?)b.Payment.Amount) ?? 0m })
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
        public async Task<IActionResult> GetBookingDetailsForCheckout(int bookingId)
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
                    .Where(b => b.Id == bookingId)
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
                /* The refund + reminder-job-delete + cancel + email logic lives in BookingCanceller so
                 * the shop-closure flow (which cancels the same way) shares one code path. We just map
                 * its Outcome to the right HTTP response here.*/
                var outcome = await BookingCanceller.CancelAsync(_context, bookingId, dueToClosure: false, forceRefund: refundAnyway);
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

                var closureDate = await _context.ShopClosures.FirstOrDefaultAsync(s =>
                s.IsActive == true && (s.BarberId == null || s.BarberId == barberId) && ((
                s.EndDate == null && s.StartDate == appointmentDate
                ) || (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) &&
                (s.IsFullDay || (s.StartTime < endTime && s.EndTime > appointmentTime))
                );
                if (closureDate != null) return BadRequest(new { message = "The chosen slot falls on an unavailable date" });

                // Staff reschedule is exempt from working hours (as with the create paths) but must
                // still honour the between-booking buffer (see ShopSettings.BufferMin).
                var buffer = await _context.ShopSettings.Select(s => s.BufferMin).FirstAsync();
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

