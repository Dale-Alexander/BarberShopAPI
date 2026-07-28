using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* Tier 1 of the cancellation/orphaning matrix: the schedule-change trigger (SchedulesController).
     *
     * This trigger is the odd one out - it never cancels anything. A confirmed booking left outside the
     * barber's new hours is GRANDFATHERED (stays COMPLETED) and flagged for the admin's Needs Review
     * worklist instead, after a one-time 409 confirmation. These tests pin down both halves of that: what
     * gets flagged, and just as importantly what does not.
     *
     * Pure DB - no Stripe, no email. Bookings here have no PaymentIntent, and this path never calls
     * BookingCanceller at all. */
    public class ScheduleOrphaningTests : IntegrationTestBase
    {
        public ScheduleOrphaningTests(DatabaseFixture fixture) : base(fixture) { }

        // Every weekday covered, so a test only ever varies the TIME window - a booking is never
        // orphaned merely because its weekday stopped being a working day.
        private static object[] AllDays(int startHour, int startMinute, int endHour, int endMinute) =>
            Enumerable.Range(0, 7)
                .Select(d => (object)new
                {
                    dayOfWeek = d,
                    startTime = new TimeOnly(startHour, startMinute),
                    endTime = new TimeOnly(endHour, endMinute)
                })
                .ToArray();

        private async Task<(int BarberId, int ScheduleId, int BookingId)> ArrangeBarberWithLateBooking(
            BookingStatus status = BookingStatus.COMPLETED, int daysAhead = 14, int bookingHour = 16)
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            var barber = db.AddBarber();
            // Current, open-ended hours: every day 09:00-17:30.
            var schedule = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            var booking = db.AddBooking(barber.Id, TestData.FutureAt(daysAhead, bookingHour), status);

            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
            return (barber.Id, schedule.Id, booking.Id);
        }

        // ---------------------------------------------------------------------------------------------
        // PUT /api/schedules/version/{id} - editing an existing version's hours
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Editing_hours_that_strand_a_confirmed_booking_returns_409_and_changes_nothing()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();

            // 16:00 booking no longer fits a day that now ends at 15:00.
            var response = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            var affected = payload.GetProperty("affected").EnumerateArray().ToList();
            Assert.Single(affected);
            Assert.Equal(bookingId, affected[0].GetProperty("id").GetInt32());

            // A soft warning must be genuinely side-effect free: the hours are untouched and the booking
            // is neither flagged nor cancelled, so the admin can still back out.
            using var db = NewDb();
            var shifts = await db.BarberScheduleShifts.Where(s => s.BarberScheduleId == scheduleId).ToListAsync();
            Assert.All(shifts, s => Assert.Equal(new TimeOnly(17, 30), s.EndTime));

            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.False(booking.NeedsReview);
            Assert.Equal(CancellationReason.None, booking.CancellationReason);
        }

        [Fact]
        public async Task Confirming_the_edit_grandfathers_the_booking_and_flags_it_for_review()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();

            var response = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = true }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var db = NewDb();
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);

            // Grandfathered: this trigger flags, it never cancels.
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.Equal(CancellationReason.None, booking.CancellationReason);
            Assert.True(booking.NeedsReview);
            Assert.Contains("working hours changed", booking.ReviewReason);

            // And the edit itself actually landed.
            var shifts = await db.BarberScheduleShifts.Where(s => s.BarberScheduleId == scheduleId).ToListAsync();
            Assert.Equal(7, shifts.Count);
            Assert.All(shifts, s => Assert.Equal(new TimeOnly(15, 0), s.EndTime));

            // Nothing is emailed for a grandfathered booking - the admin handles it by hand.
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Editing_hours_that_still_cover_the_booking_flags_nothing()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();

            // Widened hours - the 16:00 booking still fits, so there is nothing to warn about.
            var response = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(8, 0, 18, 0), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var db = NewDb();
            Assert.False((await db.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        [Fact]
        public async Task A_pending_booking_outside_the_new_hours_is_neither_warned_about_nor_flagged()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking(BookingStatus.PENDING);

            // Deliberate exclusion: PENDING bookings self-resolve. Either the customer confirms and
            // ConfirmCashBooking / the webhook re-checks the schedule and cancels them properly, or they
            // expire. So they must not even raise the confirmation prompt.
            var response = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var db = NewDb();
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.PENDING, booking.Status);
            Assert.False(booking.NeedsReview);
        }

        [Fact]
        public async Task A_past_booking_outside_the_new_hours_is_not_flagged()
        {
            using var arrange = NewDb();
            var admin = arrange.AddUser(Role.ADMIN);
            var barber = arrange.AddBarber();
            var schedule = arrange.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            // Already happened - changing future hours can't strand an appointment that is over.
            var booking = arrange.AddBooking(barber.Id, TestData.FutureAt(-3, 16), BookingStatus.COMPLETED);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);

            var response = await Client.PutAsync($"/api/schedules/version/{schedule.Id}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var db = NewDb();
            Assert.False((await db.Bookings.SingleAsync(b => b.Id == booking.Id)).NeedsReview);
        }

        [Fact]
        public async Task An_edit_only_strands_bookings_inside_the_edited_versions_own_date_window()
        {
            int adminId, editedScheduleId, insideBookingId, outsideBookingId;
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                // Two contiguous versions; the edit targets the FIRST one.
                var edited = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), ShopClock.Today.AddDays(10));
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(11), null);

                // Identical 16:00 bookings either side of the boundary. Only the one the edited version
                // actually governs may be flagged - asserting both directions in one request means this
                // can't pass just because the edit silently did nothing.
                insideBookingId = db.AddBooking(barber.Id, TestData.FutureAt(5, 16), BookingStatus.COMPLETED).Id;
                outsideBookingId = db.AddBooking(barber.Id, TestData.FutureAt(20, 16), BookingStatus.COMPLETED).Id;

                adminId = admin.Id;
                editedScheduleId = edited.Id;
            }
            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.PutAsync($"/api/schedules/version/{editedScheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = true }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.True((await assertDb.Bookings.SingleAsync(b => b.Id == insideBookingId)).NeedsReview);
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == outsideBookingId)).NeedsReview);
        }

        [Theory]
        // A 16:45 booking runs to 17:15. With 30 minutes of grace it still finishes inside 17:00 + grace,
        // so it is not stranded; with no grace it is. Grace applies to the day's LAST shift only.
        [InlineData(30, false)]
        [InlineData(0, true)]
        public async Task Grace_minutes_decide_whether_a_booking_that_runs_past_closing_is_stranded(
            int graceMinutes, bool expectOrphaned)
        {
            int adminId, scheduleId, bookingId;
            using (var db = NewDb())
            {
                db.SetShopSetting(s => s.GraceMinutesAfterClose = graceMinutes);
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                var schedule = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(14, 16, 45), BookingStatus.COMPLETED, durationMin: 30);

                adminId = admin.Id;
                scheduleId = schedule.Id;
                bookingId = booking.Id;
            }
            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 17, 0), confirmOrphaned = true }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(expectOrphaned, booking2.NeedsReview);
        }

        [Fact]
        public async Task A_version_that_has_already_ended_cannot_be_edited()
        {
            int adminId, pastScheduleId;
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                var past = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), ShopClock.Today.AddDays(-10));
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-9), null);

                adminId = admin.Id;
                pastScheduleId = past.Id;
            }
            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.PutAsync($"/api/schedules/version/{pastScheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = true }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Past schedules", (await ReadJson(response)).GetProperty("message").GetString());
        }

        [Theory]
        // Both come from ValidateShifts, which runs before the orphan check - a malformed edit must never
        // reach the point of flagging bookings.
        [InlineData(9, 0, 12, 0, 11, 0, 15, 0, "can't overlap")]
        [InlineData(15, 0, 9, 0, 16, 0, 17, 0, "must start before it ends")]
        public async Task Invalid_shifts_are_rejected_before_anything_is_evaluated(
            int aStartH, int aStartM, int aEndH, int aEndM,
            int bStartH, int bStartM, int bEndH, int bEndM, string expectedMessage)
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();

            var shifts = new object[]
            {
                new { dayOfWeek = 1, startTime = new TimeOnly(aStartH, aStartM), endTime = new TimeOnly(aEndH, aEndM) },
                new { dayOfWeek = 1, startTime = new TimeOnly(bStartH, bStartM), endTime = new TimeOnly(bEndH, bEndM) }
            };

            var response = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts, confirmOrphaned = true }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expectedMessage, (await ReadJson(response)).GetProperty("message").GetString());

            using var db = NewDb();
            Assert.False((await db.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        // ---------------------------------------------------------------------------------------------
        // POST /api/schedules/barber/{id} - starting a new seasonal version
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Creating_a_version_that_strands_a_booking_warns_first_and_creates_nothing()
        {
            var (barberId, _, bookingId) = await ArrangeBarberWithLateBooking(daysAhead: 20);

            var response = await Client.PostAsync($"/api/schedules/barber/{barberId}",
                Body(new
                {
                    effectiveFrom = ShopClock.Today.AddDays(10),
                    shifts = AllDays(9, 0, 15, 0),
                    confirmOrphaned = false
                }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var affected = (await ReadJson(response)).GetProperty("affected").EnumerateArray().ToList();
            Assert.Single(affected);
            Assert.Equal(bookingId, affected[0].GetProperty("id").GetInt32());

            using var db = NewDb();
            // The whole point of warning first: no new version, and the existing one is still open-ended.
            Assert.Single(await db.BarberSchedules.Where(s => s.BarberId == barberId).ToListAsync());
            Assert.Null((await db.BarberSchedules.SingleAsync(s => s.BarberId == barberId)).EffectiveTo);
            Assert.False((await db.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        [Fact]
        public async Task Confirming_a_new_version_chains_the_old_one_and_flags_the_stranded_booking()
        {
            var (barberId, oldScheduleId, bookingId) = await ArrangeBarberWithLateBooking(daysAhead: 20);
            var effectiveFrom = ShopClock.Today.AddDays(10);

            var response = await Client.PostAsync($"/api/schedules/barber/{barberId}",
                Body(new { effectiveFrom, shifts = AllDays(9, 0, 15, 0), confirmOrphaned = true }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var db = NewDb();
            var versions = await db.BarberSchedules.Where(s => s.BarberId == barberId)
                .OrderBy(s => s.EffectiveFrom).ToListAsync();
            Assert.Equal(2, versions.Count);

            // Chaining: the old version closes the day before the new one starts, leaving exactly one
            // open-ended version (which the filtered unique index also enforces).
            Assert.Equal(oldScheduleId, versions[0].Id);
            Assert.Equal(effectiveFrom.AddDays(-1), versions[0].EffectiveTo);
            Assert.Equal(effectiveFrom, versions[1].EffectiveFrom);
            Assert.Null(versions[1].EffectiveTo);

            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking.Status);
            Assert.True(booking.NeedsReview);
            Assert.Contains("working hours changed", booking.ReviewReason);
        }

        [Fact]
        public async Task A_new_version_cannot_start_in_the_past()
        {
            var (barberId, _, _) = await ArrangeBarberWithLateBooking();

            var response = await Client.PostAsync($"/api/schedules/barber/{barberId}",
                Body(new
                {
                    effectiveFrom = ShopClock.Today.AddDays(-1),
                    shifts = AllDays(9, 0, 15, 0),
                    confirmOrphaned = true
                }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("can't start in the past", (await ReadJson(response)).GetProperty("message").GetString());
        }

        // ---------------------------------------------------------------------------------------------
        // DELETE /api/schedules/version/{id}
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public async Task Deleting_the_current_version_can_strand_a_booking_with_no_warning_and_no_flag()
        {
            /* CHARACTERISATION TEST - documents a GAP, not desired behaviour.
             *
             * PUT and POST both run FindOrphanedBookingsAsync and make the admin confirm. DELETE does not:
             * removing the current version reopens the prior one, whose narrower hours can strand exactly
             * the same confirmed bookings - silently, with no 409 and no Needs Review flag. If that gap is
             * ever closed, this test SHOULD fail; update it to expect the 409 rather than deleting it. */
            int adminId, currentScheduleId, bookingId;
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                // Prior version: mornings only. Current version: full days.
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), ShopClock.Today.AddDays(9),
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(12, 0))).ToArray());
                var current = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(10), null);
                // 16:00 fits the current version, but not the prior one that deletion will reopen.
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(20, 16), BookingStatus.COMPLETED);

                adminId = admin.Id;
                currentScheduleId = current.Id;
                bookingId = booking.Id;
            }
            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.DeleteAsync($"/api/schedules/version/{currentScheduleId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.False(booking2.NeedsReview); // <- the gap: nobody was told

            // The prior version really did reopen and really does strand it.
            var reopened = await assertDb.BarberSchedules.Include(s => s.Shifts)
                .SingleAsync(s => s.EffectiveTo == null);
            Assert.All(reopened.Shifts, s => Assert.Equal(new TimeOnly(12, 0), s.EndTime));
        }

        [Fact]
        public async Task A_barbers_only_schedule_version_cannot_be_deleted()
        {
            var (_, scheduleId, _) = await ArrangeBarberWithLateBooking();

            // Deleting it would leave the barber with no hours at all, i.e. permanently unbookable.
            var response = await Client.DeleteAsync($"/api/schedules/version/{scheduleId}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("only one", (await ReadJson(response)).GetProperty("message").GetString());
        }
    }
}
