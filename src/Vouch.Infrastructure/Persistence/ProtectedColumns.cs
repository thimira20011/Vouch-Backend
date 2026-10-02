namespace Vouch.Infrastructure.Persistence;

// One inventory shared by runtime converters and the explicit data-upgrade tool.
public sealed record ProtectedColumn(string Table, string Column, bool LegacyEncrypted = false, int Limit = 0)
{
    public string Purpose => Table + "." + Column;
}

public static class ProtectedColumns
{
    public static readonly ProtectedColumn[] Strings =
    [
        new("Users", "Email", true, 120), new("Users", "FullName", true, 120), new("Users", "Bio", true, 280),
        new("Users", "Faculty", Limit: 100), new("Users", "Department", Limit: 100),
        new("Messages", "Body", Limit: 2000), new("Vouches", "Note"),
        new("Reports", "Details"), new("Reports", "ArchitectNotes"), new("AmbassadorInvites", "IntendedEmail", Limit: 120)
    ];
    public static readonly ProtectedColumn[] Json = [new("Users", "DeepValues"), new("Users", "IntellectualInterests")];
}
