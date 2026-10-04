using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Domain.Services;

public static class UniversityIdentity
{
    public static bool IsApprovedEmail(string email, string campusDomain)
    {
        // DomainPattern is an exact approved mailbox domain, never a suffix wildcard.
        var domain = campusDomain.Trim().TrimStart('@');
        return System.Net.Mail.MailAddress.TryCreate(email, out var address)
            && string.Equals(address.Address, email, StringComparison.Ordinal)
            && string.Equals(address.Host, domain, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOnboarded(User user) => user.EmailVerifiedAt is not null && user.OnboardingCompletedAt is not null;
    public static bool IsActiveMember(User user) => IsOnboarded(user) && user.Status == AccountStatus.Active;
}
