using Vouch.Application.Features.Auth;
using Vouch.Domain.Enums;

namespace Vouch.UnitTests.Security;

public sealed class OnboardingValidationTests
{
    [Fact]
    public void NullCollectionsFailWithoutThrowing()
    {
        var result = new CompleteOnboardingRequestValidator().Validate(new CompleteOnboardingRequest("", null!, null!));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "DeepValues");
        Assert.Contains(result.Errors, e => e.PropertyName == "IntellectualInterests");
    }

    [Theory]
    [InlineData("Unknown", 1)]
    [InlineData("", 1)]
    [InlineData("Sincerity", 0)]
    [InlineData("Sincerity", 99)]
    public void UnknownChoicesFail(string value, int interest)
        => Assert.False(new CompleteOnboardingRequestValidator().Validate(new CompleteOnboardingRequest("", [value], [(IntellectualInterest)interest])).IsValid);

    [Fact]
    public void DuplicateAndEmptyChoicesFail()
    {
        var validator = new CompleteOnboardingRequestValidator();
        Assert.False(validator.Validate(new CompleteOnboardingRequest("", [], [])).IsValid);
        Assert.False(validator.Validate(new CompleteOnboardingRequest("", ["Sincerity", "Sincerity"], [IntellectualInterest.Science, IntellectualInterest.Science])).IsValid);
        Assert.True(validator.Validate(new CompleteOnboardingRequest("", ["Sincerity"], [IntellectualInterest.Science])).IsValid);
    }
}
