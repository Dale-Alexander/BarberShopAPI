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
using PhoneNumbers;
using SixLabors.ImageSharp;
using Stripe;
namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public BookingsController(BarberShopContext context)
        {
            _context = context;
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
            if (model.StartDateTime < DateTime.Now) return BadRequest(new { message = "Cannot book slots in the past" });
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

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var overlap = await _context.Bookings.AnyAsync(b =>
                b.BarberId == model.BarberId &&
                b.Status != BookingStatus.CANCELLED &&
                b.StartDateTime < endDateTime &&
                b.StartDateTime.AddMinutes(b.DurationMin) > model.StartDateTime);
                if (overlap) return BadRequest(new { message = "The chosen slot overlaps with an existing booking" });


                if (!IsValidName(model.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(model.Phone))
                    return BadRequest(new { message = "Invalid phone number" });

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == model.Phone);
                if (user == null)
                {
                    var parts = model.FullName?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    user = new User
                    {
                        Name = parts.Length > 0 ? parts[0] : "",
                        Surname = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "",
                        Phone = model.Phone
                    };
                    _context.Users.Add(user);
                    await _context.SaveChangesAsync();
                }
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
                await transaction.RollbackAsync();
                return BadRequest(new { message = "This slot was just booked by someone else, please try again" });
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
            if (model.StartDateTime < DateTime.Now) return BadRequest(new { message = "Cannot book slots in the past" });
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
            var closureDate = await _context.ShopClosures.FirstOrDefaultAsync(s =>
            s.IsActive == true && (s.BarberId == null || s.BarberId == model.BarberId) && ((s.EndDate == null && s.StartDate == appointmentDate)||
            (s.EndDate != null && s.StartDate <= appointmentDate && s.EndDate >= appointmentDate)) && (s.IsFullDay || (s.StartTime <
            endTime && s.EndTime > appointmentTime))
            );
            if (closureDate != null) return BadRequest(new { message = "The chosen slot falls on an unavailable slot" });

            var overlap = await _context.Bookings.AnyAsync(b =>
            b.BarberId == model.BarberId &&
            b.Status != BookingStatus.CANCELLED &&
            b.StartDateTime < endDateTime &&
            b.StartDateTime.AddMinutes(b.DurationMin) > model.StartDateTime);
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
            await _context.SaveChangesAsync();
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
                        CustomerName = b.User.Name + " " + b.User.Surname,
                        BarberName = b.Barber.User.Name + " " + b.Barber.User.Surname,
                        ServiceNames = b.Services.Select(bs => bs.Service.Name).ToList(),
                        AmountPaid = b.Services.Sum(bs => bs.Service.Price),
                        Date = b.StartDateTime.ToString("dddd, MMMM d, yyyy"),
                        Time = b.StartDateTime.ToString("h:mm tt"),
                        PaymentMethod = b.Payment.Method.ToString(),
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
                var existingPayment = await _context.Payments.FirstOrDefaultAsync(p => p.BookingId == model.BookingId);
                if (existingPayment != null) return BadRequest(new { message = "A payment already exists for this booking" });

                if (!IsValidName(model.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(model.Phone))
                    return BadRequest(new { message = "Invalid phone number" });
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Phone == model.Phone);
                if (user == null)
                {
                    var parts = model.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    //the above splits by space and ignores extra spaces
                    string firstName = parts.Length > 0 ? parts[0] : "";
                    string lastName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                    user = new User
                    {
                        Name = firstName,
                        Surname = lastName,
                        Phone = model.Phone,
                    };
                    _context.Users.Add(user);
                    await _context.SaveChangesAsync();
                }
                booking.UserId = user.Id;
                booking.Status = BookingStatus.COMPLETED;
                var payment = new Payment
                {
                    BookingId = booking.Id,
                    Amount = model.Amount// might be null if accessedByAdmin is true because it might not be passed from the frontend
                };
                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
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
                return StatusCode(500, new { message = "An error occurred while processing the payment", ex.Message });
            }
        }
        [HttpPost("payment-intent")]
        public async Task<IActionResult> StartBookingCardFlow([FromBody] PaymentIntentViewModel request)
        {
            Console.WriteLine($"{request.BookingId}, {request.Amount}, {request.FullName}, {request.Phone}");
            try
            {

                if (!IsValidName(request.FullName))
                    return BadRequest(new { message = "Please enter a valid full name" });
                if (!IsValidPhoneNumber(request.Phone))
                    return BadRequest(new { message = "Invalid phone number" });
                var paymentIntentOptions = new PaymentIntentCreateOptions
                {
                    Amount = request.Amount,
                    Currency = "eur",
                    PaymentMethodTypes = new List<string> { "card" },
                    Metadata = new Dictionary<string, string> {                        
                            {"BookingId", request.BookingId.ToString() },
                            {"FullName", request.FullName },
                            {"Phone", request.Phone }
                        }
                };
                var service = new PaymentIntentService();
                var paymentIntent = await service.CreateAsync(paymentIntentOptions);
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

        [Authorize(Roles ="ADMIN,BARBER")]
        [HttpGet("barber-fetch/{barberId}")]
        public async Task<IActionResult> GetBarberBookings([FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? status,
            int barberId)
        {
            var from = fromDate?.ToLocalTime();
            var to = toDate?.ToLocalTime();
            var barber = await _context.Barbers.Where(b => b.Id == barberId && b.isActive == true)
                .Select(b => new { b.Id, b.User.Name, b.User.Surname, b.ImageUrl }).FirstOrDefaultAsync();
            if (barber == null) return NotFound(new { message = "Barber not found" });

            if (from.HasValue != to.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
            if (from.HasValue && to.HasValue && from.Value > to.Value) return BadRequest(new { message = "From Date cannot be after To Date" });

            var query = _context.Bookings.Where(b => b.BarberId == barberId).AsQueryable();
            if (status == "CANCELLED")
                query = query.Where(b => b.Status == BookingStatus.CANCELLED);
            else
                query = query.Where(b => b.Status == BookingStatus.COMPLETED); // default

            if (status == "PAID")
                query = query.Where(b => b.Payment.Status == PaymentStatus.COMPLETED);
            else if (status == "PENDING")
                query = query.Where(b => b.Payment.Status == PaymentStatus.PENDING);
            if (from.HasValue && to.HasValue)
            {
                query = query.Where(b =>
                    b.StartDateTime >= from.Value &&
                    b.StartDateTime <= to.Value
                );
            }
            var bookings = await query.Select(b => new
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
                bookings
            });
        }

        [Authorize(Roles = "ADMIN")]
        [HttpGet("admin-fetch")]
        public async Task<IActionResult> GetBookingsForAdmin(
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? status)
        {
            try
            {
                var from = fromDate?.ToLocalTime();
                var to = toDate?.ToLocalTime();
                Console.WriteLine($"Status is {status}, FromDate is{from}, toDate is {to}");
                if (from.HasValue != to.HasValue) return BadRequest(new { message = "Both From Date and To Date must be provided or left empty" });
                if (from.HasValue && to.HasValue && from.Value > to.Value) return BadRequest(new { message = "From Date cannot be after To Date" });
                var query = _context.Bookings.AsQueryable();

                // Default to COMPLETED unless bookingStatus is explicitly passed
                if (status == "CANCELLED")
                    query = query.Where(b => b.Status == BookingStatus.CANCELLED);
                else
                    query = query.Where(b => b.Status == BookingStatus.COMPLETED); // default

                if (status == "PAID")
                    query = query.Where(b => b.Payment.Status == PaymentStatus.COMPLETED);
                else if (status == "PENDING")
                    query = query.Where(b => b.Payment.Status == PaymentStatus.PENDING);

                if (from.HasValue && to.HasValue)
                {
                    query = query.Where(b =>
                        b.StartDateTime >= from.Value &&
                        b.StartDateTime <= to.Value
                    );
                }

                var bookings = await query
                    .Select(b => new BookingsAdminViewModel
                    {
                        Id = b.Id,
                        FirstName = b.User.Name,
                        LastName = b.User.Surname,
                        StartDateTime = b.StartDateTime,
                        Amount = b.Payment.Amount,
                        PaymentMethod = b.Payment.Method.ToString(),//without .ToString the frontend would receive numbers like 0 or 1
                        PaymentStatus = b.Payment.Status.ToString()
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
                return Ok(bookings);
            }
            catch(Exception ex)
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
                if (booking.StartDateTime <= DateTime.Now) return BadRequest(new {message = "Cannot edit a booking that has passed"});

                return Ok(new EditBookingViewModel
                {
                    BarberId = booking.BarberId,
                    StartDateTime = booking.StartDateTime,
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
        [Authorize(Roles = "ADMIN,BARBER")]
        [HttpPatch("cancel/{bookingId}")]
        public async Task<IActionResult>CancelBooking(int bookingId)
        {
            try
            {
                var booking = await _context.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId);
                /* You might be wondering why dont we do .Where() and .Select to return the id and status instead of the entire booking.
                 * The reason is that when you do .Select(b => new { b.Id, b.Status }), you are 
                 projecting the result into an anonymous object meaning that EF Core just
                returns  aplain C# object - it has no idea it came from the database. It is not 
                tracking it at all so when you change the status EF Core doesnt see it and 
                SaveChangesAsync has nothing to save. EF Core's change tracker only tracks
                full entities*/

                if (booking == null) return NotFound(new { message = "This booking was not found" });
                if (booking.Status == BookingStatus.CANCELLED) return BadRequest(new { message = "Booking is already cancelled" });
                if (booking.Status == BookingStatus.PENDING) return BadRequest(new {message =  "Cannot cancel a pending booking"});
                booking.Status = BookingStatus.CANCELLED;
                await _context.SaveChangesAsync();
                return Ok($"Booking with id {bookingId} was cancelled succefully");
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
                if(request.StartDateTime != null && request.StartDateTime < DateTime.Now)
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

                var overlap = await _context.Bookings.AnyAsync(b =>
                b.Id != bookingId &&//exclude current booking
                b.BarberId == barberId &&
                b.Status != BookingStatus.CANCELLED &&
                b.StartDateTime < endDateTime && b.StartDateTime.AddMinutes(b.DurationMin) > startDateTime
                );
                if (overlap) return BadRequest(new { message = "The chosen slot overlaps with an existing booking" });
                booking.StartDateTime = startDateTime;
                booking.BarberId = barberId;
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
        }
    }

