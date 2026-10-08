using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.Security;

public sealed class SessionService(ApplicationDbContext db, IJwtTokenService jwt, TimeProvider clock, IRealtimeConnections? realtime = null, CampusBoundary? boundary = null)
{
    public async Task<AuthResponse> CreateAsync(User identity, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(identity.CampusId, ct);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == identity.Id, ct);
        EnsureUsable(user);
        if (boundary is not null) await boundary.RequireAsync(db, user.CampusId, ct);
        if (user.PasswordHash != identity.PasswordHash) throw new UnauthorizedAccessException("Authentication changed. Sign in again.");
        var now = clock.GetUtcNow();
        var session = new AuthSession { UserId = user.Id, CreatedAt = now, ExpiresAt = now.AddDays(30) };
        var token = SingleUseToken.Create();
        db.AuthSessions.Add(session);
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = SingleUseToken.Hash(token), CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Response(user, session, token);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 200)
            throw new UnauthorizedAccessException("Invalid refresh token.");
        var hash = SingleUseToken.Hash(request.RefreshToken);
        var identity = await db.RefreshTokens.AsNoTracking().Where(t => t.TokenHash == hash)
            .Select(t => new { t.SessionId, t.Session.User.CampusId }).SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Invalid refresh token.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Same lock order as account-state writes and session creation.
        await LockCampusAsync(identity.CampusId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AuthSessions\" WHERE \"Id\" = {identity.SessionId} FOR UPDATE", ct);
        var token = await db.RefreshTokens.AsNoTracking().Include(t => t.Session).ThenInclude(s => s.User).SingleAsync(t => t.TokenHash == hash, ct);
        var session = token.Session;
        if (boundary is not null) await boundary.RequireAsync(db, session.User.CampusId, ct);
        var now = clock.GetUtcNow();
        if (token.UsedAt is not null || session.RevokedAt is not null || session.ExpiresAt <= now ||
            session.User.Status is not (AccountStatus.Active or AccountStatus.InIncubation))
        {
            await db.AuthSessions.Where(s => s.Id == session.Id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
            await tx.CommitAsync(ct);
            realtime?.RevokeSession(session.Id);
            throw new UnauthorizedAccessException("Refresh session is unavailable. Sign in again.");
        }
        await db.RefreshTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        var replacement = SingleUseToken.Create();
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = SingleUseToken.Hash(replacement), CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Response(session.User, session, replacement);
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId, bool all, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.AuthSessions.Where(s => s.UserId == userId && (all || s.Id == sessionId) && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        if (all) realtime?.RevokeUser(userId); else realtime?.RevokeSession(sessionId);
    }

    public static void EnsureUsable(User user)
    {
        if (user.Status is not (AccountStatus.Active or AccountStatus.InIncubation))
            throw new UnauthorizedAccessException("Account is unavailable. Sign in is not permitted.");
    }

    private Task LockCampusAsync(Guid campusId, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {campusId} FOR UPDATE", ct);
    private AuthResponse Response(User user, AuthSession session, string token) => new(user.Id, user.Email, user.FullName,
        user.Role.ToString(), user.Status.ToString(), user.TrustScore, user.HasFoundingMemberBadge,
        jwt.GenerateToken(user, session.Id), user.EmailVerifiedAt != null, user.OnboardingCompletedAt != null,
        token, DateTimeOffset.UtcNow.AddMinutes(15), session.ExpiresAt);
}

public static class SessionAccess
{
    public static async Task<User> RequireAsync(IApplicationDbContext db, ClaimsPrincipal principal, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(principal.FindFirstValue("sid"), out var sessionId)) throw new UnauthorizedAccessException("Authentication is required.");
        var user = await db.AuthSessions.AsNoTracking().Where(s => s.Id == sessionId && s.UserId == userId &&
            s.RevokedAt == null && s.ExpiresAt > now).Select(s => s.User).SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Session is unavailable. Sign in again.");
        SessionService.EnsureUsable(user);
        if (principal.FindFirstValue(ClaimTypes.Role) != user.Role.ToString() ||
            principal.FindFirstValue("campus_id") != user.CampusId.ToString()) throw new UnauthorizedAccessException("Account permissions changed. Sign in again.");
        return user;
    }
}
