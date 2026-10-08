using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Matching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public class MatchService : IMatchService
{
    private readonly IApplicationDbContext _context;
    private readonly ILogger<MatchService> _logger;
    private readonly IMemoryCache _cache;
    private readonly CampusBoundary? _boundary;

    public MatchService(IApplicationDbContext context, ILogger<MatchService> logger, IMemoryCache cache, CampusBoundary? boundary = null)
    {
        _context = context;
        _logger = logger;
        _cache = cache;
        _boundary = boundary;
    }

    public async Task<TodayConnectionResponse> GetTodayConnectionAsync(Guid userId, CancellationToken ct = default)
    {
        await MemberEligibility.RequireActiveAsync(_context, userId, ct);
        // Step 24: Cache today's connection per user, invalidated at midnight or on RespondToMatchAsync
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"match:{userId}:{today:yyyy-MM-dd}";

        if (_cache.TryGetValue(cacheKey, out TodayConnectionResponse? cached) && cached is not null)
        {
            if (cached.Match is null || await ResourceAccess.CanPeerAsync(_context, userId, cached.Match.MatchedUserId, ct) &&
                await _context.Users.CountAsync(u => (u.Id == userId || u.Id == cached.Match.MatchedUserId) && !u.IsSoftHiddenFromMatchmaking, ct) == 2 &&
                await _context.Matches.AnyAsync(m => m.Id == cached.Match.MatchId && m.CycleDate == today &&
                    (m.UserAId == userId && m.UserBId == cached.Match.MatchedUserId || m.UserBId == userId && m.UserAId == cached.Match.MatchedUserId) &&
                    m.UserA.CampusId == m.CampusId && m.UserB.CampusId == m.CampusId, ct)) return cached;
            _cache.Remove(cacheKey);
        }

        var user = await _context.Users
            .AsNoTracking()
            .Include(u => u.Campus)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        // 1. Check if user already has a match for today
        var existingMatch = await _context.Matches
            .AsNoTracking()
            .Include(m => m.UserA)
            .Include(m => m.UserB)
            .FirstOrDefaultAsync(m => (m.UserAId == userId || m.UserBId == userId) && m.CycleDate == today &&
                m.UserA.CampusId == m.CampusId && m.UserB.CampusId == m.CampusId &&
                m.UserA.Status == AccountStatus.Active && m.UserB.Status == AccountStatus.Active &&
                m.UserA.EmailVerifiedAt != null && m.UserA.OnboardingCompletedAt != null && m.UserB.EmailVerifiedAt != null && m.UserB.OnboardingCompletedAt != null &&
                !m.UserA.IsSoftHiddenFromMatchmaking && !m.UserB.IsSoftHiddenFromMatchmaking &&
                !_context.Blocks.Any(b => b.BlockerId == m.UserAId && b.BlockedUserId == m.UserBId || b.BlockerId == m.UserBId && b.BlockedUserId == m.UserAId), ct);

        if (existingMatch != null)
        {
            var matchedUser = existingMatch.UserAId == userId ? existingMatch.UserB : existingMatch.UserA;
            MemberEligibility.RequireActive(matchedUser);
            var sharedValues = user.DeepValues.Intersect(matchedUser.DeepValues, StringComparer.OrdinalIgnoreCase).ToList();
            var sharedInterests = user.IntellectualInterests.Intersect(matchedUser.IntellectualInterests).ToList();

            var matchDto = new DailyMatchDto(
                MatchId: existingMatch.Id,
                MatchedUserId: matchedUser.Id,
                MatchedUserFullName: matchedUser.FullName,
                Faculty: matchedUser.Faculty,
                Department: matchedUser.Department,
                Bio: matchedUser.Bio,
                TrustScore: matchedUser.TrustScore,
                HasFoundingMemberBadge: matchedUser.HasFoundingMemberBadge,
                SharedDeepValues: sharedValues,
                SharedInterests: sharedInterests,
                CompatibilityScore: existingMatch.CompatibilityScore,
                Status: existingMatch.Status
            );

            var matchResult = new TodayConnectionResponse(HasMatch: true, Match: matchDto, Reflection: null);

            // Step 24: Cache until midnight so repeated GETs don't re-query the DB
            var midnightForMatch = DateTimeOffset.UtcNow.Date.AddDays(1);
            _cache.Set(cacheKey, matchResult, midnightForMatch - DateTimeOffset.UtcNow);

            return matchResult;
        }

        // 2. If no match exists today, fetch Daily Reflection (REQ-12)
        var primaryInterest = user.IntellectualInterests.FirstOrDefault();
        var reflection = await _context.Reflections
            .AsNoTracking()
            .Where(r => r.Category == primaryInterest)
            .OrderBy(r => Guid.NewGuid())
            .FirstOrDefaultAsync(ct)
            ?? await _context.Reflections.AsNoTracking().OrderBy(r => Guid.NewGuid()).FirstOrDefaultAsync(ct);

        var reflectionDto = reflection != null
            ? new DailyReflectionDto(
                Quote: reflection.Quote,
                Author: reflection.Author,
                ThoughtProvokingQuestion: reflection.ThoughtProvokingQuestion,
                InterestCategory: reflection.Category.ToString())
            : new DailyReflectionDto(
                Quote: "The soul becomes dyed with the color of its thoughts.",
                Author: "Marcus Aurelius",
                ThoughtProvokingQuestion: "What contemplation brought you peace today?",
                InterestCategory: "Philosophy");

        var result = new TodayConnectionResponse(HasMatch: false, Match: null, Reflection: reflectionDto);

        // Cache result until midnight UTC (when the daily cycle resets)
        var midnight = DateTimeOffset.UtcNow.Date.AddDays(1);
        var expiry = midnight - DateTimeOffset.UtcNow;
        _cache.Set(cacheKey, result, expiry);

        return result;
    }

    public async Task<bool> RespondToMatchAsync(Guid userId, Guid matchId, bool accept, CancellationToken ct = default)
    {
        await MemberEligibility.RequireActiveAsync(_context, userId, ct);
        var match = await _context.Matches.FirstOrDefaultAsync(m => m.Id == matchId, ct)
            ?? throw new KeyNotFoundException("Match not found.");
        if (match.UserAId != userId && match.UserBId != userId) throw new Vouch.Application.Common.EligibilityException("Match is unavailable.");
        var peer = await ResourceAccess.RequirePeerAsync(_context, userId, match.UserAId == userId ? match.UserBId : match.UserAId, ct: ct);
        if (peer.CampusId != match.CampusId || peer.IsSoftHiddenFromMatchmaking) throw new Vouch.Application.Common.EligibilityException("Match is unavailable.");
        await MemberEligibility.RequireActiveAsync(_context, match.UserAId, ct);
        await MemberEligibility.RequireActiveAsync(_context, match.UserBId, ct);

        if (match.UserAId != userId && match.UserBId != userId)
        {
            throw new UnauthorizedAccessException("You are not a participant in this match.");
        }

        if (!accept)
        {
            match.Status = MatchStatus.Rejected;
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Match rejected. MatchId={MatchId} UserId={UserId}", matchId, userId);

            // Step 24: Invalidate cache for both participants
            InvalidateMatchCache(match.UserAId);
            InvalidateMatchCache(match.UserBId);
            return false;
        }

        if (match.UserAId == userId) match.UserAAccepted = true;
        if (match.UserBId == userId) match.UserBAccepted = true;

        // If both accepted -> create Conversation
        if (match.UserAAccepted && match.UserBAccepted)
        {
            match.Status = MatchStatus.Accepted;

            var conversation = new Conversation
            {
                UserAId = match.UserAId,
                UserBId = match.UserBId,
                Status = ConversationStatus.Active,
                AdaptiveRevealThreshold = SlowBurnCalculator.DefaultThreshold
            };

            _context.Conversations.Add(conversation);
            match.ResultingConversationId = conversation.Id;
        }

        await _context.SaveChangesAsync(ct);
        if (match.UserAAccepted && match.UserBAccepted)
            _logger.LogInformation("Match accepted — conversation started. MatchId={MatchId} ConversationId={ConversationId}",
                matchId, match.ResultingConversationId);

        // Step 24: Invalidate cache for both participants so next GET is fresh
        InvalidateMatchCache(match.UserAId);
        InvalidateMatchCache(match.UserBId);

        return true;
    }

    /// <summary>Step 24: Removes the today-connection cache entry for a specific user.</summary>
    private void InvalidateMatchCache(Guid userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _cache.Remove($"match:{userId}:{today:yyyy-MM-dd}");
    }

    public async Task GenerateDailyMatchesForCampusAsync(Guid campusId, CancellationToken ct = default)
    {
        if (_boundary is not null) await _boundary.RequireAsync(_context, campusId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Active non-hidden users who are not in-incubation and not deletion requested
        var eligibleUsers = await _context.Users
            .Where(u => u.CampusId == campusId &&
                        u.Status == AccountStatus.Active &&
                        u.EmailVerifiedAt != null && u.OnboardingCompletedAt != null &&
                        !u.IsSoftHiddenFromMatchmaking &&
                        u.DeletionRequestedAt == null)
            .ToListAsync(ct);

        // Get blocked pairs
        var blockedPairs = await _context.Blocks.AsNoTracking().Select(b => new { b.BlockerId, b.BlockedUserId }).ToListAsync(ct);
        var blockedSet = new HashSet<(Guid, Guid)>(blockedPairs.Select(b => (b.BlockerId, b.BlockedUserId)));

        // Get already matched today
        var existingMatchesToday = await _context.Matches
            .AsNoTracking()
            .Where(m => m.CampusId == campusId && m.CycleDate == today)
            .Select(m => new { m.UserAId, m.UserBId })
            .ToListAsync(ct);

        var matchedUserIds = new HashSet<Guid>(
            existingMatchesToday.Select(m => m.UserAId).Concat(existingMatchesToday.Select(m => m.UserBId)));

        var candidatePool = eligibleUsers.Where(u => !matchedUserIds.Contains(u.Id)).ToList();
        var paired = new HashSet<Guid>();

        for (int i = 0; i < candidatePool.Count; i++)
        {
            var userA = candidatePool[i];
            if (paired.Contains(userA.Id)) continue;

            MatchScoreResult? bestScore = null;
            User? bestPartner = null;

            for (int j = i + 1; j < candidatePool.Count; j++)
            {
                var userB = candidatePool[j];
                if (paired.Contains(userB.Id)) continue;

                // Check blocks (NFR-13)
                if (blockedSet.Contains((userA.Id, userB.Id)) || blockedSet.Contains((userB.Id, userA.Id)))
                    continue;

                var score = MatchmakingScorer.EvaluateCompatibility(userA, userB);
                if (score.IsQualityMatch && (bestScore == null || score.CompatibilityScore > bestScore.CompatibilityScore))
                {
                    bestScore = score;
                    bestPartner = userB;
                }
            }

            if (bestPartner != null && bestScore != null)
            {
                var match = new DailyMatch
                {
                    CampusId = campusId,
                    UserAId = userA.Id,
                    UserBId = bestPartner.Id,
                    CycleDate = today,
                    CompatibilityScore = bestScore.CompatibilityScore,
                    SharedDeepValuesCount = bestScore.SharedDeepValuesCount,
                    SharedInterestsCount = bestScore.SharedInterestsCount,
                    TrustScoreDifferential = bestScore.TrustScoreDifferential,
                    Status = MatchStatus.Pending
                };

                _context.Matches.Add(match);
                paired.Add(userA.Id);
                paired.Add(bestPartner.Id);
            }
        }

        await _context.SaveChangesAsync(ct);
    }
}
