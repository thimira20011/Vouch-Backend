using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vouch.Api.Configuration;

public static class DatabaseOperations
{
    public static async Task<int> RunAsync(IConfiguration config)
    {
        try
        {
            var encryption = new AesEncryptionService(config);
            var lookup = new EmailLookup(config);
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(RequiredConfiguration.Read(config, "ConnectionStrings:DefaultConnection")).Options;
            await using var db = new ApplicationDbContext(options, encryption, lookup);
            switch (config["database-task"])
            {
                case "inspect":
                    var baseline = await new LegacySchemaBaseline(db).IdentifyAsync();
                    var report = await new ProtectedDataUpgrade(db, encryption, lookup).RunAsync(rotateLookupKey: config.GetValue("rotate-lookup-key", false));
                    Console.WriteLine(JsonSerializer.Serialize(new { RecognizedMigrations = baseline, Data = report }, new JsonSerializerOptions { WriteIndented = true }));
                    return report.CanApply ? 0 : 2;
                case "upgrade":
                    var applied = await new DatabaseUpgrade(db, encryption, lookup).ApplyAsync(
                        config.GetValue("adopt-ensure-created", false), config.GetValue("backup-confirmed", false), config.GetValue("rotate-lookup-key", false));
                    Console.WriteLine(JsonSerializer.Serialize(applied));
                    return 0;
                case "seed":
                    await DatabaseReadinessCheck.VerifyAsync(db, lookup, encryption);
                    await using (var transaction = await db.Database.BeginTransactionAsync())
                    {
                        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72672604)");
                        await DbInitializer.SeedAsync(db);
                        await transaction.CommitAsync();
                    }
                    Console.WriteLine("Reference data seeded; no accounts created.");
                    return 0;
                case "provision-admin":
                    // Passwords are environment/secrets settings, never command-line arguments.
                    var password = Environment.GetEnvironmentVariable("VOUCH_BOOTSTRAP_PASSWORD");
                    if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Set VOUCH_BOOTSTRAP_PASSWORD only for this provisioning process.");
                    var resetPassword = config.GetValue("reset-admin-password", false);
                    if (resetPassword && !config.GetValue("backup-confirmed", false))
                        throw new InvalidOperationException("Password reset requires --backup-confirmed true after reviewing the existing account and recovery plan.");
                    var id = await new ArchitectProvisioner(db, lookup, encryption, new BCryptPasswordHasher()).ProvisionAsync(
                        RequiredConfiguration.Read(config, "Bootstrap:Email"), password,
                        RequiredConfiguration.Read(config, "Bootstrap:FullName"), RequiredConfiguration.Read(config, "Bootstrap:CampusCode"), resetPassword);
                    Console.WriteLine($"Architect provisioned/verified. UserId={id}");
                    return 0;
                case "export-deletions":
                case "replay-deletions":
                    return await RunDeletionLedgerAsync(config, db, lookup, encryption);
                default: throw new ArgumentException("database-task must be inspect, upgrade, seed, provision-admin, export-deletions or replay-deletions.");
            }
        }
        catch (Exception ex)
        {
            // Provider exceptions can include SQL/PII. Print only errors authored by these operations.
            Console.Error.WriteLine(SafeFailureMessage(ex));
            return 1;
        }
    }

    private static async Task<int> RunDeletionLedgerAsync(IConfiguration config, ApplicationDbContext db, EmailLookup lookup, AesEncryptionService encryption)
    {
        await DatabaseReadinessCheck.VerifyAsync(db, lookup, encryption);
        var path = Path.GetFullPath(RequiredConfiguration.Read(config, "deletion-ledger-path"));
        await db.Database.OpenConnectionAsync();
        await using var gate = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_lock(72672604)", (Npgsql.NpgsqlConnection)db.Database.GetDbConnection());
        if (!(bool)(await gate.ExecuteScalarAsync())!) throw new InvalidOperationException("Another database operation is running.");
        try
        {
            var processor = new AccountDeletionProcessor(db, TimeProvider.System, NullLogger<AccountDeletionProcessor>.Instance);
            if (config["database-task"] == "export-deletions")
            {
                var value = await processor.ExportRestoreLedgerAsync();
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(file);
                await writer.WriteAsync(value);
                Console.WriteLine("Encrypted deletion ledger exported. Keep it private with the matching retained key ring.");
            }
            else
            {
                if (!config.GetValue("backup-confirmed", false)) throw new InvalidOperationException("Restore replay requires --backup-confirmed true and stopped application writers.");
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 8 * 1024 * 1024) throw new ArgumentException("A valid encrypted deletion ledger file is required.");
                var count = await processor.ReplayRestoreLedgerAsync(await File.ReadAllTextAsync(path));
                Console.WriteLine($"Restore deletion replay completed. Accounts erased={count}. Verify readiness before enabling traffic.");
            }
            return 0;
        }
        finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(72672604)"); }
    }

    public static string SafeFailureMessage(Exception exception)
    {
        // Provider diagnostics can contain passwords, statements or protected fields.
        // Describe only known error categories/codes, including nested connection failures.
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is Npgsql.PostgresException postgres)
                return postgres.SqlState switch
                {
                    "28P01" => "Database authentication failed. Check the database password and pooler username in local configuration.",
                    "28000" => "Database login was rejected. Check the Supabase project/pooler username and connection mode.",
                    "3D000" => "The configured database does not exist. Check the database name in local configuration.",
                    _ => "PostgreSQL rejected the database operation. No provider details or protected values are printed; review schema and permissions."
                };
            if (cause is System.Security.Authentication.AuthenticationException)
                return "Database TLS certificate validation failed. Configure the trusted project root certificate; keep SSL Mode=VerifyFull.";
            if (cause is System.Net.Sockets.SocketException socket)
                return $"Database host could not be reached (socket error: {socket.SocketErrorCode}). Check DNS/network access and the Supabase connection host/port.";
            if (cause is TimeoutException)
                return "Database connection/operation timed out. Check Supabase project availability, pooler settings and network access.";
        }
        return exception is ArgumentException or InvalidOperationException or ProtectedDataException
            ? exception.Message : "Database operation failed. No protected values or credentials are printed. Check connectivity/schema and recover using the operations guide.";
    }
}
