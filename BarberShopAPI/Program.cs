using BarberShopAPI.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using BarberShopAPI.Middleware;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Stripe;
using Hangfire;
using Hangfire.MemoryStorage;
using BarberShopAPI.CronJob;


DotNetEnv.Env.Load();
var builder = WebApplication.CreateBuilder(args);
var connectionString = Environment.GetEnvironmentVariable("DefaultConnection");
builder.Services.AddDbContext<BarberShopContext>(options => options.UseSqlServer(connectionString));

var stripeKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
StripeConfiguration.ApiKey = stripeKey;

var key = Encoding.ASCII.GetBytes(Environment.GetEnvironmentVariable("JWT_SECRET"));
/* The above is the same secret you used to sign the JWT during /login.
 * It is used to verify the token signature*/
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
    /* The above tells ASP.NET: JWTBearer is my default authentication method.
     *If authentication fails, use JWT logic. this is required for [Authorize]
     *to work
     DefaultAuthenticateScheme is used when the app wants to figure out who the user is
    Example: When [Authorize] is hit -> ASP.NET asks which scheme should i use 
    to validate the user -> JWT
    DefaultChallengeScheme -> used when authentication fails
    Example: [Authorize] is hit and no valid JWT is found. ASP.NET asks "how
    should i respond to an unauthenticated request" -> JWT middleware decides and
    it does so by returning 401 instead of you writing custom code
     */
    .AddJwtBearer(options =>
    {
        /* The above code registers the JWT handler that: reads token, validates them,
         * Creates HttpContext.User*/
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.ContainsKey("jwt"))
                {
                    context.Token = context.Request.Cookies["jwt"];
                }
                return Task.CompletedTask;
            }
        };


        /* The above: This is telling the JWT middleware: "Before you try to 
         * validate a token, check if the request contains a cookie called
         * jwt. If it does, treat the cookie's value as the token.
         * options.Events: JWT middleware exposes events during processing.
         * OnMessageReceived is called once per request, before validation.
         * It gives you a chance to modify where the token comes from
         * "context.Request.Cookies.containsKey("jwt"): Checks if the HTTP
         * request sent a cookie named jwt. (withCredentials has to be set to 
         * true)
         * "context.Token = context.Request.Cookies["jwt"]": Sets the token
         * that the middleware will use instead of the default Authorization
         * header because normally JWT middleware expects tokens in headers. 
         * But because youre storing the token in a HttpOnly Cookie, you need 
         * this hook to "tell it where to look". The above runs on every
         request that his a route with [Authorize], looks at the network
        request, sepcifically the cookies to find the token, sets the token
        so the JWT middleware can calidate it
        
         OnMessageReceived expects a Task(async pattrn)
        If your logic is synchronous, you still need to return a Task.
        Task.CompletedTasl is a pre-completed Task(basically: "Im done, no 
        async work to await")*/
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ClockSkew = TimeSpan.Zero,
        };
        /* The above: 
         * What this enforces: Token is signed with your secret, 
         * token is not expired, No issuer check(fine for now), no audience
         * check(fine for now), token expires exactly at expiration time
         *
         *
         *What happens on every request now:
         *Request comes in -> Middleware checks cookie -> Extracts JWT ->
         *Validates signature & expiration -> Builds ClaimsPrincipal -> 
         *Sets HttpContext.User -> [Authorize] allows or blocks/*/
    });

/* The above code: From var key = ... to here, it tells ASP.NET "For every
 * incoming request, try to authenticate the user using a JWT that comes from 
 a cookie. Once this is setup [Authorize] works automatically. You never 
manually validate JWTs again. HttpContext.User is populated for you*/

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Serialize enums as strings
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddCors(options =>
{
options.AddPolicy("AllowReactDev", policy =>
{
    policy.WithOrigins("http://localhost:5173")
          .AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials();
});
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHangfire(config => config.UseMemoryStorage());
builder.Services.AddHangfireServer();
builder.Services.AddScoped<BookingExpiryJob>();

var app = builder.Build();


app.Use(async (context, next) =>
{
    Console.WriteLine($"[{context.Request.Method}] {context.Request.Path}");
    await next();
    Console.WriteLine($"Response status: {context.Response.StatusCode}"); // 👈 add this line
});

var addresses = app.Urls;
Console.WriteLine("Server is listening on:");
foreach (var addr in addresses)
{
    Console.WriteLine(addr);
}

app.UseCors("AllowReactDev");
var provider = new FileExtensionContentTypeProvider();
// Add new mappings
provider.Mappings[".avif"] = "image/avif";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
});


/* Make sure the app.useCors() is first then the app.useStaticFiles() */


/*app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads")
        ),
    RequestPath = "/uploads"
});*/
/* UseStaticFiles tells ASP.NET Core: "I want to serve some files as static
 * content i.e: accessible via URL" PhysicalFileProvider(Path.Combine) Points
 ASP.NET Core to the physical folder on disk where files live. In this case
wwwroot/uploads. RequestPath = "/uploads" maps the url path /uploads to the 
physical folder. For example: If a file is at wwwroot/uploads/image.png, it will
be accessible via http://localhost:5000/uploads/image.png

 This is needed because when your controller saves uploaded images to 
wwwroot/uploads, they exist on the server's file system, not automatically
acccessible over the web. Without useStaticFiles, hitting /uploads/image.png
in the browser wil give a 404. UseStaticFiles exposes that folder as a public route,
so the web client can load images.*/

// Configure the HTTP request pipeline.


RecurringJob.AddOrUpdate<BookingExpiryJob>(
    "cancel-pending-bookings",
    job => job.CancelExpiredBookingsAsync(),
    "*/15 * * * *"
);



if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
// Only redirect in production


app.UseAuthentication();
/* .UseAuthentication() reads incoming request, chceks for a token/cookie
 * based on your configured authentication scheme(JWT), validates the token
 if valid, builds HttpContext.User(The ClaimsPrincipal), Does not check the 
permissions or roles so it just tells the app "Here's who the user is"*/

app.UseMiddleware<TokenVersionMiddleware>();

app.UseAuthorization();

/* .UseAuthorization() Runs after authentication. checks if the authenticated
 * user is allowed to access the resource. Applies [Authorize], role checks,
 policy checks etc. Uses HttpContext.User that was set by .UseAuthentication()
*/

//This means that .UseAuthentication needs to be before .UseAuthorization()

app.MapControllers();

app.Run();
