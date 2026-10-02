using Microsoft.Extensions.Configuration;

namespace Vouch.Infrastructure.Security;

public static class RequiredConfiguration
{
    public static string Read(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{key} must be configured with a nonblank value.");
        return value;
    }

    public static string Secret(IConfiguration configuration, string key, int minimumLength)
    {
        var value = Read(configuration, key);
        if (value.Length < minimumLength || value.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{key} must be a real secret of at least {minimumLength} characters.");
        return value;
    }
}

public sealed record JwtSettings(string Key, string Issuer, string Audience)
{
    public static JwtSettings Read(IConfiguration configuration) => new(
        RequiredConfiguration.Secret(configuration, "Jwt:Key", 32),
        RequiredConfiguration.Read(configuration, "Jwt:Issuer"),
        RequiredConfiguration.Read(configuration, "Jwt:Audience"));
}
