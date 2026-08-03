using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* POST /api/dates - creating a shop closure, and what it does to the bookings already on the slot.
     *
     * Two-step by design: the first request reports the conflicts and creates nothing; the admin re-submits
     * with ConfirmCancelBookings to go ahead.
     *
     * The split is the thing to hold on to, and it matches a barber deactivation and an hours change:
     *   - CONFIRMED bookings are grandfathered. They keep their slot, their barber and their money, and go
     *     into Needs Review for the admin to reassign, move or cancel. Nothing is emailed, because nothing
     *     has happened to them yet - they are still on.
     *   - PENDING bookings are cancelled outright. Nobody needs to review a checkout in flight.
     *
     * They used to be cancelled and refunded on confirm. That was the only irreversible action in the admin
     * screens - a wrong end date pushed real refunds through Stripe that correcting the date couldn't undo -
     * and it threw away the outcome the customer usually wants, which is being moved rather than refunded.
     *
     * Cash/unpaid bookings only here; the money paths are tier 2 (RefundFailureReviewTests). */
    public class ClosureConflictTests : IntegrationTestBase
    {
        public ClosureConflictTests(DatabaseFixture fixture) : base(fixture) { }

        private static object ClosureBody(DateOnly startDate, bool confirm, int? barberId = null,
            bool isFullDay = true, TimeOnly? startTime = null, TimeOnly? endTime = null) => new
            {
                barberId,
                startDate,
                endDate = (DateOnly?)null,
                isFullDay,
                startTime,
                endTime,
                reason = "Test closure",
                confirmCancelBookings = confirm
            };

        private int AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
            return admin.Id;
        }

        [Fact]
        public async Task An_overlapping_booking_is_reported_first_and_the_closure_is_not_created()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false)));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            var conflicts = payload.GetProperty("conflicts").EnumerateArray().ToList();
            Assert.Single(conflicts);
            Assert.Equal(bookingId, conflicts[0].GetProperty("id").GetInt32());
            // Nothing is emailed on confirm any more. A grandfathered booking is still ON, so telling the
            // customer anything at this point would be announcing a cancellation that hasn't happened.
            Assert.False(conflicts[0].GetProperty("willBeEmailed").GetBoolean());

            using var assertDb = NewDb();
            Assert.Empty(await assertDb.ShopClosures.ToListAsync());
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.Equal(CancellationReason.None, booking2.CancellationReason);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Confirming_creates_the_closure_and_flags_the_confirmed_booking_instead_of_cancelling_it()
        {
            AuthenticateAsAdmin();
            int bookingId, paymentId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                bookingId = booking.Id;
                paymentId = db.AddCashPayment(booking.Id).Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            // The closure is created regardless - the slot must end up shut either way.
            Assert.Single(await assertDb.ShopClosures.ToListAsync());

            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // Still on. The admin decides whether it's moved or cancelled, and the cancel path works the
            // money out from there (full refund, closure email - see AdminCancelTests).
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.Equal(CancellationReason.None, booking2.CancellationReason);

            Assert.True(booking2.NeedsReview);
            Assert.Contains("shop is closed", booking2.ReviewReason);
            // The note must say so out loud: unlike most worklist entries, nothing has happened here yet.
            Assert.Contains("NOT been told", booking2.ReviewReason);

            // Nothing has happened to the customer, so nothing is sent and no money moves.
            Assert.Empty(Factory.EnqueuedEmailJobs());
            Assert.Equal(PaymentStatus.COMPLETED,
                (await assertDb.Payments.SingleAsync(p => p.Id == paymentId)).Status);
        }

        [Fact]
        public async Task A_pending_conflict_is_cancelled_but_never_emailed()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                // No StripePaymentIntentId, so the PaymentIntent-void branch is skipped entirely.
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.PENDING).Id;
            }

            var warn = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false)));
            Assert.Equal(HttpStatusCode.Conflict, warn.StatusCode);
            // The admin is told up front that this one can't be reached by email.
            var conflicts = (await ReadJson(warn)).GetProperty("conflicts").EnumerateArray().ToList();
            Assert.False(conflicts[0].GetProperty("willBeEmailed").GetBoolean());

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ShopClosure, booking.CancellationReason);
            // A pending booking never got a confirmation, so there is nothing to walk back.
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task A_confirmed_booking_with_no_email_on_file_is_flagged_as_phone_only()
        {
            AuthenticateAsAdmin();
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                // Admin-created bookings carry only a phone number.
                db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED, contactEmail: null);
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false)));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await ReadJson(response);
            Assert.False(payload.GetProperty("conflicts")[0].GetProperty("willBeEmailed").GetBoolean());
            // The modal promises the worklist entry that the confirm step below actually creates - the admin
            // is told up front that these customers won't be silently forgotten once they click through.
            Assert.Contains("flagged in Needs Review", payload.GetProperty("message").GetString());
        }

        [Fact]
        public async Task The_worklist_note_carries_the_phone_number_when_there_is_no_email()
        {
            AuthenticateAsAdmin();
            int bookingId;
            string phone;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var customer = db.AddUser();
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED,
                    customer: customer, contactEmail: null);
                db.AddCashPayment(booking.Id);
                bookingId = booking.Id;
                phone = customer.Phone!;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // Grandfathered like any other confirmed booking - having no email doesn't change that.
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);

            /* A walk-in booked by staff carries a phone number and nothing else, so whenever the admin does
             * act on this one, no email can reach them. Putting the number in the note means that call is
             * possible without going digging for it. */
            Assert.True(booking2.NeedsReview);
            Assert.Contains("No email on file", booking2.ReviewReason);
            Assert.Contains(phone, booking2.ReviewReason);

            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        /* PATCH /api/dates/delete/{id} - the mirror of the above. Removing a closure makes the bookings it
         * flagged fine again, and the admin is shown which ones so they can clear the notes. Same treatment
         * as a widened schedule and a reactivated barber; without it those notes sit there describing a
         * closure that no longer exists.
         *
         * INFORMATION, never a "clear them all" button: notes accumulate, so the same booking may also be
         * carrying a failed refund. The admin reads each one and clears it by hand. */
        [Fact]
        public async Task Deleting_a_closure_lists_the_bookings_it_was_holding_up()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var created = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var closureId = (await ReadJson(created)).GetProperty("id").GetInt32();

            using (var db = NewDb())
                Assert.True((await db.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);

            var deleted = await Client.PatchAsync($"/api/dates/delete/{closureId}", null);

            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            var reopened = (await ReadJson(deleted)).GetProperty("noLongerClosed").EnumerateArray().ToList();
            Assert.Single(reopened);
            Assert.Equal(bookingId, reopened[0].GetProperty("id").GetInt32());

            // Announced, NOT auto-cleared - the booking may be carrying other notes too.
            using var assertDb = NewDb();
            Assert.True((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        /* A booking in the worklist for more than one thing is still announced when the closure half is
         * resolved. Hiding it would mean the admin never learns that part is done, and the booking would
         * sit there until someone re-derived the whole picture by hand. */
        [Fact]
        public async Task A_booking_flagged_for_something_else_too_is_still_listed_when_the_closure_goes()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var created = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));
            var closureId = (await ReadJson(created)).GetProperty("id").GetInt32();

            // A second, unrelated problem lands on the same booking - money this time.
            using (var db = NewDb())
            {
                var b = await db.Bookings.SingleAsync(x => x.Id == bookingId);
                b.FlagForReview("Refund failed - check Stripe by hand.");
                await db.SaveChangesAsync();
            }

            var deleted = await Client.PatchAsync($"/api/dates/delete/{closureId}", null);

            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            var reopened = (await ReadJson(deleted)).GetProperty("noLongerClosed").EnumerateArray().ToList();
            Assert.Single(reopened);
            Assert.Equal(bookingId, reopened[0].GetProperty("id").GetInt32());

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            // Both notes intact: the closure note can't be removed (it has a time pasted into it), and the
            // refund one can't be verified from state at all. The admin reads them and decides.
            Assert.Contains("Refund failed", booking.ReviewReason);
            Assert.True(booking.NeedsReview);
        }

        [Fact]
        public async Task A_booking_another_closure_still_covers_is_not_announced_as_reopened()
        {
            AuthenticateAsAdmin();
            int bookingId, barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            // Two closures over the same slot - a shop-wide holiday on top of this barber's day off.
            var barberClosure = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true, barberId: barberId)));
            var closureId = (await ReadJson(barberClosure)).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)))).StatusCode);

            var deleted = await Client.PatchAsync($"/api/dates/delete/{closureId}", null);

            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            // The shop is still shut that day, so calling this booking fine again would be a lie.
            Assert.Empty((await ReadJson(deleted)).GetProperty("noLongerClosed").EnumerateArray());
        }

        [Fact]
        public async Task A_barber_scoped_closure_leaves_other_barbers_bookings_alone()
        {
            AuthenticateAsAdmin();
            int closedBarberBookingId, otherBarberBookingId, closedBarberId;
            using (var db = NewDb())
            {
                var closed = db.AddBarber();
                var other = db.AddBarber();
                db.AddSchedule(closed.Id, ShopClock.Today.AddDays(-30));
                db.AddSchedule(other.Id, ShopClock.Today.AddDays(-30));
                closedBarberId = closed.Id;
                closedBarberBookingId = db.AddBooking(closed.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
                // Same slot, different chair - untouched by a closure scoped to one barber.
                otherBarberBookingId = db.AddBooking(other.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true, barberId: closedBarberId)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            // Both stay COMPLETED now, so the flag is what tells them apart.
            Assert.True((await assertDb.Bookings.SingleAsync(b => b.Id == closedBarberBookingId)).NeedsReview);
            var untouched = await assertDb.Bookings.SingleAsync(b => b.Id == otherBarberBookingId);
            Assert.Equal(BookingStatus.COMPLETED, untouched.Status);
            Assert.False(untouched.NeedsReview);
        }

        [Fact]
        public async Task A_booking_that_ends_exactly_when_a_partial_closure_starts_does_not_conflict()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                // 14:00 + 30min ends exactly at 14:30, where the closure begins. The overlap test is
                // half-open (end > closureStart), so this must NOT be swept up.
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 14), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: false, isFullDay: false,
                    startTime: new TimeOnly(14, 30), endTime: new TimeOnly(17, 0))));

            // No conflict reported at all - the closure is created straight away.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(BookingStatus.COMPLETED,
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).Status);
        }

        [Fact]
        public async Task A_past_booking_is_not_swept_up_by_a_closure_on_a_future_date()
        {
            AuthenticateAsAdmin();
            int pastBookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                pastBookingId = db.AddBooking(barber.Id, TestData.FutureAt(-3, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PostAsync("/api/dates",
                Body(ClosureBody(ShopClock.Today.AddDays(14), confirm: true)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == pastBookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.Equal(CancellationReason.None, booking.CancellationReason);
        }
    }
}
