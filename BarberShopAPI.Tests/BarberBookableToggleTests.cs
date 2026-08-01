using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* PATCH /api/barbers/{id}/accepting-bookings - closing a barber to NEW customer bookings without
     * deactivating them ("working their notice").
     *
     * The whole point of the flag is that it is NARROWER than isActive, so most of what's asserted here is
     * what it deliberately does NOT do:
     *   - existing bookings are untouched (no cancel, no refund, no email) - that's DeleteBarber's job;
     *   - the barber keeps their login and dashboard access, because isActive never changes;
     *   - staff can still book and reassign onto them, so the shop can honour a phone request.
     * Only the two customer-facing queries read it: the public roster and create-pending. */
    public class BarberBookableToggleTests : IntegrationTestBase
    {
        public BarberBookableToggleTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        [Fact]
        public async Task Public_roster_hides_a_barber_closed_to_new_bookings_and_shows_them_again()
        {
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
            }

            // Positive control first: with the flag on they're in the customer's picker.
            var before = await ReadJson(await Client.GetAsync("/api/Barbers/barbers-with-bookings"));
            Assert.Contains(before.GetProperty("barbers").EnumerateArray(),
                b => b.GetProperty("barberId").GetInt32() == barberId);

            using (var db = NewDb())
            {
                db.Barbers.Single(b => b.Id == barberId).AcceptsNewBookings = false;
                db.SaveChanges();
            }

            var after = await ReadJson(await Client.GetAsync("/api/Barbers/barbers-with-bookings"));
            Assert.DoesNotContain(after.GetProperty("barbers").EnumerateArray(),
                b => b.GetProperty("barberId").GetInt32() == barberId);
        }

        /* The staff roster keeps them. The flag means "can a CUSTOMER pick them?" and nothing else, so
         * reading the customer list on the staff booking page stranded a barber working their notice:
         * they vanished from their OWN edit page and couldn't move the appointments they were still
         * working through - the exact situation the toggle exists to support. */
        [Fact]
        public async Task Staff_roster_keeps_a_barber_closed_to_new_bookings()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barber.AcceptsNewBookings = false;
                db.SaveChanges();
                barberId = barber.Id;
            }

            var staff = await ReadJson(await Client.GetAsync("/api/Barbers/barbers-with-bookings?includeUnbookable=true"));
            Assert.Contains(staff.GetProperty("barbers").EnumerateArray(),
                b => b.GetProperty("barberId").GetInt32() == barberId);
        }

        /* The endpoint is anonymous, so the flag can't be a self-service switch into the staff view -
         * otherwise any customer could put a barber back in their own picker by editing the URL. */
        [Fact]
        public async Task An_anonymous_caller_passing_the_staff_flag_still_gets_the_customer_roster()
        {
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barber.AcceptsNewBookings = false;
                db.SaveChanges();
                barberId = barber.Id;
            }

            // No auth cookie at all - this test never calls Authenticate.
            var anon = await ReadJson(await Client.GetAsync("/api/Barbers/barbers-with-bookings?includeUnbookable=true"));
            Assert.DoesNotContain(anon.GetProperty("barbers").EnumerateArray(),
                b => b.GetProperty("barberId").GetInt32() == barberId);
        }

        /* A deactivated barber is out of BOTH rosters. includeUnbookable relaxes AcceptsNewBookings only -
         * it must not become a way to book someone who has left the shop. */
        [Fact]
        public async Task The_staff_roster_still_excludes_a_deactivated_barber()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber(isActive: false);
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
            }

            var staff = await ReadJson(await Client.GetAsync("/api/Barbers/barbers-with-bookings?includeUnbookable=true"));
            Assert.DoesNotContain(staff.GetProperty("barbers").EnumerateArray(),
                b => b.GetProperty("barberId").GetInt32() == barberId);
        }

        [Fact]
        public async Task Customer_checkout_is_refused_once_the_barber_is_closed_to_new_bookings()
        {
            int barberId, serviceId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                serviceId = db.AddService(durationMin: 30).Id;
            }

            // Positive control: the identical request succeeds while they're open, so the 400 below can
            // only be the flag and not some unrelated validation failure.
            var allowed = await Client.PostAsync("/api/Bookings/create-pending", Body(new
            {
                ServicesIds = new[] { serviceId },
                StartDateTime = TestData.FutureAt(14, 11),
                BarberId = barberId
            }));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            using (var db = NewDb())
            {
                db.Barbers.Single(b => b.Id == barberId).AcceptsNewBookings = false;
                db.SaveChanges();
            }

            var refused = await Client.PostAsync("/api/Bookings/create-pending", Body(new
            {
                ServicesIds = new[] { serviceId },
                StartDateTime = TestData.FutureAt(14, 15),
                BarberId = barberId
            }));

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("Barber was not found", (await ReadJson(refused)).GetProperty("message").GetString());
        }

        [Fact]
        public async Task Staff_can_still_book_a_barber_who_is_closed_to_new_bookings()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber(acceptsNewBookings: false);
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
            }

            // A regular rings up asking for their barber before he leaves - the shop can still take it.
            var response = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(new
            {
                StartDateTime = TestData.FutureAt(14, 11),
                BarberId = barberId,
                DefaultDurationMin = 30,
                Phone = "+35679123456",
                FullName = "Walk In"
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var assertDb = NewDb();
            Assert.True(await assertDb.Bookings.AnyAsync(b => b.BarberId == barberId));
        }

        [Fact]
        public async Task Toggling_leaves_existing_bookings_and_the_barbers_session_completely_alone()
        {
            AuthenticateAsAdmin();
            int barberId, barberUserId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                barberUserId = barber.UserId;
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
            }

            var response = await Client.PatchAsync($"/api/Barbers/{barberId}/accepting-bookings",
                Body(new { AcceptsNewBookings = false }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False((await ReadJson(response)).GetProperty("acceptsNewBookings").GetBoolean());

            using (var assertDb = NewDb())
            {
                var barber = await assertDb.Barbers.SingleAsync(b => b.Id == barberId);
                Assert.False(barber.AcceptsNewBookings);
                // Still staff: this is the whole difference from DeleteBarber.
                Assert.True(barber.isActive);

                // No TokenVersion bump, so their existing session keeps working - they still need the
                // dashboard to serve the bookings they're honouring.
                Assert.Equal(0, (await assertDb.Users.SingleAsync(u => u.Id == barberUserId)).TokenVersion);

                var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
                Assert.Equal(BookingStatus.COMPLETED, booking.Status);
                Assert.Equal(CancellationReason.None, booking.CancellationReason);
                Assert.False(booking.NeedsReview);
            }
            // Nothing to tell the customer about - their appointment is still on.
            Assert.Empty(Factory.EnqueuedEmailJobs());

            // And it's reversible.
            var reopen = await Client.PatchAsync($"/api/Barbers/{barberId}/accepting-bookings",
                Body(new { AcceptsNewBookings = true }));
            Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
            using var reopenDb = NewDb();
            Assert.True((await reopenDb.Barbers.SingleAsync(b => b.Id == barberId)).AcceptsNewBookings);
        }

        [Fact]
        public async Task A_barber_cannot_close_themselves_to_new_bookings()
        {
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                Client.Authenticate(barber.UserId, Role.BARBER, tokenVersion: 0);
            }

            var response = await Client.PatchAsync($"/api/Barbers/{barberId}/accepting-bookings",
                Body(new { AcceptsNewBookings = false }));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var assertDb = NewDb();
            Assert.True((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).AcceptsNewBookings);
        }

        [Fact]
        public async Task Toggling_a_deactivated_barber_is_a_404_rather_than_a_silent_no_op()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                barberId = db.AddBarber(isActive: false).Id;
            }

            var response = await Client.PatchAsync($"/api/Barbers/{barberId}/accepting-bookings",
                Body(new { AcceptsNewBookings = true }));

            // They're already unbookable through the isActive half of the filter, so "re-opening" them
            // would look like it worked and change nothing.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task The_deactivate_warning_points_the_admin_at_the_softer_option()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}");

            // This 409 is the only place the toggle is discoverable - nothing else in the UI mentions it,
            // so an admin about to take the login off a barber who could still serve these appointments
            // themselves would never learn there was another way.
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("close them to new bookings instead",
                (await ReadJson(response)).GetProperty("message").GetString());
        }

        [Fact]
        public async Task Reviving_a_barber_reopens_them_to_new_bookings()
        {
            AuthenticateAsAdmin();
            int barberId;
            string barberEmail;
            using (var db = NewDb())
            {
                // The state a barber ends up in when they're closed to new bookings while working their
                // notice and then deactivated for good: BOTH flags off.
                var barber = db.AddBarber(isActive: false, acceptsNewBookings: false);
                barberId = barber.Id;
                barberEmail = db.Users.Single(u => u.Id == barber.UserId).Email!;
            }

            // Reactivation routes through CreateBarber's revive-by-email branch (see TeamMembers.jsx).
            using var form = new MultipartFormDataContent
            {
                { new StringContent("Revived Barber"), "FullName" },
                { new StringContent(barberEmail), "Email" },
                { new StringContent("not-a-real-password"), "Password" }
            };
            var response = await Client.PostAsync("/api/Barbers/create-barber", form);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await ReadJson(response)).GetProperty("acceptsNewBookings").GetBoolean());

            using var assertDb = NewDb();
            var revived = await assertDb.Barbers.SingleAsync(b => b.Id == barberId);
            Assert.True(revived.isActive);
            // Without the reset they'd come back live on the team screen but invisible in the customer
            // picker, with nothing on the card explaining why.
            Assert.True(revived.AcceptsNewBookings);
        }

        [Fact]
        public async Task An_empty_body_is_rejected_rather_than_defaulting_to_closed()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                barberId = db.AddBarber().Id;
            }

            var response = await Client.PatchAsync($"/api/Barbers/{barberId}/accepting-bookings", Body(new { }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var assertDb = NewDb();
            // A plain `bool` would have bound to false here and quietly closed the barber.
            Assert.True((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).AcceptsNewBookings);
        }
    }
}
