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
using Microsoft.AspNetCore.Authorization;

namespace BarberShopAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class authController : ControllerBase
    {
        private readonly BarberShopContext _context;
        public authController(BarberShopContext context)
        {
            _context = context;
        }
        [HttpPost("login")]
        public async Task <IActionResult> Login([FromBody] LoginEmailPasswordViewModel request)
        {
            try
            {
                Console.WriteLine(request.Email);
                Console.WriteLine(request.Password);
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
                if (user == null)
                {
                    return NotFound(new { message = "User not found" });
                }
                if (user.Role.ToString() != "ADMIN" && user.Role.ToString() != "BARBER")
                {
                    return StatusCode(403, new { message = "Only admin and barbers can log in" });
                }
                if (string.IsNullOrEmpty(request.Password))
                {
                    return StatusCode(500, new { message = "Password not set" });
                }
                //only barbers and admins have passwords. This might be redundant 
                //since we are checking for barbers and admins previously
                bool IsPasswordCorrect = BCrypt.Net.BCrypt.Verify(request.Password, user.Password);
                if (!IsPasswordCorrect)
                {
                    return Unauthorized(new { message = "Invalid Credentials. Please try again" });
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
                    Expires = DateTime.Now.AddDays(1),//stays logged in for 1 day
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
                    Secure = false,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTime.Now.AddDays(1)
                });
                Console.WriteLine("Login successful");
                return Ok(new
                {
                    user.Id,
                    user.Email,
                    user.Role,
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
                return Ok(new { user.Id, user.Email, user.Role });

            /* [Authorize] will automatically return 401 if the token is missing or invalid
             *This is done so by the challenge used in Program.cs
             */
        }
        [Authorize]
        [HttpGet("logout")]
        public async Task<IActionResult> Logout()
        {
            var userId = int.Parse(User.FindFirst("id")?.Value);
            var user = await _context.Users.FindAsync(userId);
            user.TokenVersion++;
            await _context.SaveChangesAsync();
            Response.Cookies.Delete("jwt");
            return Ok(new { message = "Logged out successfully" });
        }

        [HttpPost("forgot-password")]
        public async Task<IActionResult> CreateNewPassword([FromBody] NewPasswordViewModel request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            try
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
                if (user == null)
                {
                    return NotFound(new { message = "User with that email was not found" });
                }
                if (user.Role.ToString() != "ADMIN" && user.Role.ToString() != "BARBER")
                {
                    return StatusCode(403, new { message = "Only admin and barbers can log in" });
                }
                if (string.IsNullOrEmpty(request.newPassword) || string.IsNullOrEmpty(request.confirmNewPassword))
                {
                    return StatusCode(500, new { message = "Password not set" });
                }
                if (request.confirmNewPassword != request.newPassword)
                {
                    return StatusCode(500, new { message = "Both inputs need to be the same" });
                }
                if (BCrypt.Net.BCrypt.Verify(request.newPassword, user.Password))
                {
                    return StatusCode(400, new { message = "This password is already in use" });
                }
                user.Password = BCrypt.Net.BCrypt.HashPassword(request.newPassword);
                await _context.SaveChangesAsync();

                var TokenHandler = new JwtSecurityTokenHandler();

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
                    Expires = DateTime.Now.AddDays(1),//stays logged in for 1 day
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                };

                var token = TokenHandler.CreateToken(tokenDescriptor);

                var jwt = TokenHandler.WriteToken(token);


                Response.Cookies.Append("jwt", jwt, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = false,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTime.Now.AddDays(1)
                });

                return Ok(new { message = "Updated password successfully", user.Id, user.Email, user.Role});
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Login exception: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        }
    }

