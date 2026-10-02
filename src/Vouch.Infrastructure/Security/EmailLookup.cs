using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.Security;

public sealed class EmailLookup : IEmailLookup
{
    private readonly byte[] _key;
    public EmailLookup(IConfiguration configuration)
        => _key = SHA256.HashData(Encoding.UTF8.GetBytes(
            RequiredConfiguration.Secret(configuration, "Security:EmailLookupKey", 32)));

    public string Normalize(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Email is required.");
        return email.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public string Hash(string email) => Convert.ToHexString(HMACSHA256.HashData(_key,
        Encoding.UTF8.GetBytes("vouch-email-v1:" + Normalize(email))));
}
