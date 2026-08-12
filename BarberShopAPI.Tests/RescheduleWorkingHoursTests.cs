using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* Working hours on the two STAFF booking paths - create-admin-booking and update-booking.
     *
     * Staff used to be exempt entirely, which quietly broke the Needs Review worklist: a booking flagged
     * "the barber's hours changed and this now falls outside their schedule" could be moved to another
     * out-of-hours slot and look dealt with while being exactly the problem it was flagged for.
     *
     * The rule now is not a ban but a decision. Outside the hours needs confirmOutsideHours - a shop
     * staying open late for a regular is real, it just has to be something a person chose rather than
     * something a request drifted into. Two things follow, and both are tested here:
     *   - without the confirmation it's a 409, so a caller that never asks never gets through;
     *   - a BARBER may only confirm for their own chair. Agreeing to work late is theirs to agree to, so
     *     a colleague can't commit them to it by reassigning a booking onto their chair.
     *
     * Default seeded hours are every day 09:00-17:30 (TestData.AddSchedule) and bookings are 30 minutes,
     * so 16:00 fits and 18:00 does not. Grace-past-close is 0 unless a test says otherwise. */
    public class RescheduleWorkingHoursTests : IntegrationTestBase
    {
        public RescheduleWorkingHoursTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        private (int BarberId, int BookingId) ArrangeBarberWithBooking(
            TimeOnly? shiftStart = null, TimeOnly? shiftEnd = null, int bookingHour = 16)
        {
            using var db = NewDb();
            var barber = db.AddBarber();
            var shifts = Enumerable.Range(0, 7)
                .Select(d => ((DayOfWeek)d, shiftStart ?? new TimeOnly(9, 0), shiftEnd ?? new TimeOnly(17, 30)))
                .ToArray();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), null, shifts);
            var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, bookingHour), BookingStatus.COMPLETED);
            return (barber.Id, booking.Id);
        }

        [Fact]
        public async Task A_reschedule_inside_the_barbers_hours_is_accepted()
        {
            AuthenticateAsAdmin();
            var (_, bookingId) = ArrangeBarberWithBooking();

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 11) }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(TestData.FutureAt(15, 11),
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).StartDateTime);
        }

        [Fact]
        public async Task A_reschedule_past_the_end_of_the_shift_needs_confirming_first()
        {
            AuthenticateAsAdmin();
            var (_, bookingId) = ArrangeBarberWithBooking();
            var original = TestData.FutureAt(14, 16);

            // 18:00 on a 09:00-17:30 day.
            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 18) }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            Assert.True(payload.GetProperty("outsideWorkingHours").GetBoolean());

            using var assertDb = NewDb();
            // Refused means untouched - not moved and then complained about.
            Assert.Equal(original, (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).StartDateTime);
        }

        [Fact]
        public async Task A_reschedule_before_the_shift_starts_needs_confirming_first()
        {
            AuthenticateAsAdmin();
            var (_, bookingId) = ArrangeBarberWithBooking();

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 8) }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.True((await ReadJson(response)).GetProperty("outsideWorkingHours").GetBoolean());
        }

        [Fact]
        public async Task An_admin_who_confirms_can_move_a_booking_outside_the_hours()
        {
            AuthenticateAsAdmin();
            var (_, bookingId) = ArrangeBarberWithBooking();

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 18), confirmOutsideHours = true }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(TestData.FutureAt(15, 18), booking.StartDateTime);
            // A decision, not a problem - so it does NOT land in the worklist. (A later change to the
            // barber's hours will resurface it through SchedulesController's sweep, like any other
            // booking sitting outside the new hours.)
            Assert.False(booking.NeedsReview);
        }

        /* Confirming buys you the working-hours rule and nothing else. A closure is the shop being shut,
         * which is not the barber's to overrule by agreeing to come in. */
        [Fact]
        public async Task Confirming_does_not_get_a_booking_past_a_closure()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
                db.AddClosure(ShopClock.Today.AddDays(15), barberId: barber.Id);
            }

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 18), confirmOutsideHours = true }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("unavailable", (await ReadJson(response)).GetProperty("message").GetString());
        }

        // -------------------------------------------------------------------------------------------------
        // Who may agree to it. Working late is the barber's own call.
        // -------------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_barber_can_confirm_an_out_of_hours_slot_on_their_own_chair()
        {
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
                Client.Authenticate(barber.UserId, Role.BARBER, tokenVersion: 0);
            }

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 18), confirmOutsideHours = true }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        /* Reassignment is an admin action full stop - not just when the target slot is awkward. Putting
         * work on a colleague's day is the owner's call, and it matches the create path, where a barber
         * is pinned to their own chair and can't give a colleague new work either. */
        [Fact]
        public async Task A_barber_cannot_move_a_booking_onto_a_colleague()
        {
            int colleagueId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                var colleague = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                db.AddSchedule(colleague.Id, ShopClock.Today.AddDays(-30));
                colleagueId = colleague.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
                Client.Authenticate(barber.UserId, Role.BARBER, tokenVersion: 0);
            }

            // Refused even though 11:00 is squarely inside the colleague's hours - it's the reassignment
            // itself that a barber may not do, not the slot.
            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { barberId = colleagueId, startDateTime = TestData.FutureAt(15, 11) }));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("Only an admin", (await ReadJson(response)).GetProperty("message").GetString());

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.NotEqual(colleagueId, booking.BarberId);
            Assert.Equal(TestData.FutureAt(14, 16), booking.StartDateTime);
        }

        // Naming their own barber id explicitly is a reschedule, not a reassignment, so it's allowed.
        [Fact]
        public async Task A_barber_can_still_reschedule_on_their_own_chair()
        {
            int barberId, bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
                Client.Authenticate(barber.UserId, Role.BARBER, tokenVersion: 0);
            }

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { barberId, startDateTime = TestData.FutureAt(15, 11) }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(barberId, booking.BarberId);
            Assert.Equal(TestData.FutureAt(15, 11), booking.StartDateTime);
        }

        /* The boundary that decides whether "finishes at 17:30" means the last booking may START or must
         * END at 17:30. It must END there: a 30-minute booking at 17:00 is the last one of the day, and
         * 17:15 - which would run 15 minutes past close - is not allowed (grace is 0 by default). */
        [Fact]
        public async Task A_booking_ending_exactly_at_closing_time_fits_but_one_minute_over_does_not()
        {
            AuthenticateAsAdmin();
            var (_, bookingId) = ArrangeBarberWithBooking();

            var endsExactlyAtClose = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 17) }));
            Assert.Equal(HttpStatusCode.OK, endsExactlyAtClose.StatusCode);

            // One minute over is already "outside the hours", so it takes a confirmation like any other.
            var spillsOver = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 17, 1) }));
            Assert.Equal(HttpStatusCode.Conflict, spillsOver.StatusCode);
        }

        /* The same boundary from the other side, and with grace switched on so the end test alone cannot
         * catch it: 17:30 + 30 = 18:00 is exactly close + grace, so a rule that bounded only the END would
         * have moved this booking wholly past closing time without so much as a confirmation. Reschedule is
         * its own call into EvaluateHoursAsync, and the slot you can't create is the slot you must not be
         * able to move into either. */
        [Fact]
        public async Task A_reschedule_to_a_slot_starting_at_closing_time_needs_confirming_first()
        {
            AuthenticateAsAdmin();
            var (_, bookingId) = ArrangeBarberWithBooking();
            using (var db = NewDb()) db.SetShopSetting(s => s.GraceMinutesAfterClose = 30);

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 17, 30) }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var refusal = await ReadJson(response);
            // Both boundaries: the shift ends at 17:30 as well, so neither has room for a start there.
            Assert.True(refusal.GetProperty("outsideWorkingHours").GetBoolean());
            Assert.True(refusal.GetProperty("outsideShopHours").GetBoolean());

            // Untouched by the refusal - the booking is still where it was.
            using var assertDb = NewDb();
            Assert.Equal(TestData.FutureAt(14, 16),
                (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).StartDateTime);
        }

        [Fact]
        public async Task Reassigning_to_a_barber_who_is_not_working_then_is_rejected()
        {
            AuthenticateAsAdmin();
            int lateBarberId, earlyBarberId, bookingId;
            using (var db = NewDb())
            {
                var lateBarber = db.AddBarber();
                var earlyBarber = db.AddBarber();
                // The late barber works into the evening; the early one is gone by 13:00.
                db.AddSchedule(lateBarber.Id, ShopClock.Today.AddDays(-30), null,
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(20, 0))).ToArray());
                db.AddSchedule(earlyBarber.Id, ShopClock.Today.AddDays(-30), null,
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(13, 0))).ToArray());
                lateBarberId = lateBarber.Id;
                earlyBarberId = earlyBarber.Id;
                bookingId = db.AddBooking(lateBarber.Id, TestData.FutureAt(14, 19), BookingStatus.COMPLETED).Id;
            }

            // A 19:00 appointment can't simply be handed to someone who finishes at 13:00.
            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { barberId = earlyBarberId }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.True((await ReadJson(response)).GetProperty("outsideWorkingHours").GetBoolean());

            using var assertDb = NewDb();
            Assert.Equal(lateBarberId, (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).BarberId);
        }

        [Fact]
        public async Task Reassigning_to_a_barber_who_is_working_then_is_accepted()
        {
            AuthenticateAsAdmin();
            int keeperId, bookingId;
            using (var db = NewDb())
            {
                var leaver = db.AddBarber();
                var keeper = db.AddBarber();
                db.AddSchedule(leaver.Id, ShopClock.Today.AddDays(-30));
                db.AddSchedule(keeper.Id, ShopClock.Today.AddDays(-30));
                keeperId = keeper.Id;
                bookingId = db.AddBooking(leaver.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { barberId = keeperId }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(keeperId, (await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).BarberId);
        }

        /* The reason this check exists at all. A booking flagged by a schedule change must not be
         * rescuable by a move that leaves it just as far outside the hours - otherwise the admin clears a
         * worklist entry that was never actually resolved. */
        [Fact]
        public async Task An_orphaned_booking_cannot_be_moved_to_another_out_of_hours_slot()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), null,
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(13, 0))).ToArray());
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                booking.NeedsReview = true;
                booking.ReviewReason = "The barber's working hours changed and this booking now falls outside their schedule.";
                db.SaveChanges();
                bookingId = booking.Id;
            }

            // 18:00 is no more inside 09:00-13:00 than the 16:00 it already sat at, so this can't be done
            // by accident - the admin has to be shown the warning and agree to it.
            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 18) }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(TestData.FutureAt(14, 16), booking2.StartDateTime);
            Assert.True(booking2.NeedsReview);
        }

        /* And when they do agree, the flag still stands. Nothing clears NeedsReview implicitly (see
         * UpdateBooking) - the admin decides the worklist entry is settled by marking it reviewed, which
         * is honest here precisely because they just made a conscious call about the slot. */
        [Fact]
        public async Task Confirming_the_move_leaves_the_flag_for_the_admin_to_clear()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), null,
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(13, 0))).ToArray());
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED);
                booking.NeedsReview = true;
                booking.ReviewReason = "The barber's working hours changed and this booking now falls outside their schedule.";
                db.SaveChanges();
                bookingId = booking.Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(15, 18), confirmOutsideHours = true }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(TestData.FutureAt(15, 18), booking2.StartDateTime);
            Assert.True(booking2.NeedsReview);
        }

        // -------------------------------------------------------------------------------------------------
        // POST /api/bookings/create-admin-booking - the same rule, so a slot is never bookable from scratch
        // but unreachable by moving an existing booking into it.
        // -------------------------------------------------------------------------------------------------

        // User.Phone is unique, so each walk-in in these tests needs its own number.
        private static int _phoneSeq;

        private static object AdminBookingBody(int barberId, DateTime start, int durationMin = 30) => new
        {
            barberId,
            startDateTime = start,
            defaultDurationMin = durationMin,
            fullName = "Walk In",
            phone = $"+35677{Interlocked.Increment(ref _phoneSeq):000000}"
        };

        [Fact]
        public async Task Creating_outside_the_barbers_hours_needs_confirming_first()
        {
            AuthenticateAsAdmin();
            var (barberId, _) = ArrangeBarberWithBooking();
            var lateSlot = TestData.FutureAt(15, 19);

            var response = await Client.PostAsync("/api/bookings/create-admin-booking",
                Body(AdminBookingBody(barberId, lateSlot)));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            Assert.True(payload.GetProperty("outsideWorkingHours").GetBoolean());

            using var assertDb = NewDb();
            // Refused outright - no half-made booking or customer row left behind.
            Assert.False(await assertDb.Bookings.AnyAsync(b => b.StartDateTime == lateSlot));
        }

        [Fact]
        public async Task Creating_outside_the_barbers_hours_goes_through_once_confirmed()
        {
            AuthenticateAsAdmin();
            var (barberId, _) = ArrangeBarberWithBooking();
            var lateSlot = TestData.FutureAt(15, 19);

            var response = await Client.PostAsync("/api/bookings/create-admin-booking",
                Body(new
                {
                    barberId,
                    startDateTime = lateSlot,
                    defaultDurationMin = 30,
                    fullName = "Late Regular",
                    phone = $"+35677{Interlocked.Increment(ref _phoneSeq):000000}",
                    confirmOutsideHours = true
                }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.StartDateTime == lateSlot);
            Assert.False(booking.NeedsReview);
        }

        [Fact]
        public async Task Staff_can_create_a_booking_inside_the_barbers_hours()
        {
            AuthenticateAsAdmin();
            var (barberId, _) = ArrangeBarberWithBooking();

            var response = await Client.PostAsync("/api/bookings/create-admin-booking",
                Body(AdminBookingBody(barberId, TestData.FutureAt(15, 11))));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        /* What staff KEEP. The customer lead time (90 minutes by default) and the 60-day horizon still
         * don't apply to them - only the working-hours rule does. Both of these would be refused on the
         * customer path, so a pass here proves the exemption survived the change. */
        [Fact]
        public async Task Staff_keep_their_exemption_from_the_lead_time_and_the_booking_horizon()
        {
            AuthenticateAsAdmin();
            int barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberId = barber.Id;
            }

            // Well beyond the 60-day customer horizon.
            var farOut = await Client.PostAsync("/api/bookings/create-admin-booking",
                Body(AdminBookingBody(barberId, TestData.FutureAt(200, 11))));
            Assert.Equal(HttpStatusCode.OK, farOut.StatusCode);

            // And tomorrow's first slot, which a customer booking it late tonight could fall foul of.
            var soon = await Client.PostAsync("/api/bookings/create-admin-booking",
                Body(AdminBookingBody(barberId, TestData.FutureAt(1, 9))));
            Assert.Equal(HttpStatusCode.OK, soon.StatusCode);
        }

        /* Fails closed. A barber with no schedule version covering the date has no hours to be inside of,
         * so the move is refused rather than waved through on a missing record. */
        [Fact]
        public async Task A_date_no_schedule_version_covers_is_rejected()
        {
            AuthenticateAsAdmin();
            int bookingId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                // Hours that expire before the date being moved to.
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), ShopClock.Today.AddDays(20));
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(14, 16), BookingStatus.COMPLETED).Id;
            }

            var response = await Client.PatchAsync($"/api/bookings/update-booking/{bookingId}",
                Body(new { startDateTime = TestData.FutureAt(40, 11) }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.True((await ReadJson(response)).GetProperty("outsideWorkingHours").GetBoolean());
        }
    }
}
