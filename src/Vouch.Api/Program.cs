using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Vouch.Api.Endpoints;
using Vouch.Api.Configuration;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Infrastructure;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.SignalR;
using Vouch.Infrastructure.Security;

// Step 22: Configure Serilog from appsettings ("Serilog" section)
// Console sink: always active. File sink: optional, configurable per environment.
var builder = WebApplication.CreateBuilder(args);
StartupConfiguration.AddLocalDevelopmentSettings(builder.Configuration, builder.Environment);
if (args.Contains("--database-task", StringComparer.Ordinal))
    return await DatabaseOperations.RunAsync(builder.Configuration);
builder.Host.UseSerilog((context, services, logger) =>
{
    logger.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "Vouch")
        // SignalR browser transports can carry access_token in the query string.
        // Hosting request-start logs include the full URI; the request middleware logs Path only.
        .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console();
    if (context.Configuration.GetValue("Logging:File:Enabled", true))
        logger.WriteTo.File("logs/vouch-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);
});
var useTrustedProxies = StartupConfiguration.AddTrustedProxySupport(builder.Services, builder.Configuration);
if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(builder.Configuration["Tenancy:CampusCode"]))
    throw new InvalidOperationException("Production requires Tenancy:CampusCode and an isolated campus database/deployment. See docs/sessions-and-access.md.");
var configuredPhotoUrl = builder.Configuration["PhotoStorage:BaseUrl"];
if (!string.IsNullOrWhiteSpace(configuredPhotoUrl) && configuredPhotoUrl != "/photos")
    throw new InvalidOperationException("PhotoStorage:BaseUrl must be /photos for authorized local photo retrieval.");

// 1. Add Infrastructure Services (EF Core PostgreSQL, SignalR, Security, Domain Services)
builder.Services.AddInfrastructure(builder.Configuration);

// 2. Configure JWT Authentication & Authorization
// SECURITY: These values MUST be set via environment variables or secrets manager.
// The app will refuse to start if any are missing — never use fallback defaults for secrets.
var jwtSettings = JwtSettings.Read(builder.Configuration);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.Zero,
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key))
    };

    // Support SignalR authentication via query string access_token
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            try
            {
                var services = context.HttpContext.RequestServices;
                var db = services.GetRequiredService<IApplicationDbContext>();
                var user = await SessionAccess.RequireAsync(db, context.Principal!, services.GetRequiredService<TimeProvider>().GetUtcNow(), context.HttpContext.RequestAborted);
                await services.GetRequiredService<CampusBoundary>().RequireAsync(db, user.CampusId, context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or Vouch.Application.Common.EligibilityException)
            { context.Fail("Session is unavailable."); }
        },
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ArchitectOnly", policy => policy.RequireRole("Architect"));
});

// 3. CORS Configuration — Step 13: Origins read from config for env-specific overrides
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (allowedOrigins is null || allowedOrigins.Length == 0)
    throw new InvalidOperationException("Cors:AllowedOrigins is not configured. Add at least one origin to appsettings.json.");
if (allowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
    uri.Scheme is not ("http" or "https") || origin.TrimEnd('/') != uri.GetLeftPart(UriPartial.Authority)))
    throw new InvalidOperationException("Cors:AllowedOrigins must contain explicit HTTP(S) origins without paths.");

builder.Services.AddCors(options =>
{
    options.AddPolicy("VouchCorsPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddOpenApi();

// Step 11: Register all FluentValidation validators from Vouch.Application assembly
// New validators are auto-discovered — no changes needed here when adding validators
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

// 4. Rate Limiting — brute-force protection on auth endpoints (Step 7)
StartupConfiguration.AddAuthRateLimiting(builder.Services);

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Tenancy:CampusCode"]))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CampusBoundary>().VerifyDeploymentAsync(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>());
}

// Schema upgrades, protected-data backfill, reference seeds and admin provisioning are explicit deployment commands.

// 5. Configure HTTP Pipeline
// Exception handler MUST be first so it wraps the entire pipeline (Step 10)
app.UseMiddleware<Vouch.Api.Middleware.GlobalExceptionHandlerMiddleware>();
if (useTrustedProxies) app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseSerilogRequestLogging(); // Step 22: Structured HTTP request logging
app.UseCors("VouchCorsPolicy");
// Photos are served only by the authorized endpoint; never through public static middleware.
app.UseAuthentication();
app.UseRateLimiter(); // Authenticated policies partition by user; public auth routes partition by client IP.
app.UseAuthorization();

// 6. Map API Endpoints
app.MapAuthEndpoints();
app.MapVouchEndpoints();
app.MapMatchEndpoints();
app.MapMessagingEndpoints();
app.MapWingmanEndpoints();
app.MapModerationEndpoints();
app.MapProfileEndpoints(); // Step 21: PUT /api/profile/photo (REQ-17)

// 7. Map SignalR Hub
app.MapHub<VouchHub>("/hubs/vouch", options => options.CloseOnAuthenticationExpiration = true);

// Step 12: Health check endpoint — used by Docker HEALTHCHECK and load balancer probes
app.MapHealthChecks("/healthz");

app.MapGet("/", () => Results.Ok(new
{
    Application = "Vouch API",
    Version = "2.0.0",
    Philosophy = "Monastic Minimalism & Slow Tech",
    Readiness = "/healthz"
}));

app.Run();
return 0;

public partial class Program { }
