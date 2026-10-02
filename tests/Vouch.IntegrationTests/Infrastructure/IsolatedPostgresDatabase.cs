using Npgsql;

namespace Vouch.IntegrationTests.Infrastructure;

public sealed class IsolatedPostgresDatabase : IAsyncDisposable
{
    private readonly string _adminConnection;
    private bool _created;
    public string Name { get; } = $"vouch_test_{Guid.NewGuid():N}";
    public string ConnectionString { get; }

    public IsolatedPostgresDatabase(string adminConnection)
    {
        var settings = ValidateAdminConnection(adminConnection);
        settings.Pooling = false;
        settings.Timeout = 10;
        _adminConnection = settings.ConnectionString;
        settings.Database = Name;
        ConnectionString = settings.ConnectionString;
    }

    public static NpgsqlConnectionStringBuilder ValidateAdminConnection(string connection)
    {
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Database != "vouch_test_admin" || settings.Username != "vouch_test_runner" ||
            settings.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Tests require a loopback-only dedicated PostgreSQL server, Database=vouch_test_admin and Username=vouch_test_runner. Never supply the application connection string.");
        return settings;
    }

    public async Task CreateAsync()
    {
        if (_created) throw new InvalidOperationException("The test database already exists.");
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        // Name is generated internally and contains only a fixed prefix plus hexadecimal GUID.
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{Name}\"", connection);
        await command.ExecuteNonQueryAsync();
        _created = true;
    }

    public async Task ResetDataAsync()
    {
        if (!_created) throw new InvalidOperationException("Cannot reset a database this run did not create.");
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var verify = new NpgsqlCommand("SELECT current_database()", connection);
        if ((string?)await verify.ExecuteScalarAsync() != Name)
            throw new InvalidOperationException("Test database identity mismatch.");
        // Preserve migrations; only clear application data inside this run's generated database.
        await using var reset = new NpgsqlCommand("TRUNCATE TABLE \"Campuses\", \"Users\", \"AmbassadorInvites\", \"Vouches\", \"Matches\", \"Reflections\", \"Conversations\", \"Messages\", \"Reports\", \"Blocks\", \"Icebreakers\" CASCADE", connection);
        await reset.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_created) return;
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{Name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        _created = false;
    }
}
