using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Entities;
using Vouch.Domain.Services;

namespace Vouch.Infrastructure.Security;

public static class MemberEligibility
{
    public static async Task RequireActiveAsync(IApplicationDbContext db, Guid userId, CancellationToken ct = default)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.EmailVerifiedAt != null &&
            u.OnboardingCompletedAt != null && u.Status == Vouch.Domain.Enums.AccountStatus.Active, ct))
            throw new EligibilityException("Verified onboarding and an active account are required.");
    }

    public static void RequireActive(User user)
    {
        if (!UniversityIdentity.IsActiveMember(user)) throw new EligibilityException("Verified onboarding and an active account are required.");
    }
}
