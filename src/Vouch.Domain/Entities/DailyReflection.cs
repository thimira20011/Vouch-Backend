using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class DailyReflection : BaseEntity
{
    public IntellectualInterest Category { get; set; }
    public required string Quote { get; set; }
    public required string Author { get; set; }
    public required string ThoughtProvokingQuestion { get; set; }
}
