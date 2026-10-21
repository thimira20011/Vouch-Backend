using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common;
using Vouch.Application.Features.Auth;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.Services;

public static class AccountDeletionRequests
{
    public static async Task<AccountDeletionResponse> RequestAsync(ApplicationDbContext db, TimeProvider clock, Guid userId,
        CancellationToken ct = default, DateTimeOffset? restoredRequestTime = null)
    {
        var identity = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\"={identity.CampusId} FOR UPDATE", ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        await db.Entry(user).ReloadAsync(ct);
        if (user.CampusId != identity.CampusId) throw new EligibilityException("Account campus changed; retry the request.");
        var now = clock.GetUtcNow();
        var request = await db.AccountDeletions.SingleOrDefaultAsync(d => d.UserId == userId && d.CompletedAt == null, ct);
        var requested = user.DeletionRequestedAt ?? (user.Status == AccountStatus.DeletionRequested ? user.UpdatedAt ?? user.CreatedAt : now);
        if (requested > now) requested = now;
        if (restoredRequestTime.HasValue && restoredRequestTime < requested) requested = restoredRequestTime.Value;
        if (request is null)
        {
            request = new AccountDeletion { UserId = userId, CampusId = user.CampusId, RequestedAt = requested,
                DeadlineAt = requested.AddDays(30), NextAttemptAt = now, CreatedAt = now,
                RequestTimeInferred = user.Status == AccountStatus.DeletionRequested && user.DeletionRequestedAt == null };
            request.BackupSubjectId = DeletionTombstone.Encode(request.Id, userId);
            db.AccountDeletions.Add(request);
        }
        else if (requested < request.RequestedAt)
        {
            request.RequestedAt = requested; request.DeadlineAt = requested.AddDays(30);
        }
        user.Status = AccountStatus.DeletionRequested;
        user.DeletionRequestedAt ??= requested;
        await db.SaveChangesAsync(ct); // Session revocation and campus metrics share this transaction.
        await tx.CommitAsync(ct);
        return new(request.Id, request.RequestedAt, request.DeadlineAt);
    }
}
