using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Domain.Services;

public record MatchScoreResult(
    double CompatibilityScore,
    int SharedDeepValuesCount,
    int SharedInterestsCount,
    double TrustScoreDifferential,
    bool IsQualityMatch
);

public static class MatchmakingScorer
{
    public const double QualityThreshold = 0.50; // Minimum score to qualify as Daily Match
    private const double MaxTrustScoreCap = 20.0;

    public static MatchScoreResult EvaluateCompatibility(User userA, User userB)
    {
        // 1. Shared Deep Values Overlap (40% weight)
        var sharedValues = userA.DeepValues.Intersect(userB.DeepValues, StringComparer.OrdinalIgnoreCase).ToList();
        var maxPossibleValues = Math.Max(1, Math.Max(userA.DeepValues.Count, userB.DeepValues.Count));
        var valuesScore = (double)sharedValues.Count / maxPossibleValues;

        // 2. Intellectual Interest Alignment (30% weight)
        var sharedInterests = userA.IntellectualInterests.Intersect(userB.IntellectualInterests).ToList();
        var maxPossibleInterests = Math.Max(1, Math.Max(userA.IntellectualInterests.Count, userB.IntellectualInterests.Count));
        var interestsScore = (double)sharedInterests.Count / maxPossibleInterests;

        // 3. Trust Score Proximity (prefer similar Trust Scores) (20% weight)
        var trustDiff = Math.Abs(userA.TrustScore - userB.TrustScore);
        var trustProximityScore = Math.Max(0.0, 1.0 - (trustDiff / MaxTrustScoreCap));

        // 4. Faculty and Department Diversity Preference (REQ-14: faculty/department diversity preferred) (10% weight)
        // Give a slight diversity bonus if students are from different faculties/departments on same campus
        var diversityBonus = !string.Equals(userA.Faculty, userB.Faculty, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.6;

        // Weighted total
        var totalScore = (valuesScore * 0.40) +
                         (interestsScore * 0.30) +
                         (trustProximityScore * 0.20) +
                         (diversityBonus * 0.10);

        var roundedScore = Math.Round(Math.Clamp(totalScore, 0.0, 1.0), 3);
        var isQuality = roundedScore >= QualityThreshold;

        return new MatchScoreResult(
            CompatibilityScore: roundedScore,
            SharedDeepValuesCount: sharedValues.Count,
            SharedInterestsCount: sharedInterests.Count,
            TrustScoreDifferential: Math.Round(trustDiff, 2),
            IsQualityMatch: isQuality
        );
    }
}
