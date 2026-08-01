using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* DELETE /api/barbers/delete/{id} - deactivating a barber, and what happens to the work they leave.
     *
     * The invariant here is that firing someone does NOT decide the fate of their customers' appointments.
     * Confirmed bookings survive untouched - same barber, same time, same money - and land in the
     * NeedsReview worklist for the admin to reassign or cancel. Only PENDING bookings (payment still in
     * flight, never confirmed to anyone) are cancelled outright, via BookingConflictCanceller.
     *
     * So the two things worth guarding are that a confirmed booking is NOT cancelled, refunded or emailed
     * by this endpoint, and that it can't quietly fall off the worklist. */
    public class BarberDeactivationTests : IntegrationTestBase
    {
        public BarberDeactivationTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        [Fact]
        public async Task Upcoming_bookings_are_reported_first_and_the_barber_stays_active()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            var conflicts = payload.GetProperty("conflicts").EnumerateArray().ToList();
            Assert.Single(conflicts);
            Assert.Equal(bookingId, conflicts[0].GetProperty("id").GetInt32());
            // Confirmed, so it survives - the modal badges it "kept & flagged", not "will be cancelled".
            Assert.False(conflicts[0].GetProperty("willBeCancelled").GetBoolean());

            using var assertDb = NewDb();
            // Nothing happened yet - the admin can still back out.
            Assert.True((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);
            Assert.Equal(BookingStatus.COMPLETED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Confirming_deactivates_the_barber_revokes_their_session_and_keeps_the_booking()
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

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadJson(response);
            Assert.Equal(1, body.GetProperty("flaggedForReview").GetInt32());
            Assert.Equal(0, body.GetProperty("cancelled").GetInt32());

            using var assertDb = NewDb();
            Assert.False((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);

            // TokenVersion bump locks the deactivated barber out of their dashboard immediately, rather
            // than leaving their still-valid JWT working until it expires. This is the half of the old
            // behaviour that must survive: keeping the bookings must not mean keeping the login.
            Assert.Equal(1, (await assertDb.Users.SingleAsync(u => u.Id == barberUserId)).TokenVersion);

            /* The appointment is untouched - still confirmed, still on this barber, still uncancelled.
             * It's the admin's call whether this customer gets moved or refunded, so nothing about the
             * booking may change here except that it now demands attention. */
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.Equal(CancellationReason.None, booking2.CancellationReason);
            Assert.Equal(barberId, booking2.BarberId);
            Assert.True(booking2.NeedsReview);
            Assert.Contains("still live", booking2.ReviewReason);
            Assert.Contains("has NOT been told", booking2.ReviewReason);

            // No cancellation notice: as far as the customer knows the appointment stands, and it does.
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_phone_only_booking_carries_the_number_into_the_worklist()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            string phone;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                // An admin-created walk-in: phone number only, so whatever the admin decides later can only
                // be communicated by ringing them. The note has to carry the number.
                var customer = db.AddUser();
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED,
                    customer: customer, contactEmail: null);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
                phone = customer.Phone!;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.True(booking2.NeedsReview);
            Assert.Contains(phone, booking2.ReviewReason);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_pending_booking_is_cancelled_as_barber_unavailable_without_an_email()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.PENDING).Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadJson(response);
            Assert.Equal(1, body.GetProperty("cancelled").GetInt32());
            Assert.Equal(0, body.GetProperty("flaggedForReview").GetInt32());

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // Never confirmed to anyone, so there is nothing to honour and nothing to reassign - it goes.
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.BarberUnavailable, booking.CancellationReason);
            Assert.False(booking.NeedsReview);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Past_bookings_are_left_as_history()
        {
            AuthenticateAsAdmin();
            int barberId, pastBookingId, futureBookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                pastBookingId = db.AddBooking(barber.Id, TestData.FutureAt(-3, 16), BookingStatus.COMPLETED).Id;
                futureBookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            // Asserting both in one run: the future one proves the sweep ran at all, so the past one being
            // unflagged is a real exclusion rather than a no-op. A haircut that already happened isn't work
            // anyone can still do something about, so it must not clutter the worklist.
            Assert.True((await assertDb.Bookings.SingleAsync(b => b.Id == futureBookingId)).NeedsReview);
            var past = await assertDb.Bookings.SingleAsync(b => b.Id == pastBookingId);
            Assert.Equal(BookingStatus.COMPLETED, past.Status);
            Assert.Equal(CancellationReason.None, past.CancellationReason);
            Assert.False(past.NeedsReview);
        }

        [Fact]
        public async Task An_already_inactive_barber_cannot_be_deactivated_again()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
                barberId = db.AddBarber(isActive: false).Id;

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("already inactive", (await ReadJson(response)).GetProperty("message").GetString());
        }

        [Fact]
        public async Task Deactivating_a_barber_with_no_upcoming_bookings_needs_no_confirmation()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);
        }

        /* -----------------------------------------------------------------------------------------------
         * mark-reviewed is the ONLY way out of the worklist.
         *
         * Nine places raise NeedsReview and they don't mean the same thing: some are settled by dealing
         * with the booking (barber left, hours changed), but others - a cancellation email that never sent,
         * a refund Stripe refused - are about a customer who still needs a phone call or money that's still
         * stuck, and no amount of editing touches those. Clearing the flag on some actions and not others
         * would leave staff unable to tell, at a glance, which rows they'd already dealt with. So nothing
         * clears it implicitly, and these two tests pin that down for both actions.
         * ----------------------------------------------------------------------------------------------- */

        [Fact]
        public async Task Reassigning_a_flagged_booking_to_a_live_barber_leaves_it_flagged()
        {
            AuthenticateAsAdmin();
            int leaverId, keeperId, bookingId;
            using (var db = NewDb())
            {
                var leaver = db.AddBarber();
                var keeper = db.AddBarber();
                db.AddSchedule(leaver.Id, ShopClock.Today.AddDays(-30));
                db.AddSchedule(keeper.Id, ShopClock.Today.AddDays(-30));
                leaverId = leaver.Id;
                keeperId = keeper.Id;
                bookingId = db.AddBooking(leaver.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            await Client.DeleteAsync($"/api/barbers/delete/{leaverId}?confirm=true");

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { barberId = keeperId }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // The reassignment happened...
            Assert.Equal(keeperId, booking.BarberId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            // ...but the admin, not the edit, decides the worklist entry is done.
            Assert.True(booking.NeedsReview);
        }

        [Fact]
        public async Task Cancelling_a_flagged_booking_leaves_it_flagged()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            var response = await Client.PatchAsync($"/api/bookings/cancel/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.True(booking.NeedsReview);
        }

        /* A second problem must not erase the first. A booking flagged over stuck money that then has its
         * barber walk out has two things owed on it, and the money one is the expensive one to forget. */
        [Fact]
        public async Task A_second_flag_is_added_to_the_note_rather_than_replacing_it()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                booking.NeedsReview = true;
                booking.ReviewReason = "Check Stripe for a charge on this booking and refund by hand.";
                db.SaveChanges();
                bookingId = booking.Id;
            }

            await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.True(booking2.NeedsReview);
            Assert.Contains("Check Stripe", booking2.ReviewReason);
            Assert.Contains("has left the shop", booking2.ReviewReason);
        }

        // The same problem landing twice is one note, not the same sentence printed twice.
        [Fact]
        public async Task The_same_flag_twice_does_not_repeat_itself()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");
            // Reviving and deactivating again re-runs the same flagging over an already-flagged booking.
            using (var db = NewDb())
            {
                db.Barbers.Single(b => b.Id == barberId).isActive = true;
                db.SaveChanges();
            }
            await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            using var assertDb = NewDb();
            var reason = (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).ReviewReason!;
            var occurrences = reason.Split("has left the shop").Length - 1;
            Assert.Equal(1, occurrences);
        }

        /* Bringing the barber back makes their stranded bookings fine again - same barber, same time - so
         * the admin is handed the list to go and clear. The flags themselves stay: a note may have picked
         * up other problems since, and only the admin can judge that. */
        [Fact]
        public async Task Reactivating_the_barber_lists_the_bookings_that_are_fine_again()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            string email;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                email = db.Users.Single(u => u.Id == barber.UserId).Email!;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            // Reactivation reuses the create endpoint's revive-by-email branch.
            var form = new MultipartFormDataContent
            {
                { new StringContent("Test Person"), "FullName" },
                { new StringContent(email), "Email" },
                { new StringContent("Str0ngPassw0rd!"), "Password" }
            };
            var response = await Client.PostAsync("/api/barbers/create-barber", form);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var listed = (await ReadJson(response)).GetProperty("backOnDuty").EnumerateArray().ToList();
            Assert.Single(listed);
            Assert.Equal(bookingId, listed[0].GetProperty("id").GetInt32());

            using var assertDb = NewDb();
            // Listed, not cleared.
            Assert.True((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        // A booking dealt with while the barber was away isn't theirs any more, so it isn't listed.
        [Fact]
        public async Task A_booking_already_moved_to_another_barber_is_not_listed_on_reactivation()
        {
            AuthenticateAsAdmin();
            int leaverId, keeperId, bookingId;
            string email;
            using (var db = NewDb())
            {
                var leaver = db.AddBarber();
                var keeper = db.AddBarber();
                db.AddSchedule(leaver.Id, ShopClock.Today.AddDays(-30));
                db.AddSchedule(keeper.Id, ShopClock.Today.AddDays(-30));
                leaverId = leaver.Id;
                keeperId = keeper.Id;
                email = db.Users.Single(u => u.Id == leaver.UserId).Email!;
                bookingId = db.AddBooking(leaver.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            await Client.DeleteAsync($"/api/barbers/delete/{leaverId}?confirm=true");
            await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}", Body(new { barberId = keeperId }));

            var form = new MultipartFormDataContent
            {
                { new StringContent("Test Person"), "FullName" },
                { new StringContent(email), "Email" },
                { new StringContent("Str0ngPassw0rd!"), "Password" }
            };
            var response = await Client.PostAsync("/api/barbers/create-barber", form);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty((await ReadJson(response)).GetProperty("backOnDuty").EnumerateArray());
        }

        [Fact]
        public async Task Mark_reviewed_is_what_clears_it()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            await Client.DeleteAsync($"/api/barbers/delete/{barberId}?confirm=true");

            var response = await Client.PatchAsync($"/api/bookings/mark-reviewed/{bookingId}", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.False(booking.NeedsReview);
            Assert.Null(booking.ReviewReason);
        }
    }
}
