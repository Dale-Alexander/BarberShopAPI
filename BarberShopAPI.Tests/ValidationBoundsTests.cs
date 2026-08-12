using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace BarberShopAPI.Tests
{
    /* The bounds on policy values, proven at BOTH levels they are enforced at.
     *
     * The endpoints validate with data annotations, which [ApiController] turns into a 400 before the action
     * runs - that half is exercised all over the suite already, every time a test posts a valid body. What is
     * NOT otherwise covered is the database backstop underneath it, and that is the half that matters for
     * writers which never pass through a view model: seeds, migrations, the fixture's own raw UPDATE, and any
     * admin tooling written later. A check constraint that silently stopped being created would leave no
     * trace anywhere else in this suite. */
    public class ValidationBoundsTests : IntegrationTestBase
    {
        public ValidationBoundsTests(DatabaseFixture fixture) : base(fixture) { }

        private void AuthenticateAsAdmin()
        {
            using var db = NewDb();
            var admin = db.AddUser(Role.ADMIN);
            Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
        }

        // ---- The database backstop ----------------------------------------------------------------

        /* Writes the entity directly, deliberately bypassing UpdateShopSettingsViewModel's [Range] - which is
         * exactly what a seed or a future admin tool would do. Grace stands in for all seven columns; they are
         * declared together in OnModelCreating, so one proves the mechanism is live. */
        [Fact]
        public void Shop_settings_outside_their_range_are_refused_by_the_database()
        {
            using var db = NewDb();

            Assert.Throws<DbUpdateException>(() =>
                db.SetShopSetting(s => s.GraceMinutesAfterClose = 500));
        }

        [Fact]
        public void A_slot_step_that_does_not_divide_the_hour_is_refused_by_the_database()
        {
            using var db = NewDb();

            // 25 would walk the picker's grid off the hour (09:00, 09:25, 09:50, 10:15...), which is why this
            // column is an allowed SET and not a range.
            Assert.Throws<DbUpdateException>(() => db.SetShopSetting(s => s.SlotStepMin = 25));
        }

        /* The exemption, and the reason CK_ShopHours_OpenBeforeClose is not a plain OpenTime < CloseTime.
         *
         * A closed day's times are ignored by every reader and kept only so reopening restores what was there
         * before, so they are allowed to be junk - ShopHoursDayViewModel.Validate skips its own check for the
         * same reason. Constrain them and a day whose stored times are inverted becomes impossible to CLOSE,
         * which is the very state an admin closes a day to get out of. */
        [Fact]
        public void A_day_can_still_be_closed_when_its_stored_times_are_inverted()
        {
            using var db = NewDb();

            db.SetShopHours(DayOfWeek.Monday,
                open: new TimeOnly(18, 0), close: new TimeOnly(9, 0), isClosed: true);

            var row = db.ShopHours.Single(h => h.DayOfWeek == DayOfWeek.Monday);
            Assert.True(row.IsClosed);
            Assert.Equal(new TimeOnly(18, 0), row.OpenTime);
        }

        [Fact]
        public void An_open_day_whose_times_are_inverted_is_refused_by_the_database()
        {
            using var db = NewDb();

            Assert.Throws<DbUpdateException>(() => db.SetShopHours(DayOfWeek.Monday,
                open: new TimeOnly(18, 0), close: new TimeOnly(9, 0), isClosed: false));
        }

        // ---- The endpoint bound -------------------------------------------------------------------

        /* Until [Range] went on DefaultDurationMin the controller only rejected <= 0, so a four-figure
         * duration sailed past and was refused by the working-hours check instead - reported to the admin as
         * "outside the barber's working hours", which is a confusing way to say "that is not a length". */
        [Theory]
        [InlineData(4)]
        [InlineData(241)]
        public async Task An_admin_booking_duration_outside_5_to_240_is_refused(int durationMin)
        {
            AuthenticateAsAdmin();
            int barberId;
            var monday = TestData.NextWeekday(DayOfWeek.Monday);
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                barberId = barber.Id;
                db.AddSchedule(barberId, ShopClock.Today.AddDays(-30));
            }

            var response = await Client.PostAsync("/api/Bookings/create-admin-booking", Body(new
            {
                startDateTime = monday.ToDateTime(new TimeOnly(10, 0)),
                barberId,
                defaultDurationMin = durationMin,
                phone = "+35679000777",
                fullName = "Odd Duration",
                confirmOutsideHours = false
            }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var check = NewDb();
            Assert.Empty(check.Bookings);
        }
    }
}
