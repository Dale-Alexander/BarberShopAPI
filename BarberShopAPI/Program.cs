using BarberShopAPI.CronJob;
using BarberShopAPI.Data;
using BarberShopAPI.Services;
using BarberShopAPI.Middleware;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Resend;
using Stripe;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;


/* Env.Load OVERWRITES variables already present in the environment, so a test or CI run cannot simply
 * export DefaultConnection and launch - the developer's .env silently wins and the run chews through the
 * real dev database. Choosing the FILE is the way round it: this variable is read before the load, so
 * nothing can clobber it.
 *
 * Missing file is a hard failure rather than a fall back to .env. Falling back would point an E2E run at
 * the dev database, which is the exact accident this exists to prevent, and it would do it silently. */
var envFile = Environment.GetEnvironmentVariable("BARBERSHOP_ENV_FILE");//PlayWright sets this. when you run the app normally, its unset so this is null. 
if (!string.IsNullOrWhiteSpace(envFile))
{
    // System.IO.File spelled out: `using Stripe;` below brings a Stripe.File into scope too.
    if (!System.IO.File.Exists(envFile))//Why system.IO.File and not just file? Further down, using Stripe; imports Stripe library and Stripe has its own class called File
        throw new InvalidOperationException(
            $"BARBERSHOP_ENV_FILE is set to '{envFile}' but no such file exists. Refusing to fall back to "
            + ".env, which would point this run at the development database.");
    /* Why crash instead of carrying on? If we shrugged and loaded .env instead, the tests would run against the real database and quietly destory it. A crash
     * is louf and costs nothing*/
    DotNetEnv.Env.Load(envFile);//loads that specific file
    Console.WriteLine($"Loaded environment from {envFile}");
}
else
{
    DotNetEnv.Env.Load();//no filename given -> original behaviour, load .env
}

var builder = WebApplication.CreateBuilder(args);
var connectionString = Environment.GetEnvironmentVariable("DefaultConnection");
builder.Services.AddDbContext<BarberShopContext>(options => options.UseSqlServer(connectionString));

var stripeKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
StripeConfiguration.ApiKey = stripeKey;


var resendKey = Environment.GetEnvironmentVariable("RESEND_API_KEY");

builder.Services.AddOptions();
builder.Services.AddHttpClient<ResendClient>();
builder.Services.Configure<ResendClientOptions>(o =>
{
    o.ApiToken = resendKey!;
});
builder.Services.AddTransient<IResend, ResendClient>();
builder.Services.AddScoped<IEmailService, EmailService>();


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

var frontendOrigin = Environment.GetEnvironmentVariable("FRONTEND_BASE_URL") ?? "http://localhost:5173";

builder.Services.AddCors(options =>
{
options.AddPolicy("AllowReactDev", policy =>
{
    policy.WithOrigins(frontendOrigin)
          .AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials();
});
});
//using CORS is redundant because i am going to use a proxy which treats the frontend and backend as the same origin. Im using proxy in both produ and dev. 
//Claude told me to keep it as a safety net

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHangfire(config => config.UseSqlServerStorage(connectionString));
builder.Services.AddHangfireServer();
builder.Services.AddScoped<BookingExpiryJob>();

// Per-IP throttling on the auth entry points. Login is brute-force bait; forgot-password can be used
// to email-bomb a victim's inbox. We partition each policy by the caller's IP so one abuser can't
// exhaust a bucket shared with everyone else. NOTE: behind a reverse proxy, RemoteIpAddress is the
// proxy unless ForwardedHeaders is configured - revisit the partition key when deploying behind one.
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many attempts. Please wait a moment and try again." }, token);
    };

    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    // forgot-password is protected by a per-EMAIL send throttle in authController (RequestPasswordReset)
    // instead of a per-IP limiter - see the comment there for why per-IP is the wrong tool for email bombing.
});

var app = builder.Build();

if (args.Contains("--seed-admin"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BarberShopContext>();
    BarberShopAPI.Seed.SeedAdmin.Run(db);
    return; // one-shot maintenance command - never starts the web server
}

// Same one-shot shape as --seed-admin. Wipes and rebuilds the browser-test fixture, and refuses to run
// against any database whose name doesn't say it's a test one.
if (args.Contains("--seed-e2e"))
{
    using var scope = app.Services.CreateScope();/* The database connection object is designed to be short lived(normally one per web request). There's no
                                                  * web request here so we manually create a scope to borrow one from. "using" automatically closes and disposes it when the block ends*/
    var db = scope.ServiceProvider.GetRequiredService<BarberShopContext>();/* fetches the database object. Required means throw if its missing rather rhan hand back null */
    BarberShopAPI.Seed.SeedE2E.Run(db);
    return;//stops the program, the website enver starts
}

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


// Register the recurring job via the DI-resolved IRecurringJobManager rather than the static
// RecurringJob API. The static API relies on JobStorage.Current, which isn't initialized at this
// point (with AddHangfire the storage lives in DI and the global is only set once the Hangfire
// server hosted service starts), so it throws "JobStorage instance has not been initialized yet".
using (var scope = app.Services.CreateScope())
{
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<BookingExpiryJob>(
        "cancel-pending-bookings",
        job => job.CancelExpiredBookingsAsync(),
        "*/15 * * * *"
    );
}



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

// Enforces the [EnableRateLimiting] policies declared above (login / forgot-password).
app.UseRateLimiter();

app.MapControllers();

app.Run();

// Top-level statements compile into an internal Program class, which WebApplicationFactory<Program>
// can't reach. Declaring it public here is the standard way to make the real host (this exact
// pipeline, middleware and DI) bootable from the integration tests - see BarberShopAPI.Tests/ApiFactory.
public partial class Program { }
