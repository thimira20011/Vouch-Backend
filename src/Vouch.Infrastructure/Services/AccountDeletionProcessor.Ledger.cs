using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.Services;

public sealed partial class AccountDeletionProcessor
{
    private const string LedgerPurpose = "AccountDeletions.RestoreLedger.v1";
    private sealed record RestoreEntry(Guid UserId, DateTimeOffset RequestedAt);
    private sealed record RestoreLedger(int Version, DateTimeOffset ExportedAt, DateTimeOffset ExpiresAt, RestoreEntry[] Entries);

    // Offline operator-only operations: no identity-bearing ledger is exposed over HTTP/logs.
    public async Task<string> ExportRestoreLedgerAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var jobs = await db.AccountDeletions.AsNoTracking().Where(d => d.CompletedAt == null || d.DeadlineAt > now)
            .Select(d => new { d.Id, d.UserId, d.BackupSubjectId, d.RequestedAt, d.CompletedAt, d.DeadlineAt }).ToListAsync(ct);
        var entries = jobs.Select(d => new RestoreEntry(d.UserId ?? DeletionTombstone.Decode(d.BackupSubjectId
            ?? throw new InvalidOperationException("Restore tombstone is missing; keep restored data offline."), d.Id), d.RequestedAt)).ToList();
        var missing = await db.Users.AsNoTracking().Where(u => (u.Status == AccountStatus.DeletionRequested || u.DeletionRequestedAt != null) &&
            !db.AccountDeletions.Any(d => d.UserId == u.Id && d.CompletedAt == null))
            .Select(u => new { u.Id, RequestedAt = u.DeletionRequestedAt ?? u.UpdatedAt ?? u.CreatedAt }).ToListAsync(ct);
        entries.AddRange(missing.Select(u => new RestoreEntry(u.Id, u.RequestedAt > now ? now : u.RequestedAt)));
        if (entries.Count > 10_000) throw new InvalidOperationException("Restore ledger exceeds the supported batch; keep the restore offline and review a paged recovery.");
        var expires = jobs.Where(d => d.CompletedAt != null).Select(d => d.DeadlineAt).Append(now.AddDays(1)).Min();
        return db.Encryption.Encrypt(JsonSerializer.Serialize(new RestoreLedger(1, now, expires,
            entries.GroupBy(e => e.UserId).Select(g => g.OrderBy(e => e.RequestedAt).First()).ToArray())), LedgerPurpose);
    }

    public async Task<int> ReplayRestoreLedgerAsync(string encryptedLedger, CancellationToken ct = default)
    {
        if (encryptedLedger.Length > 8 * 1024 * 1024) throw new ArgumentException("Restore ledger is too large.");
        RestoreLedger ledger;
        try { ledger = JsonSerializer.Deserialize<RestoreLedger>(db.Encryption.Decrypt(encryptedLedger, LedgerPurpose))
            ?? throw new ArgumentException("Invalid restore ledger."); }
        catch (JsonException) { throw new ArgumentException("Invalid restore ledger."); }
        var now = clock.GetUtcNow();
        if (ledger.Version != 1 || ledger.ExportedAt > now.AddMinutes(5) || ledger.ExpiresAt <= now || ledger.ExpiresAt > ledger.ExportedAt.AddDays(1) ||
            ledger.Entries is null || ledger.Entries.Length > 10_000 || ledger.Entries.Any(e => e is null || e.UserId == Guid.Empty || e.RequestedAt > ledger.ExportedAt))
            throw new ArgumentException("Invalid or expired restore ledger; export a fresh ledger from the authoritative database.");
        var ids = ledger.Entries.Select(e => e.UserId).Distinct().ToArray();
        var present = await db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        foreach (var entry in ledger.Entries.GroupBy(e => e.UserId).Select(g => g.OrderBy(e => e.RequestedAt).First()).Where(e => present.Contains(e.UserId)))
        {
            await AccountDeletionRequests.RequestAsync(db, clock, entry.UserId, ct, entry.RequestedAt.ToUniversalTime());
            db.ChangeTracker.Clear();
        }
        // Restartable: completed/missing users are skipped, failed attempts retain their queue rows.
        while (await ProcessDueAsync(100, ct) > 0) { }
        if (await db.Users.AnyAsync(u => ids.Contains(u.Id) || u.Status == AccountStatus.DeletionRequested || u.DeletionRequestedAt != null, ct) ||
            await db.AccountDeletions.AnyAsync(d => d.CompletedAt == null, ct))
            throw new InvalidOperationException("Restore erasure replay is incomplete; keep application traffic disabled and retry after resolving failures.");
        return present.Count;
    }
}
