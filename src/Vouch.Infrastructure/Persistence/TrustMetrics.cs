using Microsoft.EntityFrameworkCore;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Persistence;

public static class TrustMetrics
{
    // Weight is a submission-time snapshot. Eligibility is current; aging never reweights old vouches.
    public static IQueryable<VouchRecord> Qualifying(ApplicationDbContext db) => db.Vouches.Where(v =>
        v.VoucherUserId != v.TargetUserId && v.Traits != CharacterTrait.None && (v.Traits & ~LaunchEligibility.ValidTraits) == 0 &&
        v.VoucherUser.CampusId == v.TargetUser.CampusId && v.VoucherUser.Status == AccountStatus.Active &&
        v.VoucherUser.EmailVerifiedAt != null && v.VoucherUser.OnboardingCompletedAt != null &&
        v.TargetUser.EmailVerifiedAt != null && v.TargetUser.OnboardingCompletedAt != null &&
        (v.TargetUser.Status == AccountStatus.Active || v.TargetUser.Status == AccountStatus.InIncubation) &&
        !db.Blocks.Any(b => b.BlockerId == v.VoucherUserId && b.BlockedUserId == v.TargetUserId ||
                           b.BlockerId == v.TargetUserId && b.BlockedUserId == v.VoucherUserId));

    public static async Task RecalculateAsync(ApplicationDbContext db, Guid campusId, DateTimeOffset now, CancellationToken ct)
    {
        var qualifying = Qualifying(db);
        // Historical/imported edges can form a chain of newly eligible members. Complete it under the campus lock.
        while (await db.Users.Where(u => u.CampusId == campusId && u.Status == AccountStatus.InIncubation &&
            qualifying.Count(v => v.TargetUserId == u.Id) >= 3).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, AccountStatus.Active)
                .SetProperty(u => u.IncubationCompletedAt, u => u.IncubationCompletedAt ?? now), ct) > 0) { }

        await db.Users.Where(u => u.CampusId == campusId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.ActiveVouchesReceivedCount, u => qualifying.Count(v => v.TargetUserId == u.Id))
            .SetProperty(u => u.TrustScore, u => Math.Min(20.0, Math.Max(0.0, qualifying.Where(v => v.TargetUserId == u.Id)
                .Sum(v => (double?)v.FinalCalculatedWeight) ?? 0.0))), ct);

        await db.VouchRequests.Where(r => r.Requester.CampusId == campusId && r.Status == PeerVouchRequestStatus.Pending &&
            (r.Requester.CampusId != r.RequestedVoucher.CampusId || r.Requester.EmailVerifiedAt == null ||
             r.Requester.OnboardingCompletedAt == null || r.Requester.Status != AccountStatus.Active && r.Requester.Status != AccountStatus.InIncubation ||
             r.RequestedVoucher.Status != AccountStatus.Active || r.RequestedVoucher.EmailVerifiedAt == null || r.RequestedVoucher.OnboardingCompletedAt == null ||
             db.Blocks.Any(b => b.BlockerId == r.RequesterId && b.BlockedUserId == r.RequestedVoucherId ||
                               b.BlockerId == r.RequestedVoucherId && b.BlockedUserId == r.RequesterId)))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, PeerVouchRequestStatus.Cancelled).SetProperty(r => r.ResolvedAt, now), ct);

        // Keep entities loaded by existing services consistent with the authoritative SQL results.
        var trackedIds = db.ChangeTracker.Entries<User>().Where(e => e.State != EntityState.Deleted && e.Entity.CampusId == campusId)
            .Select(e => e.Entity.Id).ToArray();
        var metrics = await db.Users.AsNoTracking().Where(u => trackedIds.Contains(u.Id))
            .Select(u => new { u.Id, u.TrustScore, u.ActiveVouchesReceivedCount, u.Status, u.IncubationCompletedAt }).ToListAsync(ct);
        foreach (var entry in db.ChangeTracker.Entries<User>().Where(e => e.State != EntityState.Deleted))
        {
            var value = metrics.SingleOrDefault(u => u.Id == entry.Entity.Id);
            if (value is null) continue;
            entry.Entity.TrustScore = value.TrustScore;
            entry.Entity.ActiveVouchesReceivedCount = value.ActiveVouchesReceivedCount;
            entry.Entity.Status = value.Status;
            entry.Entity.IncubationCompletedAt = value.IncubationCompletedAt;
        }
    }
}
