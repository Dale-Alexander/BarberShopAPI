using BarberShopAPI.Common;
using BarberShopAPI.Models.Enums;
using BarberShopAPI.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;

namespace BarberShopAPI.Tests
{
    /* An owner who also cuts hair is ONE account: role ADMIN (what they may do) plus a Barber row (a chair
     * customers can book). The two are independent, and the team screen is where they collide.
     *
     * Both cases below used to take the shop off its owner:
     *   - deactivating their chair bumped TokenVersion, dumping them on the login screen mid-click;
     *   - reactivating it ran the revive-by-email branch, which DEMOTES to BARBER and overwrites the
     *     password with whatever was typed in the modal. That one was only recoverable from the database.
     *
     * Neither is about permissions - closing a chair revokes no privilege, because the privilege was never
     * attached to the chair. */
    public class AdminBarberAccountTests : IntegrationTestBase
    {
        public AdminBarberAccountTests(DatabaseFixture fixture) : base(fixture) { }

        /// <summary>An ADMIN user that also owns a barber row + schedule, authenticated as themselves.</summary>
        private (int UserId, int BarberId) AuthenticateAsAdminBarber()
        {
            using var db = NewDb();
            var user = db.AddUser(Role.ADMIN);
            var barber = new BarberShopAPI.Models.Barber { UserId = user.Id, isActive = true, AcceptsNewBookings = true };
            db.Barbers.Add(barber);
            db.SaveChanges();
            db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
            Client.Authenticate(user.Id, Role.ADMIN, tokenVersion: 0);
            return (user.Id, barber.Id);
        }

        [Fact]
        public async Task Deactivating_your_own_chair_as_an_admin_does_not_sign_you_out()
        {
            var (userId, barberId) = AuthenticateAsAdminBarber();
            int before;
            using (var db = NewDb()) before = (await db.Users.SingleAsync(u => u.Id == userId)).TokenVersion ?? 0;

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var user = await assertDb.Users.SingleAsync(u => u.Id == userId);
            // The bump is what kills the live session; leaving it alone is what keeps them signed in.
            Assert.Equal(before, user.TokenVersion ?? 0);
            Assert.Equal(Role.ADMIN, user.Role);
            // The chair really did close - this isn't a no-op.
            Assert.False((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);
        }

        /* The role tested is the DEACTIVATED person's, not the caller's, so it holds when one admin closes
         * another admin's chair too. They're peers: neither outranks the other, and closing a chair was
         * never a way to remove someone's admin rights. */
        [Fact]
        public async Task One_admin_deactivating_another_admins_chair_does_not_sign_that_admin_out()
        {
            AuthenticateAsAdminBarber();   // the caller
            int otherUserId, otherBarberId;
            using (var db = NewDb())
            {
                var other = db.AddUser(Role.ADMIN);
                var barber = new BarberShopAPI.Models.Barber { UserId = other.Id, isActive = true, AcceptsNewBookings = true };
                db.Barbers.Add(barber);
                db.SaveChanges();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                otherUserId = other.Id;
                otherBarberId = barber.Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{otherBarberId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var other2 = await assertDb.Users.SingleAsync(u => u.Id == otherUserId);
            Assert.Equal(0, other2.TokenVersion ?? 0);
            Assert.Equal(Role.ADMIN, other2.Role);
        }

        // The counterweight: a plain barber MUST still be signed out, or a departed employee keeps
        // reaching the dashboard with a token that's still valid.
        [Fact]
        public async Task Deactivating_a_plain_barber_still_signs_them_out()
        {
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
            }
            int barberUserId, barberId;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                barberUserId = barber.UserId;
                barberId = barber.Id;
            }

            var response = await Client.DeleteAsync($"/api/barbers/delete/{barberId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            Assert.Equal(1, (await assertDb.Users.SingleAsync(u => u.Id == barberUserId)).TokenVersion ?? 0);
        }

        /* Reactivation goes through CreateBarber's revive-by-email branch, which is built for a returning
         * EMPLOYEE: it hands them a fresh password and forces them to log in with it. Run against the
         * owner's account that same code demoted them to BARBER and overwrote their password - losing them
         * the shop, from a screen that only claimed to reopen a chair. */
        [Fact]
        public async Task Reactivating_an_admins_chair_keeps_their_role_password_and_session()
        {
            var (userId, barberId) = AuthenticateAsAdminBarber();
            string email, originalHash;
            using (var db = NewDb())
            {
                var u = await db.Users.SingleAsync(x => x.Id == userId);
                u.Email = "owner@example.test";
                u.Password = BCrypt.Net.BCrypt.HashPassword("TheOwnersRealPassword1");
                await db.SaveChangesAsync();
                email = u.Email;
                originalHash = u.Password;
            }
            Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"/api/barbers/delete/{barberId}")).StatusCode);

            // Reactivate exactly as the team screen does: the create-barber form, with the existing email.
            using var form = new MultipartFormDataContent
            {
                { new StringContent("Joe Borg"), "FullName" },
                { new StringContent(email), "Email" },
                { new StringContent("SomethingTypedInTheModal9"), "Password" },
            };
            var response = await Client.PostAsync("/api/barbers/create-barber", form);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var assertDb = NewDb();
            var user = await assertDb.Users.SingleAsync(u => u.Id == userId);
            Assert.Equal(Role.ADMIN, user.Role);              // not demoted to BARBER
            Assert.Equal(originalHash, user.Password);        // password untouched
            Assert.Equal(0, user.TokenVersion ?? 0);          // still signed in
            Assert.True((await assertDb.Barbers.SingleAsync(b => b.Id == barberId)).isActive);  // chair reopened
        }

        // And the employee case it was written for still behaves: fresh password, forced re-login.
        [Fact]
        public async Task Reactivating_a_plain_barber_still_resets_their_password_and_session()
        {
            using (var db = NewDb())
            {
                var admin = db.AddUser(Role.ADMIN);
                Client.Authenticate(admin.Id, Role.ADMIN, tokenVersion: 0);
            }
            int barberUserId, barberId;
            string email, originalHash;
            using (var db = NewDb())
            {
                var barber = db.AddBarber();
                db.AddSchedule(barber.Id, ShopClock.Today.AddDays(-30));
                var u = await db.Users.SingleAsync(x => x.Id == barber.UserId);
                u.Password = BCrypt.Net.BCrypt.HashPassword("OldEmployeePassword1");
                await db.SaveChangesAsync();
                barberUserId = barber.UserId;
                barberId = barber.Id;
                email = u.Email!;
                originalHash = u.Password;
            }
            Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"/api/barbers/delete/{barberId}")).StatusCode);

            using var form = new MultipartFormDataContent
            {
                { new StringContent("Back Again"), "FullName" },
                { new StringContent(email), "Email" },
                { new StringContent("BrandNewPassword9"), "Password" },
            };
            Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync("/api/barbers/create-barber", form)).StatusCode);

            using var assertDb = NewDb();
            var user = await assertDb.Users.SingleAsync(u => u.Id == barberUserId);
            Assert.Equal(Role.BARBER, user.Role);
            Assert.NotEqual(originalHash, user.Password);
            Assert.True(BCrypt.Net.BCrypt.Verify("BrandNewPassword9", user.Password));
        }
    }
}
