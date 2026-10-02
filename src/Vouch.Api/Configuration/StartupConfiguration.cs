using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration.Json;

namespace Vouch.Api.Configuration;

public static class StartupConfiguration
{
    public static void AddLocalDevelopmentSettings(ConfigurationManager configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) return;

        // Insert after appsettings but BEFORE user secrets, environment variables and CLI arguments.
        var index = configuration.Sources.Select((source, i) => (source, i))
            .Where(item => item.source is JsonConfigurationSource json &&
                (json.Path == "appsettings.json" || json.Path == $"appsettings.{environment.EnvironmentName}.json"))
            .Select(item => item.i + 1).DefaultIfEmpty(0).Max();
        configuration.Sources.Insert(index, new JsonConfigurationSource
        {
            Path = "appsettings.Local.json", Optional = true, ReloadOnChange = true,
            FileProvider = environment.ContentRootFileProvider
        });
    }

    public static void AddAuthRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("auth_strict", context => CreatePartition(context, 5, preferUser: false));
            options.AddPolicy("auth_standard", context => CreatePartition(context, 20, preferUser: true));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) : 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests. Please wait before trying again." }, ct);
            };
        });
    }

    public static RateLimitPartition<string> CreatePartition(HttpContext context, int permitLimit, bool preferUser)
    {
        var userId = preferUser && context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        var key = userId is not null ? $"user:{userId}"
            : $"ip:{context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown"}";
        // Headers never determine identity here; only trusted forwarded-header middleware may change RemoteIpAddress.
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            AutoReplenishment = true
        });
    }

    public static bool AddTrustedProxySupport(IServiceCollection services, IConfiguration configuration)
    {
        var values = configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        if (values.Length == 0) return false;
        var addresses = values.Select(value => IPAddress.TryParse(value, out var address) ? address
            : throw new InvalidOperationException("ReverseProxy:KnownProxies must contain valid IP addresses.")).ToArray();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in addresses) options.KnownProxies.Add(address);
        });
        return true;
    }
}
