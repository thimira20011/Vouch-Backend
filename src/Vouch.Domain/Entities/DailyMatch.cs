using Vouch.Domain.Enums;

namespace Vouch.Domain.Entities;

public class DailyMatch : BaseEntity
{
    public Guid CampusId { get; set; }
    public Campus Campus { get; set; } = null!;

    public Guid UserAId { get; set; }
    public User UserA { get; set; } = null!;

    public Guid UserBId { get; set; }
    public User UserB { get; set; } = null!;

    public DateOnly CycleDate { get; set; }
    public double CompatibilityScore { get; set; }
    public int SharedDeepValuesCount { get; set; }
    public int SharedInterestsCount { get; set; }
    public double TrustScoreDifferential { get; set; }

    public MatchStatus Status { get; set; } = MatchStatus.Pending;
    public bool UserAAccepted { get; set; } = false;
    public bool UserBAccepted { get; set; } = false;

    public Guid? ResultingConversationId { get; set; }
}
