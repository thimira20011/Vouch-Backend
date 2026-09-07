using Vouch.Domain.Enums;

namespace Vouch.Application.Features.Matching;

public record DailyMatchDto(
    Guid MatchId,
    Guid MatchedUserId,
    string MatchedUserFullName,
    string Faculty,
    string Department,
    string Bio,
    double TrustScore,
    bool HasFoundingMemberBadge,
    IReadOnlyList<string> SharedDeepValues,
    IReadOnlyList<IntellectualInterest> SharedInterests,
    double CompatibilityScore,
    MatchStatus Status
);

public record DailyReflectionDto(
    string Quote,
    string Author,
    string ThoughtProvokingQuestion,
    string InterestCategory
);

public record TodayConnectionResponse(
    bool HasMatch,
    DailyMatchDto? Match,
    DailyReflectionDto? Reflection
);

public interface IMatchService
{
    Task<TodayConnectionResponse> GetTodayConnectionAsync(Guid userId, CancellationToken ct = default);
    Task<bool> RespondToMatchAsync(Guid userId, Guid matchId, bool accept, CancellationToken ct = default);
    Task GenerateDailyMatchesForCampusAsync(Guid campusId, CancellationToken ct = default);
}
