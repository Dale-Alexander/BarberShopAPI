using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* Shop opening hours (per weekday) and the two override flags that go with them.
     *
     * The three rules under test hang together, and the tests are grouped in that order:
     *
     *   1. Shop hours are a CEILING over barber shifts, enforced from both directions - a schedule edit is
     *      refused if it breaks the hours, and an hours edit is refused if it breaks a schedule. Enforcing
     *      only one side would leave the other as the way in.
     *   2. Because of (1), a customer inside a shift is automatically inside opening hours, so the customer
     *      path needs no new gate of its own - but it checks anyway, for schedules that predate the rule.
     *   3. Staff may step outside EITHER boundary on purpose, and what they overrode is recorded. That last
     *      part is the point: without it the schedule sweep reported every deliberate booking as stranded,
     *      at every edit, forever - it could never clear itself, because the "these are fine again" list
     *      only rescues bookings that land back INSIDE the new hours, which a deliberate one never does.
     */
    public class ShopHoursAndOverrideTests : IntegrationTestBase
    {
        public ShopHoursAndOverrideTests(DatabaseFixture fixture) : base(fixture) { }

        private int AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
            return admin.Id;
        }

        // ---- 1. The ceiling, from the schedule side ----------------------------------------------

        [Fact]
        public async Task A_shift_that_ends_after_the_shop_closes_is_refused()
        {
            AuthenticateAsAdmin();
            int barberId, versionId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                versionId = db.AddSchedule(barberId, ShopClock.Today.AddDays(-30)).Id;
            }

            // Shop closes 17:30; ask for a shift to 19:00.
            var response = await Client.PutAsync($"/api/Schedules/version/{versionId}", Body(new
            {
                shifts = new[] { new { dayOfWeek = 1, startTime = "09:00:00", endTime = "19:00:00" } },
                confirmOrphaned = false
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await ReadJson(response);
            var message = json.GetProperty("message").GetString();
            // The message has to name the offending shift - "shifts must fit shop hours" leaves the admin
            // hunting through seven days for the one that doesn't.
            Assert.Contains("Monday", message);
            Assert.Contains("17:30", message);

            using var check = NewDb();
            Assert.Equal(7, check.BarberScheduleShifts.Count(s => s.BarberScheduleId == versionId));
        }

        [Fact]
        public async Task A_shift_on_a_day_the_shop_is_closed_is_refused()
        {
            AuthenticateAsAdmin();
            int barberId, versionId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                versionId = db.AddSchedule(barberId, ShopClock.Today.AddDays(-30)).Id;
                db.SetShopHours(DayOfWeek.Sunday, isClosed: true);
            }

            var response = await Client.PutAsync($"/api/Schedules/version/{versionId}", Body(new
            {
                shifts = new[] { new { dayOfWeek = 0, startTime = "09:00:00", endTime = "17:00:00" } },
                confirmOrphaned = false
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("closed", (await ReadJson(response)).GetProperty("message").GetString());
        }

        [Fact]
        public async Task A_shift_inside_the_shop_hours_still_saves()
        {
            AuthenticateAsAdmin();
            int versionId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                versionId = db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30)).Id;
            }

            var response = await Client.PutAsync($"/api/Schedules/version/{versionId}", Body(new
            {
                shifts = new[] { new { dayOfWeek = 1, startTime = "10:00:00", endTime = "16:00:00" } },
                confirmOrphaned = false
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ---- 1b. The ceiling, from the hours side ------------------------------------------------

        [Fact]
        public async Task Narrowing_shop_hours_past_an_existing_shift_is_refused_and_names_the_barber()
        {
            AuthenticateAsAdmin();
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                // Widen first so a 09:00-17:30 shift is legal, then try to close at 16:00 under it.
                db.SetAllShopHours(new TimeOnly(8, 0), new TimeOnly(20, 0));
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            }

            var response = await Client.PutAsync("/api/Settings/hours", Body(new
            {
                days = Enumerable.Range(0, 7).Select(d => new
                {
                    dayOfWeek = d,
                    openTime = "09:00:00",
                    closeTime = "16:00:00",
                    isClosed = false
                }).ToArray()
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await ReadJson(response);
            var conflicts = json.GetProperty("conflicts");
            Assert.True(conflicts.GetArrayLength() > 0);
            Assert.Contains("closes", conflicts[0].GetProperty("reason").GetString());
            Assert.False(string.IsNullOrWhiteSpace(conflicts[0].GetProperty("barberName").GetString()));

            // Refused means UNCHANGED - a partial write here would leave the invariant broken.
            using var check = NewDb();
            Assert.All(check.ShopHours.ToList(), h => Assert.Equal(new TimeOnly(20, 0), h.CloseTime));
        }

        [Fact]
        public async Task Narrowing_shop_hours_is_allowed_once_no_shift_is_in_the_way()
        {
            AuthenticateAsAdmin();
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30),
                    shifts: Enumerable.Range(0, 7)
                        .Select(d => ((DayOfWeek)d, new TimeOnly(10, 0), new TimeOnly(15, 0)))
                        .ToArray());
            }

            var response = await Client.PutAsync("/api/Settings/hours", Body(new
            {
                days = Enumerable.Range(0, 7).Select(d => new
                {
                    dayOfWeek = d,
                    openTime = "09:00:00",
                    closeTime = "16:00:00",
                    isClosed = false
                }).ToArray()
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var check = NewDb();
            Assert.All(check.ShopHours.ToList(), h => Assert.Equal(new TimeOnly(16, 0), h.CloseTime));
        }

        /* The wire format the SETTINGS PAGE actually sends, minute-precision and all.
         *
         * Worth its own test because the first version of this suite sent "09:00:00" while the page sent
         * "09:00", so every test passed against an endpoint the UI could never successfully call - the
         * save failed on model binding before the controller ran, and surfaced as a bare "something went
         * wrong". A test that agrees with the client is the only kind that could have caught it. */
        [Fact]
        public async Task Saving_hours_accepts_the_minute_precision_the_settings_page_sends()
        {
            AuthenticateAsAdmin();
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30),
                    shifts: new[] { (DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(15, 0)) });
            }

            var response = await Client.PutAsync("/api/Settings/hours", Body(new
            {
                days = Enumerable.Range(0, 7).Select(d => new
                {
                    dayOfWeek = d,
                    openTime = "09:00:00",
                    // The case that reported the bug: an odd minute, not on any tidy boundary.
                    closeTime = "17:31:00",
                    isClosed = false
                }).ToArray()
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var check = NewDb();
            Assert.All(check.ShopHours.ToList(), h => Assert.Equal(new TimeOnly(17, 31), h.CloseTime));
        }

        /* The settings page's OTHER save, sent field-for-field as the page sends it.
         *
         * SlotStepMin is validated against an allowed set, so a caller that forgets it binds 0 and the
         * whole save 400s - including the six unrelated policy numbers next to it. Cheap to assert that
         * the page and the contract still agree; expensive to discover from a support message. */
        [Fact]
        public async Task Saving_settings_accepts_the_full_payload_the_settings_page_sends()
        {
            AuthenticateAsAdmin();

            var response = await Client.PutAsync("/api/Settings", Body(new
            {
                bufferMin = 10,
                defaultAdminBookingDurationMin = 45,
                graceMinutesAfterClose = 15,
                minAdvanceBookingMinutes = 120,
                maxAdvanceBookingDays = 30,
                refundCutoffHours = 48,
                slotStepMin = 15
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await ReadJson(response);
            Assert.Equal(15, json.GetProperty("slotStepMin").GetInt32());

            using var check = NewDb();
            var settings = check.ShopSettings.Single();
            Assert.Equal(15, settings.SlotStepMin);
            // Every other field round-tripped too - a partial bind would have quietly zeroed these.
            Assert.Equal(10, settings.BufferMin);
            Assert.Equal(45, settings.DefaultAdminBookingDurationMin);
            Assert.Equal(15, settings.GraceMinutesAfterClose);
            Assert.Equal(120, settings.MinAdvanceBookingMinutes);
            Assert.Equal(30, settings.MaxAdvanceBookingDays);
            Assert.Equal(48, settings.RefundCutoffHours);
        }

        [Fact]
        public async Task A_slot_step_that_does_not_divide_an_hour_is_refused()
        {
            AuthenticateAsAdmin();

            var response = await Client.PutAsync("/api/Settings", Body(new
            {
                bufferMin = 0,
                defaultAdminBookingDurationMin = 30,
                graceMinutesAfterClose = 0,
                minAdvanceBookingMinutes = 90,
                maxAdvanceBookingDays = 60,
                refundCutoffHours = 24,
                slotStepMin = 25   // walks off the hour: 09:00, 09:25, 09:50, 10:15...
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var check = NewDb();
            Assert.Equal(30, check.ShopSettings.Single().SlotStepMin);
        }

        [Fact]
        public async Task An_expired_schedule_version_does_not_block_an_hours_change()
        {
            AuthenticateAsAdmin();
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.SetAllShopHours(new TimeOnly(8, 0), new TimeOnly(20, 0));
                // Ended yesterday: it governs no future date, so it can't be violated by hours that only
                // apply from now on - and superseded versions are edited, never deleted, so treating it as
                // a blocker would make the change unfixable.
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-60), ShopClock.Today.AddDays(-1),
                    shifts: new[] { (DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(20, 0)) });
            }

            var response = await Client.PutAsync("/api/Settings/hours", Body(new
            {
                days = Enumerable.Range(0, 7).Select(d => new
                {
                    dayOfWeek = d,
                    openTime = "09:00:00",
                    closeTime = "17:00:00",
                    isClosed = false
                }).ToArray()
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // ---- 2. Grace applies to the shop's close too --------------------------------------------

        [Fact]
        public async Task Grace_after_close_lets_a_staff_booking_run_past_closing_without_an_override()
        {
            AuthenticateAsAdmin();
            int barberId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));
                db.SetShopSetting(s => s.GraceMinutesAfterClose = 30);
            }

            /* 17:15 + 30 = 17:45, which is 15 minutes past both the shift end and the shop's close. With
             * grace applied to only one of them this 409s - and because Req 2 makes "shift ends when the
             * shop closes" the normal case, that would make GraceMinutesAfterClose a setting that breaks
             * the last slot of every day instead of allowing it. */
            var response = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(17, 15)),
                barberId,
                defaultDurationMin = 30,
                phone = "+35679000111",
                fullName = "Grace Client",
                confirmOutsideHours = false
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var check = NewDb();
            var booking = check.Bookings.Single();
            Assert.False(booking.OutsideShopHours);
            Assert.False(booking.OutsideBarberSchedule);
            Assert.Null(booking.OverriddenByUserId);
        }

        /* The exact boundary: a booking that ends on the very last minute grace allows.
         *
         * Separate from the test above, which has grace to spare. This one pins that the comparison is
         * INCLUSIVE (end <= close + grace) on both axes at once - an off-by-one either way would either
         * refuse the slot the setting exists to allow, or record a phantom override on a booking nobody
         * agreed to anything about. */
        [Fact]
        public async Task A_booking_ending_exactly_on_the_grace_limit_records_no_override_on_either_axis()
        {
            AuthenticateAsAdmin();
            int barberId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));   // 09:00-17:30 every day
                db.SetShopSetting(s => s.GraceMinutesAfterClose = 15);     // shop closes 17:30 too
            }

            // 17:15 + 30 = 17:45, which is 17:30 + 15 to the minute, against BOTH the shift and the shop.
            var response = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(17, 15)),
                barberId,
                defaultDurationMin = 30,
                phone = "+35679000444",
                fullName = "Boundary Client",
                confirmOutsideHours = false   // no confirmation offered, because nothing is being overridden
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var check = NewDb();
            var booking = check.Bookings.Single();
            Assert.False(booking.OutsideShopHours);
            Assert.False(booking.OutsideBarberSchedule);
            Assert.Null(booking.OverriddenByUserId);
        }

        /* The far side of that boundary. Grace lengthens an appointment that began while the shop was open;
         * it does not open a slot after closing. 17:30-18:00 ends exactly on the 30-minute limit, so bounding
         * only the END admitted it as an ORDINARY booking - no confirmation asked, no override recorded - for
         * an appointment lying wholly after the shop shut. The staff grid never offered it (it stops one step
         * short of close), which left the extended-hours toggle showing the one out-of-hours slot it didn't
         * also guard. */
        [Fact]
        public async Task A_booking_starting_at_closing_time_is_an_override_even_when_it_ends_inside_grace()
        {
            var adminId = AuthenticateAsAdmin();
            int barberId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));   // 09:00-17:30 every day
                db.SetShopSetting(s => s.GraceMinutesAfterClose = 30);
            }

            object payload(bool confirm) => new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(17, 30)),   // 17:30 + 30 = exactly close + grace
                barberId,
                defaultDurationMin = 30,
                phone = "+35679000555",
                fullName = "After Close",
                confirmOutsideHours = confirm
            };

            var refused = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(payload(false)));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var refusal = await ReadJson(refused);
            // Both, because the shift ends at 17:30 too - neither has room for a booking that STARTS there.
            Assert.True(refusal.GetProperty("outsideShopHours").GetBoolean());
            Assert.True(refusal.GetProperty("outsideWorkingHours").GetBoolean());

            var accepted = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(payload(true)));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            using var check = NewDb();
            var booking = check.Bookings.Single();
            Assert.True(booking.OutsideShopHours);
            Assert.True(booking.OutsideBarberSchedule);
            Assert.Equal(adminId, booking.OverriddenByUserId);
        }

        /* Grace is after CLOSE, and only the shift that runs to closing earns it. A barber finishing at 13:00
         * in a shop open until 17:30 was bookable to 13:30 - half an hour of their afternoon, taken with no
         * confirmation, because the shift check granted grace to the day's last shift unconditionally. */
        [Fact]
        public async Task Grace_does_not_extend_a_shift_that_ends_before_the_shop_closes()
        {
            AuthenticateAsAdmin();
            int barberId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                // Shop shuts at the seeded 17:30; this barber's only shift ends at 13:00.
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30),
                    shifts: new[] { (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(13, 0)) });
                db.SetShopSetting(s => s.GraceMinutesAfterClose = 30);
            }

            var response = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(12, 45)),   // runs to 13:15, past a 13:00 finish
                barberId,
                defaultDurationMin = 30,
                phone = "+35679000666",
                fullName = "Early Finisher",
                confirmOutsideHours = false
            }));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var refusal = await ReadJson(response);
            // The barber's day only. The shop is open until 17:30, so nothing is being asked of the shop.
            Assert.True(refusal.GetProperty("outsideWorkingHours").GetBoolean());
            Assert.False(refusal.GetProperty("outsideShopHours").GetBoolean());
        }

        // ---- 3. Overrides ------------------------------------------------------------------------

        [Fact]
        public async Task Booking_outside_shop_hours_needs_confirmation_and_records_both_flags()
        {
            var adminId = AuthenticateAsAdmin();
            int barberId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));
            }

            object payload(bool confirm) => new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(19, 0)),   // shop shuts 17:30, shift ends 17:30
                barberId,
                defaultDurationMin = 30,
                phone = "+35679000222",
                fullName = "Late Regular",
                confirmOutsideHours = confirm
            };

            var refused = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(payload(false)));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var refusal = await ReadJson(refused);
            // Both boundaries are crossed, so both are reported - the UI copy differs for each case.
            Assert.True(refusal.GetProperty("outsideShopHours").GetBoolean());
            Assert.True(refusal.GetProperty("outsideWorkingHours").GetBoolean());
            Assert.Contains("opening hours", refusal.GetProperty("message").GetString());

            var accepted = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(payload(true)));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            using var check = NewDb();
            var booking = check.Bookings.Single();
            Assert.True(booking.OutsideShopHours);
            Assert.True(booking.OutsideBarberSchedule);
            // Taken from the caller's token, never from the request body.
            Assert.Equal(adminId, booking.OverriddenByUserId);
        }

        [Fact]
        public async Task Booking_outside_only_the_barbers_shift_flags_the_schedule_and_not_the_shop()
        {
            AuthenticateAsAdmin();
            int barberId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                // Shop open 09:00-20:00; this barber only works mornings.
                db.SetAllShopHours(new TimeOnly(9, 0), new TimeOnly(20, 0));
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30),
                    shifts: new[] { (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)) });
            }

            var response = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(15, 0)),   // shop open, barber off
                barberId,
                defaultDurationMin = 30,
                phone = "+35679000333",
                fullName = "Afternoon Favour",
                confirmOutsideHours = true
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var check = NewDb();
            var booking = check.Bookings.Single();
            /* The whole reason there are two flags. A single "overridden" bit would also excuse this
             * booking from a later SHOP-hours check - where being outside really would be an accident
             * nobody agreed to, since no one ever said the shop would open specially for it. */
            Assert.False(booking.OutsideShopHours);
            Assert.True(booking.OutsideBarberSchedule);
        }

        [Fact]
        public async Task A_deliberate_out_of_hours_booking_is_not_reported_as_stranded_by_a_schedule_edit()
        {
            AuthenticateAsAdmin();
            int barberId, versionId, deliberateId, ordinaryId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.SetAllShopHours(new TimeOnly(8, 0), new TimeOnly(20, 0));
                versionId = db.AddSchedule(barberId, ShopClock.Today.AddDays(-30),
                    shifts: Enumerable.Range(0, 7)
                        .Select(d => ((DayOfWeek)d, new TimeOnly(9, 0), new TimeOnly(18, 0)))
                        .ToArray()).Id;

                // Placed outside the barber's shift on purpose.
                var deliberate = db.AddBooking(barberId, monday.ToDateTime(new TimeOnly(18, 30)));
                deliberate.OutsideBarberSchedule = true;
                // An ordinary booking that the narrower hours below really will strand.
                var ordinary = db.AddBooking(barberId, monday.ToDateTime(new TimeOnly(17, 0)));
                db.SaveChanges();
                deliberateId = deliberate.Id;
                ordinaryId = ordinary.Id;
            }

            // Pull the day in to 16:00: both bookings now sit outside the shifts.
            var response = await Client.PutAsync($"/api/Schedules/version/{versionId}", Body(new
            {
                shifts = Enumerable.Range(0, 7)
                    .Select(d => new { dayOfWeek = d, startTime = "09:00:00", endTime = "16:00:00" }).ToArray(),
                confirmOrphaned = false
            }));

            // The conflict modal lists only the accident, never the arrangement.
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var json = await ReadJson(response);
            var listed = json.GetProperty("affected").EnumerateArray()
                .Select(b => b.GetProperty("id").GetInt32()).ToList();
            Assert.Contains(ordinaryId, listed);
            Assert.DoesNotContain(deliberateId, listed);

            // And confirming flags only that one for review.
            var confirmed = await Client.PutAsync($"/api/Schedules/version/{versionId}", Body(new
            {
                shifts = Enumerable.Range(0, 7)
                    .Select(d => new { dayOfWeek = d, startTime = "09:00:00", endTime = "16:00:00" }).ToArray(),
                confirmOrphaned = true
            }));
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

            using var check = NewDb();
            Assert.True(check.Bookings.Single(b => b.Id == ordinaryId).NeedsReview);
            Assert.False(check.Bookings.Single(b => b.Id == deliberateId).NeedsReview);
        }

        [Fact]
        public async Task Rescheduling_back_into_normal_hours_clears_the_override()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));
                var booking = db.AddBooking(barberId, monday.ToDateTime(new TimeOnly(19, 0)));
                booking.OutsideShopHours = true;
                booking.OutsideBarberSchedule = true;
                db.SaveChanges();
                bookingId = booking.Id;
            }

            var response = await Client.PatchAsync($"/api/Bookings/update-booking/{bookingId}", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(10, 0)),
                confirmOutsideHours = false
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var check = NewDb();
            var moved = check.Bookings.Single(b => b.Id == bookingId);
            /* Recomputed, not carried. Left as-is, a booking moved back into an ordinary slot would stay
             * exempt from every future conflict sweep for the rest of its life - the same false negative
             * the flags exist to prevent, only inverted. */
            Assert.False(moved.OutsideShopHours);
            Assert.False(moved.OutsideBarberSchedule);
            Assert.Null(moved.OverriddenByUserId);
        }

        [Fact]
        public async Task Rescheduling_inside_shop_hours_but_still_off_shift_clears_only_the_shop_flag()
        {
            AuthenticateAsAdmin();
            int barberId, bookingId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                // Shop 09:00-20:00; this barber only works mornings.
                db.SetAllShopHours(new TimeOnly(9, 0), new TimeOnly(20, 0));
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30),
                    shifts: new[] { (DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)) });
                // Currently outside BOTH: 21:00 is past closing and nowhere near the morning shift.
                var booking = db.AddBooking(barberId, monday.ToDateTime(new TimeOnly(21, 0)));
                booking.OutsideShopHours = true;
                booking.OutsideBarberSchedule = true;
                db.SaveChanges();
                bookingId = booking.Id;
            }

            // Move it to 15:00: the shop is open, the barber still isn't on.
            var response = await Client.PatchAsync($"/api/Bookings/update-booking/{bookingId}", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(15, 0)),
                confirmOutsideHours = true
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var check = NewDb();
            var moved = check.Bookings.Single(b => b.Id == bookingId);
            /* The two flags are cleared INDEPENDENTLY, each against its own boundary. Recomputing them as a
             * pair - or carrying whichever was set - would leave this booking excused from a future
             * shop-hours check it no longer has any agreement behind. */
            Assert.False(moved.OutsideShopHours);
            Assert.True(moved.OutsideBarberSchedule);
            // Something is still overridden, so the authoriser is still recorded.
            Assert.NotNull(moved.OverriddenByUserId);
        }

        // ---- 4. Customers get no override --------------------------------------------------------

        [Fact]
        public async Task A_customer_cannot_book_on_a_day_the_shop_is_closed()
        {
            int barberId, serviceId;
            var sunday = TestData.NextWeekday(DayOfWeek.Sunday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                // The barber is still rostered on Sunday (a schedule that predates the rule); the shop is
                // shut. This is exactly the case the customer-side check exists for - the shifts-within-
                // hours invariant would normally make it unreachable.
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));
                db.SetShopHours(DayOfWeek.Sunday, isClosed: true);
                serviceId = db.AddService(durationMin: 30).Id;
            }

            var response = await Client.PostAsync("/api/Bookings/create-pending", Body(new
            {
                startDateTime = sunday.ToDateTime(new TimeOnly(11, 0)),
                barberId,
                servicesIds = new[] { serviceId }
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var check = NewDb();
            Assert.Empty(check.Bookings);
        }

        /* The customer half of the closing-time boundary. Staff get a confirmation for it (section 2); a
         * customer gets a flat refusal, and it reaches EvaluateHoursAsync down a different branch with no
         * override in front of it - so the rule has to be proven on both. 17:30 + 30 lands exactly on
         * close + grace, so bounding only the END would have admitted this as an ordinary customer booking
         * for an appointment lying wholly after the shop shut. */
        [Fact]
        public async Task A_customer_cannot_book_a_slot_that_starts_at_closing_time()
        {
            int barberId, serviceId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));   // 09:00-17:30, and the shop shuts 17:30
                db.SetShopSetting(s => s.GraceMinutesAfterClose = 30);
                serviceId = db.AddService(durationMin: 30).Id;
            }

            var response = await Client.PostAsync("/api/Bookings/create-pending", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(17, 30)),
                barberId,
                servicesIds = new[] { serviceId }
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var check = NewDb();
            Assert.Empty(check.Bookings);
        }

        // ---- 5. The picker payload ----------------------------------------------------------------

        [Fact]
        public async Task The_picker_payload_carries_the_slot_step_and_the_seven_days_of_hours()
        {
            using (var db = NewDb())
            {
                db.SetShopSetting(s => s.SlotStepMin = 15);
                db.SetShopHours(DayOfWeek.Sunday, isClosed: true);
            }

            var response = await Client.GetAsync("/api/Barbers/barbers-with-bookings");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await ReadJson(response);
            Assert.Equal(15, json.GetProperty("slotStepMin").GetInt32());
            var hours = json.GetProperty("shopHours");
            Assert.Equal(7, hours.GetArrayLength());
            var sunday = hours.EnumerateArray().Single(h => h.GetProperty("dayOfWeek").GetInt32() == 0);
            Assert.True(sunday.GetProperty("isClosed").GetBoolean());
        }
    }
}
