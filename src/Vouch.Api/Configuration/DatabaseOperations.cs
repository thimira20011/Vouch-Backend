using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;

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
                default: throw new ArgumentException("database-task must be inspect, upgrade, seed or provision-admin.");
            }
        }
        catch (Exception ex)
        {
            // Provider exceptions can include SQL/PII. Print only errors authored by these operations.
            Console.Error.WriteLine(ex is ArgumentException or InvalidOperationException or ProtectedDataException
                ? ex.Message : "Database operation failed. No protected values or credentials are printed. Check connectivity/schema and recover using the operations guide.");
            return 1;
        }
    }
}
