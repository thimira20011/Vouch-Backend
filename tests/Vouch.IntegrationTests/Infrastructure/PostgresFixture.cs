using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.Persistence;

namespace Vouch.IntegrationTests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    public IsolatedPostgresDatabase Database { get; private set; } = null!;
    public bool FirstMigrationApplied { get; private set; }
    public static readonly IConfiguration TestSettings = new ConfigurationBuilder()
        .AddInMemoryCollection(VouchApiFactory.Settings("unused")).Build();
    public static readonly AesEncryptionService Encryption = new(TestSettings);
    public static readonly EmailLookup Lookup = new(TestSettings);
    public ApplicationDbContext CreateContext() => new(Options, Encryption, Lookup);
    public DbContextOptions<ApplicationDbContext> Options => new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(Database.ConnectionString).Options;

    public async Task InitializeAsync()
    {
        var admin = Environment.GetEnvironmentVariable("VOUCH_TEST_POSTGRES_ADMIN")
            ?? throw new InvalidOperationException("Set VOUCH_TEST_POSTGRES_ADMIN for a dedicated test PostgreSQL server. See tests/Vouch.IntegrationTests/README.md.");
        Database = new IsolatedPostgresDatabase(admin);
        try
        {
            await Database.CreateAsync();
            await using var db = CreateContext();
            await db.GetService<IMigrator>().MigrateAsync("20260917123615_AddAmbassadorInvites");
            FirstMigrationApplied = (await db.Database.GetAppliedMigrationsAsync()).Count() == 1;
            await db.Database.MigrateAsync();
            await new ProtectedDataUpgrade(db, Encryption, Lookup).RunAsync(apply: true);
        }
        catch
        {
            await Database.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (Database is not null) await Database.DisposeAsync();
    }
}

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture> { }
