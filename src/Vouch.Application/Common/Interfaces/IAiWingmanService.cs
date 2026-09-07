using Vouch.Domain.Entities;

namespace Vouch.Application.Common.Interfaces;

public record IcebreakerSuggestion(
    string Text,
    string GroundingTheme,
    bool IsFromAi
);

public interface IAiWingmanService
{
    Task<IReadOnlyList<IcebreakerSuggestion>> GenerateIcebreakersAsync(
        User userA,
        User userB,
        CancellationToken cancellationToken = default);
}
