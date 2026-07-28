using BarberShopAPI.Models.Enums;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;

namespace BarberShopAPI.Tests.Infrastructure
{
    /* Mints the same JWT authController.Login issues, so the admin/barber endpoints are exercised through
     * the real authentication + TokenVersionMiddleware pipeline rather than having auth stubbed out.
     * The three claims (id / role / tokenVersion) are exactly what the middleware checks. */
    public static class TestJwt
    {
        // Only ever used against the test database. HS256 needs >= 256 bits of key material.
        public const string Secret = "integration-test-only-jwt-secret-key-not-a-real-credential-0123456789";

        public static string Create(int userId, Role role, int? tokenVersion)
        {
            var key = Encoding.ASCII.GetBytes(Secret);
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim("id", userId.ToString()),
                    new Claim(ClaimTypes.Role, role.ToString()),
                    new Claim("tokenVersion", (tokenVersion ?? 0).ToString())
                }),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };
            var handler = new JwtSecurityTokenHandler();
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        /// <summary>The API reads the token from a `jwt` cookie (see the JwtBearer OnMessageReceived hook).</summary>
        public static void Authenticate(this HttpClient client, int userId, Role role, int? tokenVersion)
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", $"jwt={Create(userId, role, tokenVersion)}");
        }
    }
}
