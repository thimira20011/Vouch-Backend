using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Vouch.Api.Endpoints;
using Vouch.Application.Common.Interfaces;
using Vouch.Infrastructure;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.SignalR;

var builder = WebApplication.CreateBuilder(args);

// 1. Add Infrastructure Services (EF Core PostgreSQL, SignalR, Security, Domain Services)
builder.Services.AddInfrastructure(builder.Configuration);

// 2. Configure JWT Authentication & Authorization
var jwtSecret = builder.Configuration["Jwt:Key"] ?? "VouchSuperSecretJwtSigningKeyMustBeVeryLongAndSecure2026!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "VouchServer";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "VouchClient";

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
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
    };

    // Support SignalR authentication via query string access_token
    options.Events = new JwtBearerEvents
    {
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

// 3. CORS Configuration for React 19 Frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("VouchCorsPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddOpenApi();

// 4. Rate Limiting — brute-force protection on auth endpoints (Step 7)
builder.Services.AddRateLimiter(options =>
{
    // Strict: register + login — 5 attempts per minute, queue up to 2
    options.AddFixedWindowLimiter("auth_strict", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 2;
    });

    // Standard: onboarding + delete-account — 20 per minute
    options.AddFixedWindowLimiter("auth_standard", o =>
    {
        o.PermitLimit = 20;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 5;
    });

    // Return 429 Too Many Requests with a Retry-After header
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsync(
            "{\"error\":\"Too many requests. Please wait before trying again.\"}", ct);
    };
});

var app = builder.Build();

// 4. Seed Database
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        await DbInitializer.SeedAsync(db, hasher);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database seeding deferred or encountered error during startup.");
    }
}

// 5. Configure HTTP Pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("VouchCorsPolicy");
app.UseRateLimiter(); // Must come before Authentication
app.UseAuthentication();
app.UseAuthorization();

// 6. Map API Endpoints
app.MapAuthEndpoints();
app.MapVouchEndpoints();
app.MapMatchEndpoints();
app.MapMessagingEndpoints();
app.MapWingmanEndpoints();
app.MapModerationEndpoints();

// 7. Map SignalR Hub
app.MapHub<VouchHub>("/hubs/vouch");

app.MapGet("/", () => Results.Ok(new
{
    Application = "Vouch API",
    Version = "2.0.0",
    Philosophy = "Monastic Minimalism & Slow Tech",
    Status = "Operational"
}));

app.Run();
