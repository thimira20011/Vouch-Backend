using Vouch.Domain.Enums;

namespace Vouch.Domain.Services;

public static class SlowBurnCalculator
{
    public const int MinMessageLengthForCounter = 5; // REQ-19
    public const int DefaultThreshold = 40;          // REQ-18
    public const int MinThreshold = 20;
    public const int MaxThreshold = 80;

    public static bool QualifiesForCounter(string? messageBody)
    {
        if (string.IsNullOrWhiteSpace(messageBody)) return false;
        return messageBody.Trim().Length >= MinMessageLengthForCounter;
    }

    public static RevealClarityStage DetermineClarityStage(int validMessageCount, int threshold)
    {
        if (threshold <= 0) threshold = DefaultThreshold;

        var progressRatio = (double)validMessageCount / threshold;

        if (progressRatio >= 1.0)
        {
            return RevealClarityStage.Full100;
        }
        if (progressRatio >= 0.70)
        {
            return RevealClarityStage.Sixty60;
        }
        if (progressRatio >= 0.40)
        {
            return RevealClarityStage.Quarter25;
        }

        return RevealClarityStage.Abstract0;
    }

    public static int ClampThreshold(int proposedThreshold)
    {
        return Math.Clamp(proposedThreshold, MinThreshold, MaxThreshold);
    }
}
