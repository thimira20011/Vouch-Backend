using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.Security;

public sealed class ProtectedDataException : CryptographicException
{
    public ProtectedDataException() : base("Protected data could not be authenticated. Check key configuration and data integrity.") { }
}

public sealed class AesEncryptionService : IEncryptionService
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly byte[] _legacyKey;
    public string ActiveKeyId { get; }

    public AesEncryptionService(IConfiguration configuration)
    {
        // Retain the original key only for explicit legacy conversion. New writes use AES-GCM.
        var primary = RequiredConfiguration.Secret(configuration, "Security:EncryptionKey", 16);
        _legacyKey = SHA256.HashData(Utf8.GetBytes(primary));
        _keys["v1"] = _legacyKey;
        foreach (var item in configuration.GetSection("Security:EncryptionKeys").GetChildren())
        {
            ValidateId(item.Key);
            _keys[item.Key] = SHA256.HashData(Utf8.GetBytes(
                RequiredConfiguration.Secret(configuration, item.Path, 32)));
        }
        ActiveKeyId = configuration["Security:ActiveEncryptionKeyId"] ?? "v1";
        ValidateId(ActiveKeyId);
        if (!_keys.ContainsKey(ActiveKeyId)) throw new InvalidOperationException("The active encryption key ID is not configured.");
    }

    private static void ValidateId(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,32}$"))
            throw new InvalidOperationException("Encryption key IDs must contain 1–32 letters, numbers, underscores or hyphens.");
    }

    public string Encrypt(string plainText, string purpose = "value")
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;
        var bytes = Utf8.GetBytes(plainText);
        var payload = new byte[28 + bytes.Length];
        RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
        var header = $"vouch:v1:{ActiveKeyId}:";
        using var aes = new AesGcm(_keys[ActiveKeyId], 16);
        aes.Encrypt(payload.AsSpan(0, 12), bytes, payload.AsSpan(28), payload.AsSpan(12, 16), Utf8.GetBytes(header + purpose));
        return header + Convert.ToBase64String(payload);
    }

    public string Decrypt(string cipherText, string purpose = "value")
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;
        try
        {
            var parts = cipherText.Split(':', 4);
            if (parts.Length != 4 || parts[0] != "vouch" || parts[1] != "v1" || !_keys.TryGetValue(parts[2], out var key))
                throw new ProtectedDataException();
            var payload = Convert.FromBase64String(parts[3]);
            if (payload.Length < 28) throw new ProtectedDataException();
            var plain = new byte[payload.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain,
                Utf8.GetBytes($"vouch:v1:{parts[2]}:" + purpose));
            return Utf8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or DecoderFallbackException)
        {
            throw new ProtectedDataException();
        }
    }

    // Never called by runtime materialization. The upgrade tool selects known legacy formats explicitly.
    public string DecryptLegacy(string cipherText)
    {
        try
        {
            var bytes = Convert.FromBase64String(cipherText);
            if (bytes.Length < 32 || bytes.Length % 16 != 0) throw new ProtectedDataException();
            using var aes = Aes.Create();
            aes.Key = _legacyKey;
            aes.IV = bytes[..16];
            using var decryptor = aes.CreateDecryptor();
            return Utf8.GetString(decryptor.TransformFinalBlock(bytes, 16, bytes.Length - 16));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or DecoderFallbackException)
        {
            throw new ProtectedDataException();
        }
    }
}
