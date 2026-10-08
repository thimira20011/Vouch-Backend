using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;

namespace Vouch.IntegrationTests.Infrastructure;

public sealed class VouchApiFactory(string connection, Dictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    public MutableTimeProvider Clock { get; } = new();
    public const string SigningKey = "integration-test-signing-key-only-48-characters-long";
    public static Dictionary<string, string?> Settings(string connection) => new()
    {
        ["ConnectionStrings:DefaultConnection"] = connection,
        ["Jwt:Key"] = SigningKey, ["Jwt:Issuer"] = "vouch-tests", ["Jwt:Audience"] = "vouch-tests",
        ["Security:EncryptionKey"] = "integration-test-encryption-key-only",
        ["Security:EmailLookupKey"] = "integration-test-email-lookup-key-only-48-characters",
        ["Database:SeedOnStartup"] = "false", ["BackgroundJobs:Enabled"] = "false",
        ["Logging:File:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning",
        ["Smtp:Host"] = "", ["Smtp:User"] = "", ["Smtp:Password"] = "", ["Anthropic:ApiKey"] = "",
        ["ReverseProxy:KnownProxies:0"] = "127.0.0.1",
        ["Cors:AllowedOrigins:0"] = "https://frontend.test"
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = Settings(connection);
        if (overrides is not null) foreach (var setting in overrides) settings[setting.Key] = setting.Value;
        // Host settings are available while Program reads startup configuration.
        foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.RemoveAll<IAiWingmanService>();
            services.RemoveAll<IPhotoStorageService>();
            services.RemoveAll<IVerificationEmailSender>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<RecordingEmailService>();
            services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<RecordingEmailService>());
            services.AddSingleton<IVerificationEmailSender>(sp => sp.GetRequiredService<RecordingEmailService>());
            services.AddSingleton<IAiWingmanService, TestWingmanService>();
            services.AddSingleton<IPhotoStorageService, DisabledPhotoStorage>();
            services.AddTransient<IStartupFilter, TestClientAddressFilter>();
        });
    }

    public HttpClient Client(string ip = "198.51.100.10")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Client-IP", ip);
        return client;
    }

    private sealed class TestClientAddressFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Client-IP"].FirstOrDefault() ?? "198.51.100.10");
                await continuation();
            });
            next(app);
        };
    }
}

public sealed class RecordingEmailService : IEmailService, IVerificationEmailSender
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> VerificationTokens { get; } = new();
    public bool FailVerification { get; set; }
    public Task SendVerificationAsync(string to, string token, CancellationToken ct = default)
    {
        if (FailVerification) throw new Vouch.Application.Common.VerificationDeliveryException();
        VerificationTokens[to] = token;
        return Task.CompletedTask;
    }
    public int Calls { get; private set; }
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        Calls++;
        return Task.CompletedTask;
    }
}

public sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
}

public sealed class TestWingmanService : IAiWingmanService
{
    public Task<IReadOnlyList<IcebreakerSuggestion>> GenerateIcebreakersAsync(User userA, User userB, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<IcebreakerSuggestion>>([new("Offline test suggestion", "Test", false)]);
}

public sealed class DisabledPhotoStorage : IPhotoStorageService
{
    public Task<(string OriginalUrl, string AbstractUrl)> StorePhotoAsync(Guid userId, Stream stream, string contentType, CancellationToken ct = default)
        => throw new InvalidOperationException("Photo uploads are disabled in this API smoke-test harness.");
}
