using Vouch.Domain.Entities;

namespace Vouch.Domain.Services;

public static class TrustScoreCalculator
{
    public const double BaseVouchWeight = 1.0;
    public const double HighTrustVoucherBonus = 0.2;
    public const int HighTrustVoucherThreshold = 5;
    public const double MaxTrustScoreCap = 20.0;
    public const int MinAccountAgeDaysForWeight = 14;
    public const int CliqueMutualVouchersThreshold = 3;
    public const double CliqueDampeningMultiplier = 0.5;
    public const double AnomalyScoreThreshold48Hours = 3.0;

    /// <summary>
    /// Computes the weight for a single new vouch according to REQ-7, REQ-8, and REQ-9.
    /// </summary>
    public static double CalculateVouchWeight(
        User voucherUser,
        int mutualVouchersBetweenCircles,
        DateTimeOffset currentUtc)
    {
        // REQ-9: Vouches from users registered within the past 14 days carry zero Trust Score weight
        var voucherAge = currentUtc - voucherUser.CreatedAt;
        if (voucherAge.TotalDays < MinAccountAgeDaysForWeight)
        {
            return 0.0;
        }

        var weight = BaseVouchWeight;

        // REQ-7: +0.2 bonus if the voucher holds 5 or more vouches themselves
        if (voucherUser.ActiveVouchesReceivedCount >= HighTrustVoucherThreshold)
        {
            weight += HighTrustVoucherBonus;
        }

        // REQ-8: Anti-gaming clique pattern: 50% reduced weight if >= 3 mutual vouchers
        if (mutualVouchersBetweenCircles >= CliqueMutualVouchersThreshold)
        {
            weight *= CliqueDampeningMultiplier;
        }

        return weight;
    }

    /// <summary>
    /// Caps the cumulative Trust Score at 20.0 (REQ-7).
    /// </summary>
    public static double ApplyScoreCap(double rawScore)
    {
        return Math.Min(rawScore, MaxTrustScoreCap);
    }

    /// <summary>
    /// Detects if the score velocity exceeds 3 points in a 48-hour window (REQ-10).
    /// </summary>
    public static bool IsAnomalyVelocity(double scoreIncreaseIn48Hours)
    {
        return scoreIncreaseIn48Hours > AnomalyScoreThreshold48Hours;
    }
}
