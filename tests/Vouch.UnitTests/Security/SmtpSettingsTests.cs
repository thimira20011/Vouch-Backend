using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Vouch.Infrastructure.Services;
using Vouch.Application.Common;

namespace Vouch.UnitTests.Security;

public class SmtpSettingsTests
{
    private static IConfiguration Configuration(string? key = null, string? value = null)
    {
        var settings = new Dictionary<string, string?> { ["Smtp:Host"] = "smtp.example.org", ["Smtp:Port"] = "587",
            ["Smtp:User"] = "sender@example.org", ["Smtp:Password"] = "test-password", ["Smtp:FromAddress"] = "sender@example.org" };
        if (key is not null) settings[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
    [Theory]
    [InlineData("Smtp:Host", "")] [InlineData("Smtp:Host", "https://smtp.example.org")]
    [InlineData("Smtp:Port", "465")] [InlineData("Smtp:Port", "0")] [InlineData("Smtp:Port", "65536")]
    [InlineData("Smtp:Port", "invalid")] [InlineData("Smtp:User", "")]
    [InlineData("Smtp:Password", "")] [InlineData("Smtp:Password", "CHANGE_ME_PASSWORD")]
    [InlineData("Smtp:FromAddress", "invalid")]
    public void ProductionRejectsMissingOrInvalidEmailConfiguration(string key, string value)
        => Assert.Throws<InvalidOperationException>(() => SmtpSettings.Read(Configuration(key, value), production: true));

    [Fact]
    public void CompleteStartTlsConfigurationIsAccepted() => Assert.Equal(587, SmtpSettings.Read(Configuration(), production: true)!.Port);

    [Fact]
    public async Task MissingDevelopmentSmtpNeverClaimsDeliverySuccess()
    {
        var config = Configuration("Smtp:Host", ""); Assert.Null(SmtpSettings.Read(config));
        var smtp = new SmtpEmailService(config, NullLogger<SmtpEmailService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => smtp.SendAsync("recipient@example.org", "Test", "Test"));
        await Assert.ThrowsAsync<VerificationDeliveryException>(() => smtp.SendVerificationAsync("recipient@example.org", "test-token"));
    }
}
