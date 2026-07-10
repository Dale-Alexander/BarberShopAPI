using BarberShopAPI.Data;
using BarberShopAPI.Models.Enums;
using Microsoft.EntityFrameworkCore;

public class BookingExpiryJob
{
    private readonly BarberShopContext _context;

    public BookingExpiryJob(BarberShopContext context)
    {
        _context = context;
    }

    public async Task CancelExpiredBookingsAsync()
    {
        var expiredBookings = await _context.Bookings
            .Where(b => b.Status == BookingStatus.PENDING &&
                        b.CreatedAt <= DateTime.Now.AddMinutes(-15))
            .ToListAsync();

        if (!expiredBookings.Any()) return;

        foreach (var booking in expiredBookings)
        {
            booking.Status = BookingStatus.CANCELLED;
        }

        await _context.SaveChangesAsync();
        Console.WriteLine($"Cancelled {expiredBookings.Count} expired bookings");
    }
}