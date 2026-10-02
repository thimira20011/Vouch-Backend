using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vouch.Application.Common.Interfaces;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Persistence;

public sealed class DatabaseUpgrade(ApplicationDbContext db, AesEncryptionService encryption, IEmailLookup lookup)
{
    public async Task<UpgradeReport> ApplyAsync(bool adoptEnsureCreated = false, bool backupConfirmed = false, bool rotateLookupKey = false, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        // All deployment/upgrade commands share a session lock. Stop API writers before any upgrade.
        await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(72672604)", connection);
        if (!(bool)(await acquire.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Another database operation is running. Retry after it completes.");
        try
        {
            var baseline = new LegacySchemaBaseline(db);
            var identified = await baseline.IdentifyAsync(ct);
            var untracked = identified.Length > 0 && !(await db.Database.GetAppliedMigrationsAsync(ct)).Any();
            if (untracked && !adoptEnsureCreated) throw new InvalidOperationException("A recognized EnsureCreated schema requires --adopt-ensure-created true after backup and review.");
            var upgrade = new ProtectedDataUpgrade(db, encryption, lookup);
            var preflight = await upgrade.RunAsync(rotateLookupKey: rotateLookupKey, ct: ct);
            if (!preflight.CanApply) throw new InvalidOperationException("Data preflight failed. Run inspect and resolve the listed row IDs before applying.");
            if ((identified.Length > 0 || preflight.RowsInspected > 0) && !backupConfirmed)
                throw new InvalidOperationException("Existing databases require --backup-confirmed true after a verified backup/restore rehearsal.");
            if (untracked) await baseline.AdoptAsync(identified, ct);
            await db.Database.MigrateAsync(ct);
            return await upgrade.RunAsync(apply: true, rotateLookupKey: rotateLookupKey, ct: ct);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(72672604)", connection);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }
}
