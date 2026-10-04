namespace Vouch.Application.Features.Auth;

public static class OnboardingOptions
{
    public static IReadOnlyList<string> DeepValues { get; } = Array.AsReadOnly(new[]
    {
        "Sincerity", "Respect", "Empathy", "Integrity", "Curiosity", "Creativity", "Kindness",
        "Responsibility", "Growth", "Community", "Independence", "Mindfulness"
    });
}
