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
        if (requiredAmbassadors <= 0 || activeAmbassadors < 0 || ambassadorsWithAtLeastTwoVouches < 0 ||
            ambassadorsWithAtLeastTwoVouches > activeAmbassadors)
            throw new ArgumentOutOfRangeException(nameof(requiredAmbassadors));
        // Every active ambassador must qualify, including ambassadors above the minimum cohort.
        var ambassadorRatio = Math.Min(1.0, (double)activeAmbassadors / requiredAmbassadors);
        var vouchRatio = (double)ambassadorsWithAtLeastTwoVouches / Math.Max(requiredAmbassadors, activeAmbassadors);

        var readinessPercent = Math.Round((ambassadorRatio * 50.0) + (vouchRatio * 50.0), 1);
        var isGatePassed = activeAmbassadors >= requiredAmbassadors &&
                           ambassadorsWithAtLeastTwoVouches == activeAmbassadors;

        return new LaunchReadinessMetrics(
            ActiveAmbassadorCount: activeAmbassadors,
            TargetAmbassadorCount: requiredAmbassadors,
            AmbassadorsMeetingVouchTarget: ambassadorsWithAtLeastTwoVouches,
            LaunchReadinessPercentage: readinessPercent,
            IsGatePassed: isGatePassed
        );
    }
}
