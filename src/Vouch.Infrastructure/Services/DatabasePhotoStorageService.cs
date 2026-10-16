using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public sealed class DatabasePhotoStorageService(ApplicationDbContext db, PhotoProcessingService processor, TimeProvider clock) : IPhotoStorageService
{
    public async Task<(string OriginalUrl, string AbstractUrl)> StorePhotoAsync(Guid userId, Stream stream, string contentType, CancellationToken ct = default)
    {
        var identity = await RequireOwnerAsync(userId, ct);
        // Decode outside the transaction; account/campus permissions are checked again after taking the lock.
        var image = await processor.ProcessAsync(stream, contentType, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(identity.CampusId, false, ct);
        var owner = await RequireOwnerAsync(userId, ct);
        if (owner.CampusId != identity.CampusId) throw new EligibilityException("Account campus changed; retry the upload.");
        var asset = new PhotoAsset { OwnerId = userId, Width = image.Width, Height = image.Height,
            OriginalJpeg = "", AbstractJpeg = "", QuarterJpeg = "", SixtyJpeg = "", CreatedAt = clock.GetUtcNow() };
        asset.OriginalJpeg = PhotoPayload.Encode(image.Original, userId, asset.Id, RevealClarityStage.Full100);
        asset.AbstractJpeg = PhotoPayload.Encode(image.Abstract, userId, asset.Id, RevealClarityStage.Abstract0);
        asset.QuarterJpeg = PhotoPayload.Encode(image.Quarter, userId, asset.Id, RevealClarityStage.Quarter25);
        asset.SixtyJpeg = PhotoPayload.Encode(image.Sixty, userId, asset.Id, RevealClarityStage.Sixty60);
        await db.PhotoAssets.Where(p => p.OwnerId == userId).ExecuteDeleteAsync(ct);
        foreach (var old in db.ChangeTracker.Entries<PhotoAsset>().Where(e => e.Entity.OwnerId == userId).ToArray()) old.State = EntityState.Detached;
        db.PhotoAssets.Add(asset);
        var tracked = await db.Users.SingleAsync(u => u.Id == userId, ct); await db.Entry(tracked).ReloadAsync(ct);
        tracked.OriginalPhotoUrl = PhotoUrls.Create(userId, asset.Id, RevealClarityStage.Full100);
        tracked.OilPaintingAbstractPhotoUrl = PhotoUrls.Create(userId, asset.Id, RevealClarityStage.Abstract0);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return (tracked.OriginalPhotoUrl, tracked.OilPaintingAbstractPhotoUrl);
    }

    public async Task DeletePhotoAsync(Guid userId, CancellationToken ct = default)
    {
        var identity = await RequireOwnerAsync(userId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(identity.CampusId, false, ct);
        var owner = await RequireOwnerAsync(userId, ct);
        if (owner.CampusId != identity.CampusId) throw new EligibilityException("Account campus changed; retry the request.");
        await db.PhotoAssets.Where(p => p.OwnerId == userId).ExecuteDeleteAsync(ct);
        foreach (var old in db.ChangeTracker.Entries<PhotoAsset>().Where(e => e.Entity.OwnerId == userId).ToArray()) old.State = EntityState.Detached;
        var tracked = await db.Users.SingleAsync(u => u.Id == userId, ct); await db.Entry(tracked).ReloadAsync(ct);
        tracked.OriginalPhotoUrl = tracked.OilPaintingAbstractPhotoUrl = null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<byte[]?> ReadPhotoAsync(Guid viewerId, Guid ownerId, Guid generationId, RevealClarityStage stage, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(stage)) throw new ArgumentException("Unknown photo stage.");
        var identity = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ownerId, ct);
        if (identity is null) return null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(identity.CampusId, true, ct);
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ownerId && u.CampusId == identity.CampusId, ct);
        if (owner is null) return null;
        if (viewerId == ownerId) await RequireOwnerAsync(viewerId, ct);
        else
        {
            await ResourceAccess.RequirePeerAsync(db, viewerId, ownerId, ct: ct);
            var conversations = ResourceAccess.VisibleConversations(db, viewerId).Where(c => c.UserAId == ownerId || c.UserBId == ownerId);
            var allowed = stage switch
            {
                RevealClarityStage.Full100 => await conversations.AnyAsync(c => c.CurrentClarityStage == RevealClarityStage.Full100, ct),
                RevealClarityStage.Sixty60 => await conversations.AnyAsync(c => c.CurrentClarityStage == RevealClarityStage.Sixty60 || c.CurrentClarityStage == RevealClarityStage.Full100, ct),
                RevealClarityStage.Quarter25 => await conversations.AnyAsync(c => c.CurrentClarityStage == RevealClarityStage.Quarter25 || c.CurrentClarityStage == RevealClarityStage.Sixty60 || c.CurrentClarityStage == RevealClarityStage.Full100, ct),
                _ => await conversations.AnyAsync(ct) || await db.Matches.AnyAsync(m => m.CampusId == owner.CampusId && m.CycleDate == DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) &&
                    (m.Status == MatchStatus.Pending || m.Status == MatchStatus.Accepted) && !m.UserA.IsSoftHiddenFromMatchmaking && !m.UserB.IsSoftHiddenFromMatchmaking &&
                    (m.UserAId == viewerId && m.UserBId == ownerId || m.UserBId == viewerId && m.UserAId == ownerId), ct)
            };
            if (!allowed) throw new EligibilityException("Photo stage is unavailable.");
        }
        if (owner.OriginalPhotoUrl != PhotoUrls.Create(ownerId, generationId, RevealClarityStage.Full100)) return null;
        var query = db.PhotoAssets.AsNoTracking().Where(p => p.OwnerId == ownerId && p.Id == generationId);
        // Select/decrypt exactly the authorized stage, never the original for a masked request.
        var value = await (stage switch
        {
            RevealClarityStage.Full100 => query.Select(p => p.OriginalJpeg), RevealClarityStage.Quarter25 => query.Select(p => p.QuarterJpeg),
            RevealClarityStage.Sixty60 => query.Select(p => p.SixtyJpeg), _ => query.Select(p => p.AbstractJpeg)
        }).SingleOrDefaultAsync(ct);
        var bytes = value is null ? null : PhotoPayload.Decode(value, ownerId, generationId, stage);
        await tx.CommitAsync(ct);
        return bytes;
    }

    private async Task<User> RequireOwnerAsync(Guid ownerId, CancellationToken ct)
    {
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ownerId, ct) ?? throw new KeyNotFoundException("Account not found.");
        SessionService.EnsureUsable(owner);
        if (!UniversityIdentity.IsOnboarded(owner)) throw new EligibilityException("Complete verified onboarding before managing photos.");
        return owner;
    }
    private Task LockCampusAsync(Guid campus, bool shared, CancellationToken ct) => shared
        ? db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {campus} FOR SHARE", ct)
        : db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {campus} FOR UPDATE", ct);
}
