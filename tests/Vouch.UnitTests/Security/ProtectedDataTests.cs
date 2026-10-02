using System.Globalization;
using Microsoft.Extensions.Configuration;
using Vouch.Infrastructure.Security;

namespace Vouch.UnitTests.Security;

public sealed class ProtectedDataTests
{
    private static IConfiguration Config(string primary = "primary-test-encryption-key-32-chars", bool rotated = false)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:EncryptionKey"] = primary,
            ["Security:EmailLookupKey"] = "separate-test-email-lookup-key-48-characters",
            ["Security:ActiveEncryptionKeyId"] = rotated ? "v2" : "v1",
            ["Security:EncryptionKeys:v2"] = "new-test-encryption-key-48-characters-long"
        }).Build();

    [Theory]
    [InlineData("")]
    [InlineData("සෙනෙහස 日本語 🔐")]
    [InlineData("A normal name")]
    public void Encryption_RoundTripsUnicodeAndRandomizesNonemptyWrites(string value)
    {
        var encryption = new AesEncryptionService(Config());
        var first = encryption.Encrypt(value, "Users.FullName");
        Assert.Equal(value, encryption.Decrypt(first, "Users.FullName"));
        if (value.Length > 0) Assert.NotEqual(first, encryption.Encrypt(value, "Users.FullName"));
    }

    [Fact]
    public void Corruption_WrongKey_AndFieldSwapsFailClosed()
    {
        var encryption = new AesEncryptionService(Config());
        var cipher = encryption.Encrypt("private email", "Users.Email");
        var parts = cipher.Split(':', 4);
        var bytes = Convert.FromBase64String(parts[3]);
        bytes[^1] ^= 1;
        var changed = string.Join(':', parts.Take(3)) + ":" + Convert.ToBase64String(bytes);
        Assert.Throws<ProtectedDataException>(() => encryption.Decrypt(changed, "Users.Email"));
        Assert.Throws<ProtectedDataException>(() => encryption.Decrypt(cipher, "Users.FullName"));
        Assert.Throws<ProtectedDataException>(() => new AesEncryptionService(Config("different-test-encryption-key-32-chars")).Decrypt(cipher, "Users.Email"));
    }

    [Theory]
    [InlineData("plain name")]
    [InlineData("vouch:v1:unknown:AAAA")]
    [InlineData("vouch:v2:v1:AAAA")]
    [InlineData("vouch:v1:v1:invalid-base64")]
    public void RuntimeDecrypt_NeverFallsBackToPlaintext(string cipher)
        => Assert.Throws<ProtectedDataException>(() => new AesEncryptionService(Config()).Decrypt(cipher));

    [Fact]
    public void KeyRotation_ReadsRetainedKeyAndWritesActiveKey()
    {
        var old = new AesEncryptionService(Config());
        var rotated = new AesEncryptionService(Config(rotated: true));
        Assert.Equal("secret", rotated.Decrypt(old.Encrypt("secret")));
        Assert.StartsWith("vouch:v1:v2:", rotated.Encrypt("secret"));
        Assert.Equal("secret", rotated.Decrypt(rotated.Encrypt("secret")));
    }

    [Fact]
    public void EmailHash_IsCultureIndependentNormalizedAndSeparateFromEncryptionKey()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var first = new EmailLookup(Config());
            Assert.Equal(first.Hash("Identity@TEST.AC.LK"), first.Hash(" identity@test.ac.lk "));
            Assert.NotEqual(first.Hash("other@test.ac.lk"), first.Hash("identity@test.ac.lk"));
            Assert.Equal(first.Hash("identity@test.ac.lk"), new EmailLookup(Config("a-rotated-primary-encryption-secret")).Hash("identity@test.ac.lk"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
