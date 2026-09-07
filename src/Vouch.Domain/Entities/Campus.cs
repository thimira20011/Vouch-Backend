namespace Vouch.Domain.Entities;

public class Campus : BaseEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; } // e.g., "SUSL", "UOM", "UOC", "UOP"
    public required string DomainPattern { get; set; } // e.g., "@sab.ac.lk", "@uom.lk"
    public bool IsSoftLaunchUnlocked { get; set; } = false;
    public double LaunchReadinessScore { get; set; } = 0.0;
    public int RequiredAmbassadorsForLaunch { get; set; } = 30;
    public int RequiredVouchesPerAmbassador { get; set; } = 2;

    public ICollection<User> Users { get; set; } = new List<User>();
    public ICollection<DailyMatch> Matches { get; set; } = new List<DailyMatch>();
}
