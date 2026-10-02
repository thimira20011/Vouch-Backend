using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Vouch.Api.Configuration;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

public class ConfigurationTests
{
    [Fact]
    public void LocalSettings_AreLowerPriorityThanEnvironmentAndCommandLine()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vouch-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var prefix = "VOUCH_CONFIG_TEST_" + Guid.NewGuid().ToString("N") + "_";
        try
        {
            File.WriteAllText(Path.Combine(folder, "appsettings.json"), "{\"Jwt\":{\"Key\":\"base\"}}");
            File.WriteAllText(Path.Combine(folder, "appsettings.Local.json"), "{\"Jwt\":{\"Key\":\"local\"}}");
            using var provider = new PhysicalFileProvider(folder);
            var environment = new TestEnvironment(provider) { EnvironmentName = Environments.Development };
            using var config = new ConfigurationManager();
            config.AddJsonFile(provider, "appsettings.json", false, false);
            Environment.SetEnvironmentVariable(prefix + "Jwt__Key", "environment");
            config.AddEnvironmentVariables(prefix);
            StartupConfiguration.AddLocalDevelopmentSettings(config, environment);
            Assert.Equal("environment", config["Jwt:Key"]);
            config.AddCommandLine(["--Jwt:Key=command-line"]);
            Assert.Equal("command-line", config["Jwt:Key"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Jwt__Key", null);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void LocalSettings_AreIgnoredOutsideDevelopment()
    {
        using var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "deployment" });
        var sourceCount = config.Sources.Count;
        StartupConfiguration.AddLocalDevelopmentSettings(config, new TestEnvironment(new NullFileProvider()) { EnvironmentName = Environments.Production });
        Assert.Equal("deployment", config["Jwt:Key"]);
        Assert.Equal(sourceCount, config.Sources.Count);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Database=vouch_db;Username=vouch_test_runner")]
    [InlineData("Host=127.0.0.1;Database=vouch_test_admin;Username=postgres")]
    [InlineData("Host=production.example;Database=vouch_test_admin;Username=vouch_test_runner")]
    public void Harness_RejectsApplicationOrUnapprovedDatabaseTargets(string connection)
        => Assert.Throws<InvalidOperationException>(() => new IsolatedPostgresDatabase(connection));

    [Fact]
    public void RatePartition_IgnoresSpoofedForwardedHeaders()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.10");
        var first = StartupConfiguration.CreatePartition(context, 5, false).PartitionKey;
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.20";
        Assert.Equal(first, StartupConfiguration.CreatePartition(context, 5, false).PartitionKey);
    }

    [Fact]
    public void AuthenticatedStandardLimits_ArePartitionedByUserRatherThanSharedIp()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, "user-a")], "Test"));
        var first = StartupConfiguration.CreatePartition(context, 20, true).PartitionKey;
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new(System.Security.Claims.ClaimTypes.NameIdentifier, "user-b")], "Test"));
        Assert.NotEqual(first, StartupConfiguration.CreatePartition(context, 20, true).PartitionKey);
    }

    private sealed class TestEnvironment(IFileProvider provider) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = provider;
    }
}
