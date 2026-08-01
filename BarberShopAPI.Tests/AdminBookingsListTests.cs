using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* GET /api/bookings/admin-fetch - the row shape the admin bookings table renders.
     *
     * BarberName and Phone are on the row rather than baked into ReviewReason. Ten places write that free
     * text and most only say "the barber", which is ambiguous in a list spanning the whole shop; the phone
     * matters because a flagged row's instruction is often "call the customer". Both come from joins EF
     * folds into the projection, so a mistyped path fails at query time, not compile time - hence a test. */
    public class AdminBookingsListTests : IntegrationTestBase
    {
        public AdminBookingsListTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        [Fact]
        public async Task Each_row_carries_the_barbers_name_and_the_customers_phone()
        {
            string barberFirstName, customerPhone;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberFirstName = db.Users.Single(u => u.Id == barber.UserId).Name!;

                var customer = db.AddUser();
                customerPhone = customer.Phone!;
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED, customer: customer);
                // The default filter is "real bookings only" - Payment != null and COMPLETED - so an
                // unpaid row would never reach the table at all.
                db.AddCashPayment(booking.Id);
            }
            AuthenticateAsAdmin();

            var response = await Client.GetAsync("/api/bookings/admin-fetch");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var row = (await ReadJson(response)).GetProperty("bookings").EnumerateArray().Single();
            Assert.Equal(barberFirstName, row.GetProperty("barberName").GetString());
            Assert.Equal(customerPhone, row.GetProperty("phone").GetString());
        }

        [Fact]
        public async Task The_needs_review_worklist_carries_the_reason_alongside_the_barber_and_phone()
        {
            string barberFirstName, customerPhone;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberFirstName = db.Users.Single(u => u.Id == barber.UserId).Name!;

                var customer = db.AddUser();
                customerPhone = customer.Phone!;
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED, customer: customer);
                booking.NeedsReview = true;
                booking.ReviewReason = "The barber's working hours changed and this booking now falls outside their schedule.";
                db.SaveChanges();
            }
            AuthenticateAsAdmin();

            // needsReview bypasses the status/payment filters entirely - a flagged booking must surface
            // whatever state it's in, which is why this row needs no Payment.
            var response = await Client.GetAsync("/api/bookings/admin-fetch?needsReview=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var row = (await ReadJson(response)).GetProperty("bookings").EnumerateArray().Single();
            Assert.True(row.GetProperty("needsReview").GetBoolean());
            Assert.Contains("working hours changed", row.GetProperty("reviewReason").GetString());
            // The point of the whole change: "the barber" in the reason is now answerable from the row.
            Assert.Equal(barberFirstName, row.GetProperty("barberName").GetString());
            Assert.Equal(customerPhone, row.GetProperty("phone").GetString());
        }
    }
}
