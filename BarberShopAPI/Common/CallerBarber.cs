using BarberShopAPI.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BarberShopAPI.Common
{
    /* "Which barber is calling, and may they touch this one?" - the ownership question every endpoint open
     * to ADMIN,BARBER has to answer before it uses a {barberId} that came from the URL or a request body.
     *
     * This lived as a private copy in BookingsController and another in DatesController, each carrying a
     * comment telling the next reader to keep it in step with the other by hand. SchedulesController needs
     * the same check now for the barber's read-only schedule, and a third hand-synced copy is one too many:
     * the isActive predicate below is a security decision, and it should be decided once.
     *
     * The isActive predicate is defence in depth. A deactivated barber shouldn't reach these endpoints at
     * all - Login refuses to issue them a token and DeleteBarber's TokenVersion bump kills any they still
     * hold - but that leans entirely on login being the only place a token is minted. It costs nothing (one
     * more predicate on a query that already runs) and keeps the guard honest if a refresh-token or SSO
     * path is ever added. */
    public static class CallerBarber
    {
        /* The caller's own Barber.Id when they're a BARBER, or null if they have no barber row - or if that
         * row is deactivated. Returning null makes ownership checks fail closed, since a real BarberId can
         * never equal null.
         *
         * ADMIN callers are handled with User.IsInRole and must never rely on this: an admin has no barber
         * row, so it returns null for them too. Use CanAccessBarberAsync when the question is "may they",
         * and this when the question is "which chair is theirs". */
        public static async Task<int?> CallerBarberIdAsync(this ClaimsPrincipal user, BarberShopContext context)
        {
            var callerUserId = int.Parse(user.FindFirst("id")?.Value ?? "0");
            return await context.Barbers
                .Where(b => b.UserId == callerUserId && b.isActive)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync();
        }

        /* May this caller act on {barberId}? Admins may act on any barber; a barber only on the id tied to
         * their own user. Without this, any logged-in barber could read another barber's clients, phone
         * numbers or hours just by editing the id in the URL (IDOR). */
        public static async Task<bool> CanAccessBarberAsync(this ClaimsPrincipal user, BarberShopContext context, int barberId)
        {
            if (user.IsInRole("ADMIN")) return true;
            return barberId == await user.CallerBarberIdAsync(context);
        }
    }
}
