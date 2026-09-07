namespace Vouch.Domain.Enums;

public enum UserRole
{
    Seeker = 1,
    Voucher = 2,
    Ambassador = 3,
    Architect = 4
}

public enum AccountStatus
{
    InIncubation = 1,
    Active = 2,
    Suspended = 3,
    DeletionRequested = 4
}

[Flags]
public enum CharacterTrait
{
    None = 0,
    Sincere = 1 << 0,
    Respectful = 1 << 1,
    AcademicallyMotivated = 1 << 2,
    Empathetic = 1 << 3,
    Reliable = 1 << 4,
    Creative = 1 << 5
}

public enum IntellectualInterest
{
    Philosophy = 1,
    Literature = 2,
    Architecture = 3,
    Music = 4,
    AcademicGoals = 5,
    Science = 6
}

public enum RevealClarityStage
{
    Abstract0 = 0,
    Quarter25 = 25,
    Sixty60 = 60,
    Full100 = 100
}

public enum ConversationStatus
{
    Active = 1,
    Paused = 2,
    Archived = 3
}

public enum MatchStatus
{
    Pending = 1,
    Accepted = 2,
    Rejected = 3,
    Expired = 4
}

public enum ReportCategory
{
    Harassment = 1,
    Impersonation = 2,
    Spam = 3,
    InappropriateContent = 4,
    Other = 5
}

public enum ReportStatus
{
    Pending = 1,
    Upheld = 2,
    Dismissed = 3
}
