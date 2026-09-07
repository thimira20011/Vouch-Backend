namespace Vouch.Domain.Services;

public record LaunchReadinessMetrics(
    int ActiveAmbassadorCount,
    int TargetAmbassadorCount,
    int AmbassadorsMeetingVouchTarget,
    double LaunchReadinessPercentage,
    bool IsGatePassed
);

public static class LaunchReadinessCalculator
{
    public const int DefaultRequiredAmbassadors = 30; // REQ-A2, REQ-A5
    public const int DefaultRequiredVouchesPerAmbassador = 2; // REQ-A5

    public static LaunchReadinessMetrics CalculateReadiness(
        int activeAmbassadors,
        int ambassadorsWithAtLeastTwoVouches,
        int requiredAmbassadors = DefaultRequiredAmbassadors)
    {
        // 50% weight for reaching 30 ambassadors, 50% weight for each ambassador having >=2 vouches
        var ambassadorRatio = Math.Min(1.0, (double)activeAmbassadors / requiredAmbassadors);
        var vouchRatio = requiredAmbassadors > 0
            ? Math.Min(1.0, (double)ambassadorsWithAtLeastTwoVouches / requiredAmbassadors)
            : 0.0;

        var readinessPercent = Math.Round((ambassadorRatio * 50.0) + (vouchRatio * 50.0), 1);
        var isGatePassed = activeAmbassadors >= requiredAmbassadors &&
                           ambassadorsWithAtLeastTwoVouches >= requiredAmbassadors;

        return new LaunchReadinessMetrics(
            ActiveAmbassadorCount: activeAmbassadors,
            TargetAmbassadorCount: requiredAmbassadors,
            AmbassadorsMeetingVouchTarget: ambassadorsWithAtLeastTwoVouches,
            LaunchReadinessPercentage: readinessPercent,
            IsGatePassed: isGatePassed
        );
    }
}
