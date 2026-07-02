using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using AgriForecast.API.Middleware;
using AgriForecast.Application.Dependency_Injection;
using AgriForecast.Infrastructure.Dependency_Injection;
using AgriForecast.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen(options =>
{
    // Let Swagger UI send a bearer token via the Authorize button.
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter the JWT token. Example: \"eyJhb...\" (no need to prefix with 'Bearer')."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplicationLayer();

// JWT bearer authentication. The signing key comes from the "Jwt" config section
// (dev placeholder in appsettings; MUST be an env/secret-store value in production).
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
                  ?? throw new InvalidOperationException("Missing Jwt configuration section.");

// Fail-loud (security fix F-12): never boot with a missing, empty, too-short or
// known-placeholder JWT signing key. Mirrors the SqlDbService connection-string guard.
// The real key MUST come from user-secrets (dev) or the Jwt__Key env var (prod).
const string JwtPlaceholderKey = "dev-only-change-me-agriforecast-jwt-signing-key-0123456789";
if (string.IsNullOrWhiteSpace(jwtSettings.Key)
    || jwtSettings.Key == JwtPlaceholderKey
    || Encoding.UTF8.GetByteCount(jwtSettings.Key) < 32)
{
    throw new InvalidOperationException(
        "Invalid Jwt:Key. It is missing, empty, the known dev placeholder, or shorter than 32 bytes. " +
        "Set a strong (>= 32 byte) key for local dev via " +
        "'dotnet user-secrets set \"Jwt:Key\" \"<value>\"', " +
        "or in production via the Jwt__Key environment variable.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// CORS is restricted to the origins listed under "Cors:AllowedOrigins" in
// configuration (set per environment; see appsettings.Development.json).
// Fail-closed: an empty/missing list means no cross-origin access at all —
// never fall back to AllowAnyOrigin (security fix F-07).
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                  ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("ConfiguredOrigins",
        policy =>
        {
            policy.WithOrigins(corsOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader();
        });
});

// Rate limiting (security fix F-08). Defaults are ON even if the "RateLimiting"
// section is absent (fail-closed). A global fixed-window limiter guards the whole
// API per client IP; a stricter "auth" policy protects the login/register endpoints.
var globalPermit = builder.Configuration.GetValue<int?>("RateLimiting:GlobalPermitPerMinute") ?? 100;
var authPermit = builder.Configuration.GetValue<int?>("RateLimiting:AuthPermitPerMinute") ?? 10;
var queueLimit = builder.Configuration.GetValue<int?>("RateLimiting:QueueLimit") ?? 0;

// Partition key for the rate limiter. When RemoteIpAddress is null (e.g. an
// in-memory/test connection) we intentionally fall all such requests into a single
// shared "unknown" bucket rather than skipping the limit — fail-closed, never fail-open.
static string ClientKey(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global limiter: applies to every request, partitioned per client IP.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), key =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalPermit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = queueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    // Stricter named policy for authentication endpoints.
    options.AddPolicy("auth", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), key =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = queueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    // Emit a generic 429 with Retry-After; details never leak to the client.
    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsync(
            "Too many requests. Please retry later.", token);
    };
});

// Forwarded-headers support for the rate-limiter partition key (F-08 hardening).
// Behind a reverse proxy, Connection.RemoteIpAddress is the PROXY's IP, so every client
// would share one rate-limit bucket (self-DoS). Trusting X-Forwarded-For blindly lets
// clients spoof their partition key. Safe middle ground: OFF by default; only honour
// forwarded headers when the operator explicitly lists the trusted proxy IPs under
// "ForwardedHeaders:KnownProxies". Empty/missing list => direct-connection behaviour.
var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>()
                   ?? Array.Empty<string>();
var forwardedHeadersEnabled = knownProxies.Length > 0;
if (forwardedHeadersEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust ONLY the explicitly configured proxy IPs. Clear the framework defaults
        // (which trust loopback KnownNetworks/KnownProxies) so nothing else is honoured.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in knownProxies)
        {
            if (System.Net.IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Authentication API v1");
        c.RoutePrefix = string.Empty; // Root path
    });
}

app.UseCors("ConfiguredOrigins");
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseHttpsRedirection();
// Rewrite RemoteIpAddress from a trusted proxy's X-Forwarded-For BEFORE the rate limiter
// reads it, so the partition key reflects the real client (only when KnownProxies is set).
if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}
// Rate limiting runs before auth so abusive/unauthenticated floods are shed early (F-08).
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Anonymous liveness probe. Exempt from rate limiting: LB/orchestrator probes often
// originate from a single source IP and would otherwise share one bucket and hit 429,
// causing probe flaps and needless restarts (F-08 hardening).
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous()
    .DisableRateLimiting();

app.MapControllers();
app.Run();
