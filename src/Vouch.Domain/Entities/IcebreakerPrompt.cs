using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class IcebreakerPrompt : BaseEntity
{
    public IntellectualInterest Category { get; set; }
    public required string PromptText { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool IsStaticFallback { get; set; } = true;
}
