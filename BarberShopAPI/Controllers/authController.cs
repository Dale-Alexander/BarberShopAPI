using BarberShopAPI.Data;
using BarberShopAPI.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Hangfire;
using BarberShopAPI.Services;

namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class authController : ControllerBase
    {
        private readonly BarberShopContext _context;
        private readonly IEmailService _emailService;
        private readonly IWebHostEnvironment _env;
        public authController(BarberShopContext context, IEmailService emailService, IWebHostEnvironment env)
        {
            _context = context;
            _emailService = emailService;
            _env = env;
        }
        [HttpPost("login")]
        [EnableRateLimiting("login")]
        public async Task <IActionResult> Login([FromBody] LoginEmailPasswordViewModel request)
        {
            try
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
                // A single generic 401 for every pre-token failure - unknown email, a non-staff role
                // (only admins/barbers have passwords), or a missing/incorrect password. Returning the
                // same status + message for all of them means the response never reveals whether an
                // email has an account or which check failed, which would let an attacker enumerate
                // valid emails. The short-circuit order also guarantees Verify only runs with a
                // non-empty submitted password.
                if (user == null
                    || (user.Role.ToString() != "ADMIN" && user.Role.ToString() != "BARBER")
                    || string.IsNullOrEmpty(request.Password)
                    || !BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
                {
                    return Unauthorized(new { message = "Invalid email or password" });
                }
                //Create JWT

                var TokenHandler = new JwtSecurityTokenHandler();
                /* Think of "TokenHandler" as the JWT factry + validator. It knows 
                 * how to create JWTs, serialize them into strings and validate them
                 later. It does not know your secret yet.*/
                
                var key = Encoding.ASCII.GetBytes(Environment.GetEnvironmentVariable("JWT_SECRET"));
                
                /* _config["JwtSecret"] is a string from appsettings.json
                 *JWT signing requires bytes, not strings. This is the private key used
                 *to sign the token and verify the token in the future
                 */
                var tokenDescriptor = new SecurityTokenDescriptor
                /* Token descriptor describes what the token should look like
                 *Subject: Wraps the claims into a ClaimsIdentity. This becomes
                 *HttpContext.User after validation
                 *Expires: Token expiry date
                 *SigningCredentials: This is where the securty happens:
                 *SymmetricSecurtiyKey(key): Same key signs and verifies,
                 *Fast and Standard For APIs
                 *HmacSha256Signature: Strong Hashing algorithm,
                 *Industry default
                 */
                {
                    Subject = new ClaimsIdentity(new[]
                    {
                    new Claim("id", user.Id.ToString()),
                    new Claim(ClaimTypes.Role, user.Role.ToString()),
                    new Claim("tokenVersion", user.TokenVersion.ToString())
                }),
                    /* 
                     * HttpContext.User represents the "currently logged in user"
                     object that has the claims with it. 
                    HttpContext.User is actually set automatically by the JWT
                    middleware. HttpContext.User is set when the frontend makes
                    a request to a protected endpoint like /me, JWT
                    middleware reads the cookie and validates the JWT and the
                    middleware also creates a ClaimsPrincipal which contains the 
                    above claims. Then the middleware sets 
                    HttpContext.User = ClaimsPrincipal. The middleware does not
                    run during /login, because the user hasnt sent the JWT yet.
                    It only runs on subsequent requests where the client 
                    includes the JWT cookie and that is where HTTPContext.User is
                    set*/
                    Expires = DateTime.UtcNow.AddDays(1),//stays logged in for 1 day
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                };

                /* Claims - the payload(identity data)
                 *Claims are statements about the user 
                 *Inside the JWT, it becomes: {
                  "id": "5",
                  "email": "admin@site.com",
                  "role": "ADMIN"
    }
                 */
                var token = TokenHandler.CreateToken(tokenDescriptor);
                /* The above line takes your descriptor and generates the Header,
                 * Payload(claims + expiration),Signature
                */
                var jwt = TokenHandler.WriteToken(token);
                /* The above line serializes the token meaning it converts it 
                 * from an object to a string. This string is what gets sent
                 to the cookie, gets sent on every request and gets validated by 
                middleware.
                What the Final JWT contains:
                HEADER -> Algorithm + token type,
                PAYLOAD -> id, email, role, exp
                SIGNATURE -> signed with your secret*/

                Response.Cookies.Append("jwt", jwt, new CookieOptions
                {
                    HttpOnly = true,
                    // Secure = only send the cookie over HTTPS. Dev runs on plain
                    // http://localhost so it MUST be false there; production runs on HTTPS
                    // so it MUST be true. Driven off the environment (ASPNETCORE_ENVIRONMENT)
                    // so we never accidentally ship the insecure setting to production.
                    Secure = !_env.IsDevelopment(),//automatically changed
                    // SameSite=Lax is correct AS LONG AS the frontend and this API are the
                    // SAME SITE (same registrable domain) -- e.g. app.yourshop.com +
                    // api.yourshop.com via subdomains, which is the recommended production
                    // setup (see the CORS comment in Program.cs). If you instead deploy the
                    // frontend and backend on DIFFERENT domains (e.g. myapp.vercel.app +
                    // myapi.azurewebsites.net) the browser treats them as cross-site and will
                    // NOT send this cookie under Lax -- you would have to change this to
                    // SameSiteMode.None, which additionally REQUIRES Secure=true. Prefer the
                    // subdomain setup so you can keep Lax and avoid cross-site cookie blocking.
                    //NOTE: The frontend and backend on production are going to be completely different
                    //but they will be reated as the same origin because i am using a proxy.
                    //I am not using CORS because it is redundant since both origins are the same and not corss-origin
                    //The current set up still works
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTime.UtcNow.AddDays(1)
                });
                Console.WriteLine("Login successful");
                // Barbers navigate to their own bookings page at /admin/team/{barberId},
                // which is keyed by the Barber row's Id (NOT the User id). Surface it here
                // so the frontend can build that route; null for admins, who don't need it.
                int? barberId = user.Role.ToString() == "BARBER"
                    ? await _context.Barbers.Where(b => b.UserId == user.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync()
                    : null;
                return Ok(new
                {
                    user.Id,
                    user.Email,
                    user.Role,
                    BarberId = barberId,
                    message = "Login Successful"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Login exception: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }
        [Authorize]
        //.UseAuthorization takes care of this
        [HttpGet("me")]
        /* This endpoint answers the question: "Who is currently logged in " 
         It is meant for auto-login*/
        public async Task<IActionResult> Me()
        {
            /*var jwt = Request.Cookies["jwt"];
            if (string.IsNullOrEmpty(jwt)) return Unauthorized();
            /* The above 2 lines reads the HttpOnly Cookie named jwt
 * If no cookie exists -> user is not authorized and returns 401 Unauthorized
            var TokenHandler = new JwtSecurityTokenHandler();
            /* "TokenHandler" knows how to Validate JWTs and Parse Claims
             * It is the same objkect used during login
            var key = Encoding.ASCII.GetBytes(_config["JwtSecret"]);
            /* uses the same secret used to sign the token. Required
             * to verify authenticity.
            try
            {
                TokenHandler.ValidateToken(jwt, new TokenValidationParameters
                //validates the token
                {
                    ValidateIssuer = false,//you are not using issuer yet
                    ValidateAudience = false,//you are not using audience yet
                    ValidateIssuerSigningKey = true,//Required - validates the signature
                    IssuerSigningKey = new SymmetricSecurityKey(key),//the secret key
                    ClockSkew = TimeSpan.Zero/* No "grace period" after expiration,
                                              * token expires exactly on time
                }, out SecurityToken validatedToken);
                /* What the above does: Verifies the token signature, verifies
                 * expiration, ensures token was signed with my secret
                var jwtToken = (JwtSecurityToken)validatedToken;
                /* the above converts generic SecurityYoken into a JWT-sepcific token
                 * allows access to claims
                 
             The above basically does what the middleware does*/

            var userIdClaim = User.FindFirst("id")?.Value;
            //"id" was used when you added the Claims in /login. Go to /login controlelr
            if (userIdClaim == null) return Unauthorized();
            //the above reads the id claim you added during login. the id was signed
            //so it is trusted
            var userId = int.Parse(userIdClaim);
                var user = await _context.Users.FindAsync(userId);
                if (user == null) return Unauthorized();
                /* Load the user from the database. Why this step?
                 * User may have need deleted, role may have changed,account
                 may have been disabeld*/
                // Same as login: barbers need their Barber row id to reach /admin/team/{barberId}.
                int? barberId = user.Role.ToString() == "BARBER"
                    ? await _context.Barbers.Where(b => b.UserId == user.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync()
                    : null;
                return Ok(new { user.Id, user.Email, user.Role, BarberId = barberId });

            /* [Authorize] will automatically return 401 if the token is missing or invalid
             *This is done so by the challenge used in Program.cs
             */
        }
        // Deliberately NOT [Authorize], and TokenVersionMiddleware skips this route: logout must
        // ALWAYS clear the cookie and succeed, even when the caller's token is already invalid
        // (e.g. their password was just reset, which bumps TokenVersion and leaves the still-present
        // cookie stale). If the JWT authentication middleware managed to populate a CURRENT, valid
        // session we also bump TokenVersion to revoke that JWT server-side (in case a copy was
        // stolen); if the token is stale or absent there's nothing to revoke - it's already dead -
        // so we just clear the cookie.
        [HttpGet("logout")]
        public async Task<IActionResult> Logout()
        {
            if (int.TryParse(User.FindFirst("id")?.Value, out var userId)
                && int.TryParse(User.FindFirst("tokenVersion")?.Value, out var tokenVersion))
            {
                var user = await _context.Users.FindAsync(userId);
                if (user != null && user.TokenVersion == tokenVersion)
                {
                    user.TokenVersion = (user.TokenVersion ?? 0) + 1;
                    await _context.SaveChangesAsync();
                }
            }
            Response.Cookies.Delete("jwt");
            return Ok(new { message = "Logged out successfully" });
        }

        /* Forgot-password step 1: request a reset link. Deliberately has no [Authorize] -
         * whoever calls this has, by definition, lost access to their session. Security
         * doesn't come from being logged in, it comes from possessing the token that's
         * about to be emailed: an attacker who only knows the target's email address has
         * nothing to submit to ResetPassword below, since the raw token never touches
         * this response, only the user's actual inbox (see EmailService.
         * sendPasswordResetEmailAsync). This replaced an earlier version of this endpoint
         * that changed the password directly from just an email with no verification at
         * all - a real account-takeover hole. */
        [HttpPost("forgot-password")]
        public async Task<IActionResult> RequestPasswordReset([FromBody] RequestPasswordResetViewModel request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            try
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
                if (user != null && (user.Role.ToString() == "ADMIN" || user.Role.ToString() == "BARBER"))
                {
                    // Per-email send throttle: don't send another reset email to this address if one went
                    // out within the last ResetEmailThrottleMinutes. This blunts inbox-bombing a victim
                    // regardless of the caller's IP - unlike a per-IP limit, which an attacker sidesteps by
                    // rotating IPs and which can block legit users behind a shared IP. "Issued at" is derived
                    // from the 30-minute token expiry (expiry = issued + 30), so no extra column is needed.
                    const int TokenLifetimeMinutes = 30;
                    const int ResetEmailThrottleMinutes = 2;
                    var sentWithinThrottle = user.PasswordResetTokenExpiresAt != null
                        && user.PasswordResetTokenExpiresAt > DateTime.UtcNow.AddMinutes(TokenLifetimeMinutes - ResetEmailThrottleMinutes);

                    if (!sentWithinThrottle)
                    {
                        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                        user.PasswordResetTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
                        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(TokenLifetimeMinutes);
                        await _context.SaveChangesAsync();

                        BackgroundJob.Enqueue<IEmailService>(service => service.sendPasswordResetEmailAsync(user.Id, rawToken));
                    }
                }

                // Always return the same generic message, whether or not the email
                // exists or belongs to an admin/barber - avoids leaking which emails
                // have accounts.
                return Ok(new { message = "If an account with that email exists, a password reset link has been sent." });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RequestPasswordReset exception: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        /* Forgot-password step 2: actually change the password, using the token from the
         * emailed link instead of a session. Also has no [Authorize] for the same reason
         * as RequestPasswordReset above - the token itself is the proof of identity. */
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordViewModel request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            try
            {
                if (request.NewPassword != request.ConfirmNewPassword)
                {
                    return BadRequest(new { message = "Both inputs need to be the same" });
                }

                // Hash whatever token was submitted and look up whoever has that exact
                // hash on file (never trust a user id/email from the request body here -
                // the token is the only thing that ties this request to an account).
                var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
                var user = await _context.Users.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == tokenHash);

                if (user == null || user.PasswordResetTokenExpiresAt == null || user.PasswordResetTokenExpiresAt < DateTime.UtcNow)
                {
                    return BadRequest(new { message = "This reset link is invalid or has expired" });
                }

                user.Password = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
                // Nulling both fields makes the token single-use: it can't be replayed
                // once a reset has gone through, and the next PasswordResetTokenHash
                // lookup above will simply find no match for it.
                user.PasswordResetTokenHash = null;
                user.PasswordResetTokenExpiresAt = null;
                // Bumping TokenVersion invalidates every JWT issued under the old
                // password (TokenVersionMiddleware compares this against the token's
                // claim on every authenticated request) - same reasoning as
                // BarbersController's deactivate/reactivate flow. No new JWT is issued
                // here on purpose: the user goes back to /login and proves they actually
                // know the new password, rather than this endpoint silently trusting
                // that the reset request itself was legitimate.
                user.TokenVersion = (user.TokenVersion ?? 0) + 1;
                await _context.SaveChangesAsync();

                return Ok(new { message = "Password updated successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ResetPassword exception: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }
}
