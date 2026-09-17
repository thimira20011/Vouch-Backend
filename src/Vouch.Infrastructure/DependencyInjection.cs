using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Application.Features.Matching;
using Vouch.Application.Features.Messaging;
using Vouch.Application.Features.Moderation;
using Vouch.Application.Features.Vouching;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.Services;

namespace Vouch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured. Set it via environment variable or user secrets.");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        // Security & Crypto
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IEncryptionService, AesEncryptionService>();

        // AI Wingman with HttpClient
        services.AddHttpClient<IAiWingmanService, AnthropicAiWingmanService>();

        // Real-time Notification
        services.AddSignalR();
        services.AddScoped<ISlowBurnNotificationService, SlowBurnNotificationService>();

        // Domain Services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITrustService, TrustService>();
        services.AddScoped<IMatchService, MatchService>();
        services.AddScoped<IMessagingService, MessagingService>();
        services.AddScoped<IModerationService, ModerationService>();

        return services;
    }
}
