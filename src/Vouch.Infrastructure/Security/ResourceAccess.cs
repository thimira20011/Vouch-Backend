using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;

namespace Vouch.Infrastructure.Security;

public static class ResourceAccess
{
    public static Task<bool> IsBlockedAsync(IApplicationDbContext db, Guid first, Guid second, CancellationToken ct = default)
        => db.Blocks.AnyAsync(b => b.BlockerId == first && b.BlockedUserId == second || b.BlockerId == second && b.BlockedUserId == first, ct);

    public static async Task<User> RequirePeerAsync(IApplicationDbContext db, Guid viewerId, Guid targetId,
        bool safetyAction = false, bool allowIncubation = false, CancellationToken ct = default)
    {
        var viewer = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == viewerId, ct)
            ?? throw new EligibilityException("Resource is unavailable.");
        SessionService.EnsureUsable(viewer);
        var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == targetId, ct);
        if (target is null || viewer.CampusId != target.CampusId || viewerId == targetId ||
            target.Status is not (AccountStatus.Active or AccountStatus.InIncubation))
            throw new EligibilityException("Resource is unavailable.");
        if (!safetyAction)
        {
            MemberEligibility.RequireActive(viewer);
            if (!UniversityIdentity.IsOnboarded(target) || !allowIncubation && target.Status != AccountStatus.Active ||
                await IsBlockedAsync(db, viewerId, targetId, ct)) throw new EligibilityException("Resource is unavailable.");
        }
        return target;
    }

    public static async Task<bool> CanPeerAsync(IApplicationDbContext db, Guid viewerId, Guid targetId, CancellationToken ct = default)
    {
        try { await RequirePeerAsync(db, viewerId, targetId, ct: ct); return true; }
        catch (EligibilityException) { return false; }
    }

    public static async Task<Conversation> RequireConversationAsync(IApplicationDbContext db, Guid userId, Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.Id == conversationId &&
            (c.UserAId == userId || c.UserBId == userId), ct) ?? throw new EligibilityException("Conversation is unavailable.");
        await RequirePeerAsync(db, userId, conversation.UserAId == userId ? conversation.UserBId : conversation.UserAId, ct: ct);
        return conversation;
    }

    public static IQueryable<Conversation> VisibleConversations(IApplicationDbContext db, Guid userId)
        => db.Conversations.Where(c => (c.UserAId == userId || c.UserBId == userId) &&
            c.UserA.CampusId == c.UserB.CampusId && c.UserA.Status == AccountStatus.Active && c.UserB.Status == AccountStatus.Active &&
            c.UserA.EmailVerifiedAt != null && c.UserA.OnboardingCompletedAt != null && c.UserB.EmailVerifiedAt != null && c.UserB.OnboardingCompletedAt != null &&
            !db.Blocks.Any(b => b.BlockerId == c.UserAId && b.BlockedUserId == c.UserBId || b.BlockerId == c.UserBId && b.BlockedUserId == c.UserAId));

    public static async Task<User> RequireArchitectAsync(IApplicationDbContext db, Guid architectId, Guid campusId, CancellationToken ct = default)
    {
        var architect = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == architectId, ct);
        if (architect is null || architect.Role != UserRole.Architect || architect.Status != AccountStatus.Active || architect.CampusId != campusId)
            throw new EligibilityException("An active Architect for this campus is required.");
        return architect;
    }
}
