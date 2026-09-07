using Vouch.Domain.Services;
using Xunit;

namespace Vouch.UnitTests.DomainServices;

public class LaunchReadinessCalculatorTests
{
    [Fact]
    public void CalculateReadiness_ZeroAmbassadors_ReturnsZeroScore()
    {
        var result = LaunchReadinessCalculator.CalculateReadiness(0, 0);

        Assert.Equal(0.0, result.LaunchReadinessPercentage);
        Assert.False(result.IsGatePassed);
    }

    [Fact]
    public void CalculateReadiness_PartialAmbassadors_ReturnsProportionalScore()
    {
        // 15 ambassadors (50% of ambassador target = 25%)
        // 15 meeting vouch target (50% of vouch target = 25%)
        var result = LaunchReadinessCalculator.CalculateReadiness(15, 15);

        Assert.Equal(50.0, result.LaunchReadinessPercentage);
        Assert.False(result.IsGatePassed);
    }

    [Fact]
    public void CalculateReadiness_30AmbassadorsWithVouches_UnlocksGate()
    {
        var result = LaunchReadinessCalculator.CalculateReadiness(30, 30);

        Assert.Equal(100.0, result.LaunchReadinessPercentage);
        Assert.True(result.IsGatePassed);
    }
}
