using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Xunit;

namespace Vouch.UnitTests.DomainServices;

public class SlowBurnCalculatorTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("hi", false)]
    [InlineData("ok", false)]
    [InlineData("test", false)]
    [InlineData("hello", true)] // 5 chars
    [InlineData("This is a thoughtful letter to you.", true)]
    public void QualifiesForCounter_EvaluatesMessageLengthCorrectly(string body, bool expected)
    {
        var result = SlowBurnCalculator.QualifiesForCounter(body);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DetermineClarityStage_ProgressionStages_MatchesSRS()
    {
        const int threshold = 40;

        // < 40% (0 to 15 msgs) -> Abstract0
        Assert.Equal(RevealClarityStage.Abstract0, SlowBurnCalculator.DetermineClarityStage(15, threshold));

        // 40% of 40 = 16 msgs -> Quarter25
        Assert.Equal(RevealClarityStage.Quarter25, SlowBurnCalculator.DetermineClarityStage(16, threshold));
        Assert.Equal(RevealClarityStage.Quarter25, SlowBurnCalculator.DetermineClarityStage(27, threshold));

        // 70% of 40 = 28 msgs -> Sixty60
        Assert.Equal(RevealClarityStage.Sixty60, SlowBurnCalculator.DetermineClarityStage(28, threshold));
        Assert.Equal(RevealClarityStage.Sixty60, SlowBurnCalculator.DetermineClarityStage(39, threshold));

        // 100% of 40 = 40 msgs -> Full100
        Assert.Equal(RevealClarityStage.Full100, SlowBurnCalculator.DetermineClarityStage(40, threshold));
        Assert.Equal(RevealClarityStage.Full100, SlowBurnCalculator.DetermineClarityStage(55, threshold));
    }
}
