using BarberShopAPI.Data;
using System.Security.Claims;
namespace BarberShopAPI.Middleware

{
    public class TokenVersionMiddleware
    {
        private readonly RequestDelegate _next;
        /* this represents the netx thin in the pipeline.
         * If you dont call _next(context), the request will stop here
         _next() is of type RequestDelegate which expects a an HttpContext 
        argument. What _next(context) does: It passes the current HTTP request 
        along the pipeline along the pipeline to the next middleware*/
        public TokenVersionMiddleware(RequestDelegate next)
        {
            _next = next;
        }
        public async Task InvokeAsync(HttpContext context, BarberShopContext db)
            /* You might be asking why we do async Task if Task represents
             * an asyncrhonous operation. Task is just an object that 
             represents some work that may complete in the future. By itself, it
            does not make your method asyncrhonous. What if you removed
            Task. That is bad because if you do async void, void methods
            cant be awaited. The framework exects InvokeAsync to return a Task
            so it knows when the middleware has finished. With async void, the
            method returns immediately before the async work finishes. Task
            tells ASP.NET Core wait for this middleware to finish before moving
            to the next one*/
        {
            // Logout must be reachable even with an already-invalid token (e.g. the user just
            // reset their password, which bumps TokenVersion and makes their still-present cookie
            // stale). Without this bypass the stale token would be 401'd here BEFORE Logout runs,
            // so the cookie would never get cleared. Logout itself decides what to revoke.
            if (context.Request.Path.StartsWithSegments("/api/auth/logout"))
            {
                await _next(context);
                return;
            }
            if (!context.User.Identity?.IsAuthenticated ?? true)
                /* What the above if statement does: IT asks these things:
                 *If the request: Has no JWT, Has an invalid JWT, is calling
                 *a public endpoint THEN Skip the version token check
                 *Why? Login, Public endpoints dont need this logic, without
                 *this check your app would 401 everything
                 */
            {
                await _next(context);
                return;
            }
            var userIdClaim = context.User.FindFirst("id");
            var tokenVersionClaim = context.User.FindFirst("tokenVersion");
            if(userIdClaim == null || tokenVersionClaim == null)
            {
                /* Missing claims = invalid token. Why this matters:
                 *Someone could forge a token, Someone could use an old token format,
                 *Someone could remove claims. IF so then reject
                 */
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var userId = int.Parse(userIdClaim.Value);
            var tokenVersion = int.Parse(tokenVersionClaim.Value);
            //Claims are strings. Your Db values are ints
            var user = await db.Users.FindAsync(userId);
            if(user == null || user.TokenVersion != tokenVersion)
            {
                //Now we compare the JWT tokenVersion to the Database TokenVersion
                /* If user is deleted that means the token still exists
                 but we dont want the user to advance on his operation if he
                is deleted. IF there is a otken mismatch, that means that either
                the user logged out, password reset or other things. Then 
                we have to restrict access*/
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await _next(context);
            /* the above line means the JWT is valid, user exists, Token hasnt
             * been revoked*/
        }
    }
}
