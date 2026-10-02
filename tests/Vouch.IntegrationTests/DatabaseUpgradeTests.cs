using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Vouch.Application.Features.Auth;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.Services;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class DatabaseUpgradeTests : IAsyncLifetime
{
    private IsolatedPostgresDatabase _database = null!;
    private Guid _campusId;
    private ApplicationDbContext Context(AesEncryptionService? encryption = null, EmailLookup? lookup = null)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_database.ConnectionString).Options,
            encryption ?? PostgresFixture.Encryption, lookup ?? PostgresFixture.Lookup);
    private DatabaseUpgrade Upgrade(ApplicationDbContext db) => new(db, PostgresFixture.Encryption, PostgresFixture.Lookup);

    public async Task InitializeAsync()
    {
        _database = new(Environment.GetEnvironmentVariable("VOUCH_TEST_POSTGRES_ADMIN")!);
        await _database.CreateAsync();
    }
    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task FreshInstall_SeedProvisionAndLoginAreRepeatable()
    {
        await using var db = Context();
        Assert.Empty((await new LegacySchemaBaseline(db).IdentifyAsync()));
        Assert.True((await Upgrade(db).ApplyAsync()).CanApply);
        await DbInitializer.SeedAsync(db);
        await DbInitializer.SeedAsync(db);
        Assert.Equal(4, await db.Campuses.CountAsync());
        Assert.Empty(await db.Users.ToListAsync());
        var provision = new ArchitectProvisioner(db, PostgresFixture.Lookup, PostgresFixture.Encryption, new BCryptPasswordHasher());
        var id = await provision.ProvisionAsync("admin@example.org", "AdminTestPassword123!", "Test Architect", "SUSL");
        Assert.Equal(id, await provision.ProvisionAsync(" ADMIN@EXAMPLE.ORG ", "AdminTestPassword123!", "Test Architect", "SUSL"));
        var auth = new AuthService(db, new BCryptPasswordHasher(), new JwtTokenService(PostgresFixture.TestSettings), PostgresFixture.Lookup, new DisabledPhotoStorage());
        Assert.Equal("Architect", (await auth.LoginAsync(new("admin@example.org", "AdminTestPassword123!"))).Role);
        Assert.True((await Upgrade(db).ApplyAsync(backupConfirmed: true)).CanApply);
        Assert.Single(await db.Users.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provision.ProvisionAsync("other@example.org", "AdminTestPassword123!", "Other", "SUSL"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provision.ProvisionAsync("admin@example.org", "ReplacementPassword123!", "Other", "SUSL"));
        Assert.Equal(id, await provision.ProvisionAsync("admin@example.org", "ReplacementPassword123!", "Other", "SUSL", resetExistingPassword: true));
        Assert.Equal("Architect", (await auth.LoginAsync(new("admin@example.org", "ReplacementPassword123!"))).Role);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.LoginAsync(new("admin@example.org", "AdminTestPassword123!")));
    }

    [Theory]
    [InlineData(LegacySchemaBaseline.Initial)]
    [InlineData(LegacySchemaBaseline.BeforeProtection)]
    public async Task EnsureCreatedSchemas_AreRecognizedAdoptedAndPreservePlaintextAndCbcRows(string migrationId)
    {
        await CreateLegacyAsync(migrationId, ensureCreated: true);
        var plainId = await InsertLegacyAsync("admin@test.ac.lk", "Plain Admin", encrypted: false);
        var cipherId = await InsertLegacyAsync("seeker@test.ac.lk", "Legacy Seeker", encrypted: true);
        await using var db = Context();
        var baseline = await new LegacySchemaBaseline(db).IdentifyAsync();
        Assert.Equal(migrationId, baseline[^1]);
        var dryRun = await new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync();
        Assert.True(dryRun.CanApply);
        Assert.Equal(2, dryRun.RowsToRewrite);
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade(db).ApplyAsync(backupConfirmed: true));
        await Upgrade(db).ApplyAsync(adoptEnsureCreated: true, backupConfirmed: true);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("Plain Admin", (await db.Users.SingleAsync(u => u.Id == plainId)).FullName);
        Assert.Equal("Legacy Seeker", (await db.Users.SingleAsync(u => u.Id == cipherId)).FullName);
        Assert.Equal("Legacy biography", (await db.Users.SingleAsync(u => u.Id == cipherId)).Bio);
        await DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption);
        Assert.Equal(0, (await new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync()).RowsToRewrite);
    }

    [Fact]
    public async Task DuplicateNormalizedLegacyEmails_ProducePrivateReportAndNoChanges()
    {
        await CreateLegacyAsync();
        var first = await InsertLegacyAsync("Same@test.ac.lk", "First", true);
        var second = await InsertLegacyAsync("same@test.ac.lk", "Second", true);
        await using var db = Context();
        var report = await new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync();
        Assert.False(report.CanApply);
        Assert.Equal(new[] { first, second }.Order(), report.EmailCollisions.Single().Order());
        Assert.DoesNotContain("same@test.ac.lk", System.Text.Json.JsonSerializer.Serialize(report), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade(db).ApplyAsync(backupConfirmed: true));
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task WrongLegacyKey_BlocksBeforeSchemaChangeAndCorrectKeyCanRecover()
    {
        await CreateLegacyAsync();
        var id = await InsertLegacyAsync("legacy@test.ac.lk", "Legacy User", true);
        var wrong = new AesEncryptionService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:EncryptionKey"] = "wrong-but-valid-legacy-key-32-characters" }).Build());
        await using var db = Context(wrong);
        var report = await new ProtectedDataUpgrade(db, wrong, PostgresFixture.Lookup).RunAsync();
        Assert.False(report.CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseUpgrade(db, wrong, PostgresFixture.Lookup).ApplyAsync(backupConfirmed: true));
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        await using var correct = Context();
        await Upgrade(correct).ApplyAsync(backupConfirmed: true);
        Assert.Equal("legacy@test.ac.lk", (await correct.Users.SingleAsync(u => u.Id == id)).Email);
    }

    [Fact]
    public async Task UnknownEnsureCreatedSchema_IsRejectedWithoutBaseline()
    {
        await CreateLegacyAsync(ensureCreated: true);
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ADD COLUMN \"UnexpectedColumn\" text");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade(db).ApplyAsync(adoptEnsureCreated: true, backupConfirmed: true));
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task InterruptedExpansion_IsUnreadyAndBackfillCanBeRetriedAtomically()
    {
        await CreateLegacyAsync();
        var first = await InsertLegacyAsync("one@test.ac.lk", "First", false);
        var second = await InsertLegacyAsync("two@test.ac.lk", "Second", false);
        await using var db = Context();
        await db.Database.MigrateAsync(); // Simulate a stopped deployment after schema expansion.
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption));
        await RawAsync("UPDATE \"Users\" SET \"FullName\" = 'vouch:v1:v1:invalid' WHERE \"Id\" = @id", second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync(apply: true));
        Assert.Equal("one@test.ac.lk", await ScalarAsync("SELECT \"Email\" FROM \"Users\" WHERE \"Id\" = @id", first));
        await RawAsync("UPDATE \"Users\" SET \"FullName\" = 'Second' WHERE \"Id\" = @id", second);
        await Upgrade(db).ApplyAsync(backupConfirmed: true);
        await DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption);
        Assert.StartsWith("vouch:v1:", (string)(await ScalarAsync("SELECT \"Email\" FROM \"Users\" WHERE \"Id\" = @id", first))!);
    }

    [Fact]
    public async Task MissingSchemaOrChangedLookupKey_CannotPassReadiness()
    {
        await using var db = Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption));
        await Upgrade(db).ApplyAsync();
        var changed = new EmailLookup(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:EmailLookupKey"] = "a-different-lookup-key-at-least-48-characters" }).Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadinessCheck.VerifyAsync(db, changed, PostgresFixture.Encryption));
    }

    [Fact]
    public async Task ExistingDatabase_RequiresBackupAcknowledgment()
    {
        await CreateLegacyAsync();
        await InsertLegacyAsync("backup@test.ac.lk", "Backup User", false);
        await using var db = Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade(db).ApplyAsync());
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact]
    public async Task EncryptionAndLookupRotation_AreExplicitPreserveLoginAndDoNotReuseOldModelKeys()
    {
        await using var db = Context();
        await Upgrade(db).ApplyAsync();
        await DbInitializer.SeedAsync(db);
        await new ArchitectProvisioner(db, PostgresFixture.Lookup, PostgresFixture.Encryption, new BCryptPasswordHasher())
            .ProvisionAsync("rotate@example.org", "AdminTestPassword123!", "Rotation Architect", "SUSL");
        var rotatedSettings = new ConfigurationBuilder().AddConfiguration(PostgresFixture.TestSettings)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ActiveEncryptionKeyId"] = "v2", ["Security:EncryptionKeys:v2"] = "new-integration-encryption-key-48-characters"
            }).Build();
        var encryption = new AesEncryptionService(rotatedSettings);
        await using var rotated = Context(encryption);
        await new DatabaseUpgrade(rotated, encryption, PostgresFixture.Lookup).ApplyAsync(backupConfirmed: true);
        Assert.Equal("rotate@example.org", (await rotated.Users.AsNoTracking().SingleAsync()).Email);
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ProtectedDataException>(() => db.Users.AsNoTracking().SingleAsync());
        var settings = new ConfigurationBuilder().AddConfiguration(rotatedSettings).AddInMemoryCollection(new Dictionary<string, string?>
            { ["Security:EmailLookupKey"] = "rotated-independent-email-lookup-key-48-characters" }).Build();
        var lookup = new EmailLookup(settings);
        await using var next = Context(encryption, lookup);
        Assert.False((await new ProtectedDataUpgrade(next, encryption, lookup).RunAsync()).CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseUpgrade(next, encryption, lookup).ApplyAsync(backupConfirmed: true));
        await new DatabaseUpgrade(next, encryption, lookup).ApplyAsync(backupConfirmed: true, rotateLookupKey: true);
        await DatabaseReadinessCheck.VerifyAsync(next, lookup, encryption);
        var auth = new AuthService(next, new BCryptPasswordHasher(), new JwtTokenService(PostgresFixture.TestSettings), lookup, new DisabledPhotoStorage());
        Assert.Equal("Architect", (await auth.LoginAsync(new(" ROTATE@EXAMPLE.ORG ", "AdminTestPassword123!"))).Role);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadinessCheck.VerifyAsync(next, PostgresFixture.Lookup, encryption));
    }

    [Fact]
    public async Task CompletedConversion_DoesNotSilentlyAdoptTamperedPlaintext()
    {
        await using var db = Context();
        await Upgrade(db).ApplyAsync();
        await DbInitializer.SeedAsync(db);
        var id = await new ArchitectProvisioner(db, PostgresFixture.Lookup, PostgresFixture.Encryption, new BCryptPasswordHasher())
            .ProvisionAsync("protected@example.org", "AdminTestPassword123!", "Protected Architect", "SUSL");
        await RawAsync("UPDATE \"Users\" SET \"FullName\" = 'Tampered plaintext' WHERE \"Id\" = @id", id);
        var report = await new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync();
        Assert.False(report.CanApply);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade(db).ApplyAsync(backupConfirmed: true));
        Assert.Equal("Tampered plaintext", await ScalarAsync("SELECT \"FullName\" FROM \"Users\" WHERE \"Id\" = @id", id));
    }

    [Fact]
    public async Task Bootstrap_DoesNotPromoteExistingOrdinaryAccount()
    {
        await using var db = Context();
        await Upgrade(db).ApplyAsync();
        await DbInitializer.SeedAsync(db);
        var campus = await db.Campuses.FirstAsync();
        db.Users.Add(new Vouch.Domain.Entities.User
        {
            CampusId = campus.Id, Email = "ordinary@example.org", FullName = "Ordinary", Faculty = "Science", Department = "Computing",
            PasswordHash = new BCryptPasswordHasher().HashPassword("AdminTestPassword123!"), AcademicYear = 1
        });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ArchitectProvisioner(db, PostgresFixture.Lookup, PostgresFixture.Encryption, new BCryptPasswordHasher())
            .ProvisionAsync("ordinary@example.org", "AdminTestPassword123!", "Architect", campus.Code));
        Assert.Equal(Vouch.Domain.Enums.UserRole.Seeker, (await db.Users.SingleAsync()).Role);
    }

    [Fact]
    public async Task DatabaseCommands_RunAsProcessesWithoutApiStartupOrSecretOutput()
    {
        var inspect = await CommandAsync("inspect");
        Assert.Equal(0, inspect.Exit);
        Assert.Contains("RowsInspected", inspect.Output);
        Assert.Equal(0, (await CommandAsync("upgrade")).Exit);
        Assert.Equal(0, (await CommandAsync("seed")).Exit);
        var provision = await CommandAsync("provision-admin");
        Assert.Equal(0, provision.Exit);
        Assert.Contains("Architect provisioned/verified", provision.Output);
        Assert.Equal(0, (await CommandAsync("provision-admin")).Exit);
        await using var db = Context();
        var auth = new AuthService(db, new BCryptPasswordHasher(), new JwtTokenService(PostgresFixture.TestSettings), PostgresFixture.Lookup, new DisabledPhotoStorage());
        Assert.Equal("Architect", (await auth.LoginAsync(new("process-admin@example.org", "PrivateProcessPassword123!"))).Role);
        Assert.Equal(1, (await CommandAsync("unknown-operation")).Exit);
        Assert.Equal(1, (await CommandAsync("upgrade")).Exit); // Existing schema requires backup acknowledgement.
    }

    [Fact]
    public async Task NonPublicSchema_IsRejectedBeforeAnyUpgrade()
    {
        await using var db = Context();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA other; SET search_path TO other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upgrade(db).ApplyAsync());
        await db.Database.ExecuteSqlRawAsync("SET search_path TO public");
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task IdempotentMigrationSql_AppliesTwiceAndStillRequiresProtectedDataBackfill()
    {
        await CreateLegacyAsync();
        await InsertLegacyAsync("script@test.ac.lk", "Script User", false);
        await using var db = Context();
        var script = db.GetService<IMigrator>().GenerateScript(LegacySchemaBaseline.BeforeProtection, options: MigrationsSqlGenerationOptions.Idempotent);
        await db.Database.ExecuteSqlRawAsync(script);
        await db.Database.ExecuteSqlRawAsync(script);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption));
        await Upgrade(db).ApplyAsync(backupConfirmed: true);
        await DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption);
    }

    [Fact]
    public async Task RecognizedEnsureCreatedWithEmptyHistory_CanRecoverFromFailedMigrationAttempt()
    {
        await CreateLegacyAsync(ensureCreated: true);
        await InsertLegacyAsync("retry@test.ac.lk", "Retry User", false);
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"))
            """);
        await Upgrade(db).ApplyAsync(adoptEnsureCreated: true, backupConfirmed: true);
        Assert.Equal("retry@test.ac.lk", (await db.Users.SingleAsync()).Email);
        await DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption);
    }

    private async Task<(int Exit, string Output)> CommandAsync(string operation)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--database-task");
        start.ArgumentList.Add(operation);
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("Security__", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Bootstrap__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        foreach (var value in VouchApiFactory.Settings(_database.ConnectionString)) start.Environment[value.Key.Replace(":", "__")] = value.Value;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["Bootstrap__Email"] = "process-admin@example.org";
        start.Environment["Bootstrap__FullName"] = "Process Architect";
        start.Environment["Bootstrap__CampusCode"] = "SUSL";
        start.Environment["VOUCH_BOOTSTRAP_PASSWORD"] = "PrivateProcessPassword123!";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + await stderr;
        Assert.DoesNotContain("PrivateProcessPassword123!", output);
        Assert.DoesNotContain(PostgresFixture.TestSettings["Security:EncryptionKey"]!, output);
        Assert.DoesNotContain(PostgresFixture.TestSettings["Security:EmailLookupKey"]!, output);
        Assert.DoesNotContain("Now listening", output);
        return (process.ExitCode, output);
    }

    private async Task CreateLegacyAsync(string migrationId = LegacySchemaBaseline.BeforeProtection, bool ensureCreated = false)
    {
        await using var db = Context();
        if (ensureCreated)
        {
            var assembly = db.GetService<IMigrationsAssembly>();
            var target = assembly.CreateMigration(assembly.Migrations[migrationId], db.Database.ProviderName!).TargetModel;
            var model = db.GetService<IModelRuntimeInitializer>().Initialize(target, designTime: false);
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_database.ConnectionString).UseModel(model).Options;
            await using var legacy = new ApplicationDbContext(options, PostgresFixture.Encryption, PostgresFixture.Lookup);
            Assert.True(await legacy.Database.EnsureCreatedAsync()); // Isolated generated TEST database only.
        }
        else await db.GetService<IMigrator>().MigrateAsync(migrationId);
        _campusId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO "Campuses" ("Id", "Name", "Code", "DomainPattern", "IsSoftLaunchUnlocked", "RequiredAmbassadorsForLaunch", "RequiredVouchesPerAmbassador", "LaunchReadinessScore", "CreatedAt")
            VALUES (@id, 'Legacy Campus', 'TEST', '@test.ac.lk', true, 30, 2, 0, @now)
            """, connection);
        insert.Parameters.AddWithValue("id", _campusId);
        insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<Guid> InsertLegacyAsync(string email, string name, bool encrypted)
    {
        string LegacyEncrypt(string value)
        {
            using var aes = Aes.Create();
            aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(PostgresFixture.TestSettings["Security:EncryptionKey"]!));
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            var plain = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(aes.IV.Concat(encryptor.TransformFinalBlock(plain, 0, plain.Length)).ToArray());
        }
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO "Users" ("Id", "CampusId", "Email", "PasswordHash", "FullName", "Role", "Status", "HasFoundingMemberBadge", "Faculty", "Department", "AcademicYear", "Bio", "DeepValues", "IntellectualInterests", "TrustScore", "ActiveVouchesReceivedCount", "IsSoftHiddenFromMatchmaking", "UpheldReportsCount", "CreatedAt")
            VALUES (@id, @campus, @email, @password, @name, 1, 2, false, 'Science', 'Computing', 2, @bio, '[]', '[]', 0, 0, false, 0, @now)
            """, connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("campus", _campusId);
        insert.Parameters.AddWithValue("email", encrypted ? LegacyEncrypt(email) : email);
        insert.Parameters.AddWithValue("name", encrypted ? LegacyEncrypt(name) : name);
        insert.Parameters.AddWithValue("bio", encrypted ? LegacyEncrypt("Legacy biography") : "Legacy biography");
        insert.Parameters.AddWithValue("password", new BCryptPasswordHasher().HashPassword("LegacyPassword123!"));
        insert.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    private async Task RawAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
    private async Task<object?> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync();
    }
}
