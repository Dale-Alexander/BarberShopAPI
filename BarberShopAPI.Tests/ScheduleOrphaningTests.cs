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

        /* Widening hours saves without asking anything, so the admin who just undid their own mistake gets
         * no sign that the bookings it flagged now fit again. The response carries a count so the editor
         * can point them at the list. A count only - the flags stay, because a flagged booking's note may
         * carry other problems that widening the hours does nothing about. */
        [Fact]
        public async Task Widening_hours_again_reports_the_bookings_that_now_fit_without_unflagging_them()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();

            // Narrow to 15:00, stranding the 16:00 booking, then put the hours back.
            await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = true }));

            var widened = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 17, 30), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, widened.StatusCode);
            var listed = (await ReadJson(widened)).GetProperty("backInsideHours").EnumerateArray().ToList();
            Assert.Single(listed);
            // Enough detail to find the booking in the review list without hunting.
            Assert.Equal(bookingId, listed[0].GetProperty("id").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(listed[0].GetProperty("date").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(listed[0].GetProperty("time").GetString()));
            // Nothing else about the slot is in the way, so the admin can act on this straight away.
            Assert.Equal(System.Text.Json.JsonValueKind.Null, listed[0].GetProperty("stillBlockedBy").ValueKind);

            using var db = NewDb();
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            /* Announced, but nothing is withdrawn - not the flag and not the note, even though the app could
             * prove this particular note false. Notes go on automatically and come off only when a human
             * marks the booking reviewed, for every reason alike. A single reason that tidied up after
             * itself would set an expectation the other two can't meet. */
            Assert.True(booking.NeedsReview);
            Assert.Contains("falls outside their", booking.ReviewReason);
        }

        /* This list answers one question - "do these fit your hours again?" - and a closure elsewhere doesn't
         * change the answer. The booking carries the closure's own note, which the admin reads before
         * clearing anything; suppressing it here would hide that the HOURS problem really is resolved, the
         * same mistake as hiding a booking because it also has a stuck refund. Each list vouches for its own
         * dimension, and none of them clears a flag. */
        [Fact]
        public async Task A_booking_a_closure_also_covers_is_listed_but_keeps_its_closure_note()
        {
            var (barberId, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();

            await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = true }));

            // The shop shuts that day too. Created through the API, so it flags the booking exactly as it
            // would in real use - this booking now carries TWO notes.
            var closure = await Client.PostAsync("/api/dates", Body(new
            {
                barberId,
                startDate = ShopClock.Today.AddDays(14),
                endDate = (DateOnly?)null,
                isFullDay = true,
                startTime = (TimeOnly?)null,
                endTime = (TimeOnly?)null,
                reason = "Test closure",
                confirmCancelBookings = true
            }));
            Assert.Equal(HttpStatusCode.OK, closure.StatusCode);

            var widened = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 17, 30), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, widened.StatusCode);
            var listed = (await ReadJson(widened)).GetProperty("backInsideHours").EnumerateArray().ToList();
            // Listed: the hours problem really is resolved, and hiding it would mean the admin never hears so.
            Assert.Single(listed);
            Assert.Equal(bookingId, listed[0].GetProperty("id").GetInt32());
            // But told, on the row, that it isn't clear yet - no guessing required.
            Assert.Contains("closure", listed[0].GetProperty("stillBlockedBy").GetString());

            using var db2 = NewDb();
            var booking = await db2.Bookings.SingleAsync(b => b.Id == bookingId);
            // Both notes stay: nothing is ever withdrawn automatically. StillBlockedBy above is how the
            // admin learns where this booking actually stands without re-deriving it from the notes.
            Assert.Contains("falls outside their", booking.ReviewReason);
            /* This closure is scoped to the one barber (it posts a barberId), so the note says the BARBER
               is off, not that the shop is shut - the shop is open and the booking could go to another
               chair. Asserting the marker rather than prose keeps this pinned to the one author of that
               sentence (see ReviewMarkers). */
            Assert.Contains(ReviewMarkers.BarberClosed, booking.ReviewReason);
            Assert.DoesNotContain(ReviewMarkers.ShopClosed, booking.ReviewReason);
            Assert.True(booking.NeedsReview);
        }

        /* Only bookings flagged BY THE SCHEDULE PATH are listed. One flagged over money that happens to sit
         * inside the hours must not be announced as though widening them had resolved anything. */
        [Fact]
        public async Task A_booking_flagged_for_another_reason_is_not_listed()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();

            await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = true }));

            // Replace the schedule note with an unrelated one - same booking, different problem.
            using (var db = NewDb())
            {
                var booking = db.Bookings.Single(b => b.Id == bookingId);
                booking.ReviewReason = "Check Stripe for a charge on this booking.";
                db.SaveChanges();
            }

            var widened = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 17, 30), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, widened.StatusCode);
            Assert.Empty((await ReadJson(widened)).GetProperty("backInsideHours").EnumerateArray());
        }

        // A booking that always fitted must not be counted - the count means "these changed status", not
        // "these are flagged", or every unrelated flag would look like the schedule change fixed it.
        [Fact]
        public async Task A_flagged_booking_that_already_fitted_is_not_counted()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking();
            using (var db = NewDb())
            {
                var booking = db.Bookings.Single(b => b.Id == bookingId);
                booking.NeedsReview = true;   // flagged over money, nothing to do with hours
                booking.ReviewReason = "Check Stripe for a charge on this booking.";
                db.SaveChanges();
            }

            // Widen further; the 16:00 booking fitted 09:00-17:30 before and fits 09:00-20:00 now.
            var widened = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 20, 0), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, widened.StatusCode);
            Assert.Empty((await ReadJson(widened)).GetProperty("backInsideHours").EnumerateArray());
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
        public async Task A_pending_booking_outside_the_new_hours_is_cancelled_without_a_prompt()
        {
            var (_, scheduleId, bookingId) = await ArrangeBarberWithLateBooking(BookingStatus.PENDING);

            /* A checkout on a slot the new hours remove is cancelled outright, the same treatment a closure
             * gives it - but WITHOUT raising the confirmation prompt, which is only for confirmed bookings
             * the admin has to decide about. Blocking the edit on someone's half-finished checkout would be
             * the wrong trade, and there is nothing for the admin to weigh up here anyway.
             *
             * The confirmation-time checks still exist and still matter (a card already going through can't
             * be voided, so it lands there instead) - this just spares the common case a charge followed by
             * a refund minutes later. */
            var response = await Client.PutAsync($"/api/schedules/version/{scheduleId}",
                Body(new { shifts = AllDays(9, 0, 15, 0), confirmOrphaned = false }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var db = NewDb();
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ScheduleChange, booking.CancellationReason);
            // Never confirmed, so there is nothing to walk back: no flag for staff and no email.
            Assert.False(booking.NeedsReview);
            Assert.Empty(Factory.EnqueuedEmailJobs());
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
        public async Task A_pending_booking_stranded_by_a_new_version_is_cancelled_without_a_prompt()
        {
            // The third way hours can move (edit / delete / start a new version) and so the third place a
            // checkout can be left on a slot that no longer exists. Same answer as the other two.
            var (barberId, _, bookingId) = await ArrangeBarberWithLateBooking(BookingStatus.PENDING, daysAhead: 20);

            var response = await Client.PostAsync($"/api/schedules/barber/{barberId}",
                Body(new
                {
                    effectiveFrom = ShopClock.Today.AddDays(10),
                    shifts = AllDays(9, 0, 15, 0),
                    confirmOrphaned = false
                }));

            // No prompt: the new version is created on the first request, not held back for confirmation.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var db = NewDb();
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ScheduleChange, booking.CancellationReason);
            Assert.False(booking.NeedsReview);
            Assert.Empty(Factory.EnqueuedEmailJobs());

            // The cancellation must not have rolled back the version swap it followed.
            Assert.Equal(2, await db.BarberSchedules.CountAsync(s => s.BarberId == barberId));
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
        public async Task Deleting_the_current_version_warns_before_stranding_a_booking()
        {
            /* Removing a version reopens the prior one, whose narrower hours strand exactly the bookings the
             * deleted version made room for. This used to happen silently - no 409, no flag - which was the
             * worst place for it: "undo" is when an admin is least likely to think about the bookings taken
             * under the hours being removed. Now it matches PUT and POST: warn first, change nothing. */
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

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await ReadJson(response);
            Assert.True(payload.GetProperty("requiresConfirmation").GetBoolean());
            var affected = payload.GetProperty("affected").EnumerateArray().ToList();
            Assert.Single(affected);
            Assert.Equal(bookingId, affected[0].GetProperty("id").GetInt32());
            // Not "the new hours" - nothing new is being proposed, the old ones are coming back.
            Assert.Contains("the hours this restores", payload.GetProperty("message").GetString());

            using var assertDb = NewDb();
            // A soft warning must be side-effect free: the version is still there, the prior one is still
            // closed, and the booking is untouched - so the admin can still back out.
            Assert.Equal(2, await assertDb.BarberSchedules.CountAsync());
            Assert.Equal(currentScheduleId,
                (await assertDb.BarberSchedules.SingleAsync(s => s.EffectiveTo == null)).Id);
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.False(booking2.NeedsReview);
        }

        [Fact]
        public async Task Confirming_the_delete_reopens_the_prior_version_and_flags_the_stranded_booking()
        {
            int adminId, currentScheduleId, priorScheduleId, bookingId;
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                // Prior version: mornings only. Current version: full days.
                var prior = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), ShopClock.Today.AddDays(9),
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(12, 0))).ToArray());
                var current = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(10), null);
                // 16:00 fits the current version, but not the prior one that deletion reopens.
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(20, 16), BookingStatus.COMPLETED);

                adminId = admin.Id;
                priorScheduleId = prior.Id;
                currentScheduleId = current.Id;
                bookingId = booking.Id;
            }
            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.DeleteAsync(
                $"/api/schedules/version/{currentScheduleId}?confirmOrphaned=true");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            // The removal really happened: the current version is gone and the prior one is open-ended again.
            Assert.Null(await assertDb.BarberSchedules.FirstOrDefaultAsync(s => s.Id == currentScheduleId));
            var reopened = await assertDb.BarberSchedules.Include(s => s.Shifts)
                .SingleAsync(s => s.EffectiveTo == null);
            Assert.Equal(priorScheduleId, reopened.Id);
            Assert.All(reopened.Shifts, s => Assert.Equal(new TimeOnly(12, 0), s.EndTime));

            // Grandfathered, like every other schedule-change orphan: still COMPLETED, flagged not cancelled.
            var booking2 = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.COMPLETED, booking2.Status);
            Assert.Equal(CancellationReason.None, booking2.CancellationReason);
            Assert.True(booking2.NeedsReview);
            Assert.Contains("working hours changed", booking2.ReviewReason);
            Assert.Empty(Factory.EnqueuedEmailJobs());
        }

        [Fact]
        public async Task Deleting_a_version_that_strands_nothing_needs_no_confirmation()
        {
            int adminId, currentScheduleId, bookingId;
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                // Prior version is WIDER than the current one, so restoring it can't strand anything.
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), ShopClock.Today.AddDays(9),
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(8, 0), new TimeOnly(20, 0))).ToArray());
                var current = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(10), null);
                var booking = db.AddBooking(barber.Id, TestData.FutureAt(20, 16), BookingStatus.COMPLETED);

                adminId = admin.Id;
                currentScheduleId = current.Id;
                bookingId = booking.Id;
            }
            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.DeleteAsync($"/api/schedules/version/{currentScheduleId}");

            // No 409 to click through when there's nothing to warn about - the undo just works.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.False((await assertDb.Bookings.SingleAsync(b => b.Id == bookingId)).NeedsReview);
        }

        [Fact]
        public async Task A_pending_booking_stranded_by_a_delete_is_cancelled_without_a_prompt()
        {
            /* Same treatment as the edit path: restoring the prior (narrower) hours cancels a checkout they
             * strand, without warning about it - the prompt is for confirmed bookings the admin must decide
             * about, and blocking an undo on a half-finished checkout would be the wrong trade. */
            int adminId, currentScheduleId, bookingId;
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30), ShopClock.Today.AddDays(9),
                    Enumerable.Range(0, 7).Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(12, 0))).ToArray());
                var current = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(10), null);
                bookingId = db.AddBooking(barber.Id, TestData.FutureAt(20, 16), BookingStatus.PENDING).Id;

                adminId = admin.Id;
                currentScheduleId = current.Id;
            }
            Client.Authenticate(adminId, Role.ADMIN, tokenVersion: 0);

            var response = await Client.DeleteAsync($"/api/schedules/version/{currentScheduleId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var booking = await assertDb.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.CANCELLED, booking.Status);
            Assert.Equal(CancellationReason.ScheduleChange, booking.CancellationReason);
            Assert.False(booking.NeedsReview);
            Assert.Empty(Factory.EnqueuedEmailJobs());
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
