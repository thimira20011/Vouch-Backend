using Microsoft.Extensions.Configuration;
using Vouch.Infrastructure;
using Vouch.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Vouch.UnitTests.Security;

public class RequiredConfigurationTests
{
    private static IConfiguration Config(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = new string('k', 48), ["Jwt:Issuer"] = "test", ["Jwt:Audience"] = "test",
            ["Security:EncryptionKey"] = new string('e', 32),
            ["Security:EmailLookupKey"] = new string('h', 48),
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=test"
        };
        foreach (var item in overrides ?? []) values[item.Key] = item.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData("Jwt:Key", null)] [InlineData("Jwt:Key", "")] [InlineData("Jwt:Key", " ")]
    [InlineData("Jwt:Key", "short")] [InlineData("Jwt:Key", "CHANGE_ME_minimum_32_characters_long")]
    [InlineData("Jwt:Issuer", null)] [InlineData("Jwt:Issuer", " ")]
    [InlineData("Jwt:Audience", null)] [InlineData("Jwt:Audience", "")]
    public void TokenService_RejectsInvalidConfigurationWithoutFallback(string key, string? value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new JwtTokenService(Config(new() { [key] = value })));
        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("                  ")]
    [InlineData("short")] [InlineData("CHANGE_ME_aes256_key_32_chars_minimum")]
    public void EncryptionService_RejectsInvalidKeys(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => new AesEncryptionService(Config(new() { ["Security:EncryptionKey"] = key })));
    }

    [Theory]
    [InlineData("ConnectionStrings:DefaultConnection")]
    [InlineData("Jwt:Key")]
    [InlineData("Security:EncryptionKey")]
    [InlineData("Security:EmailLookupKey")]
    public void Infrastructure_RejectsBlankRequiredConfigurationBeforeResolvingServices(string key)
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(Config(new() { [key] = " " })));
    }
}
