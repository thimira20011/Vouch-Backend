using System.Net.Sockets;
using System.Security.Authentication;
using Npgsql;
using Vouch.Api.Configuration;

namespace Vouch.IntegrationTests;

public sealed class DatabaseOperationErrorsTests
{
    private const string PrivateDetail = "private-password-and-query-content";

    [Theory]
    [InlineData("28P01", "authentication failed")]
    [InlineData("28000", "login was rejected")]
    [InlineData("3D000", "does not exist")]
    [InlineData("42501", "review schema and permissions")]
    public void ProviderFailuresExposeOnlySafeCategories(string code, string expected)
    {
        var provider = new PostgresException(PrivateDetail, "ERROR", "ERROR", code, detail: PrivateDetail);
        var result = DatabaseOperations.SafeFailureMessage(new InvalidOperationException(PrivateDetail, provider));
        Assert.Contains(expected, result);
        Assert.DoesNotContain(PrivateDetail, result);
    }

    [Fact]
    public void NestedTlsErrorDoesNotExposePrivateDetails()
    {
        var result = DatabaseOperations.SafeFailureMessage(new NpgsqlException(PrivateDetail, new AuthenticationException(PrivateDetail)));
        Assert.Contains("TLS certificate validation failed", result);
        Assert.DoesNotContain(PrivateDetail, result);
    }

    [Fact]
    public void SocketFailureExposesOnlyErrorCode()
    {
        var result = DatabaseOperations.SafeFailureMessage(new NpgsqlException(PrivateDetail, new SocketException((int)SocketError.HostNotFound)));
        Assert.Contains("HostNotFound", result);
        Assert.DoesNotContain(PrivateDetail, result);
    }

    [Fact]
    public void TimeoutDoesNotExposePrivateDetails()
    {
        var result = DatabaseOperations.SafeFailureMessage(new NpgsqlException(PrivateDetail, new TimeoutException(PrivateDetail)));
        Assert.Contains("timed out", result);
        Assert.DoesNotContain(PrivateDetail, result);
    }
}
