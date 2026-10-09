using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;

namespace Vouch.Infrastructure.Persistence;

public static class LaunchEligibility
{
    public const CharacterTrait ValidTraits = CharacterTrait.Sincere | CharacterTrait.Respectful | CharacterTrait.AcademicallyMotivated |
        CharacterTrait.Empathetic | CharacterTrait.Reliable | CharacterTrait.Creative;
    public static async Task<LaunchReadinessMetrics> CalculateAsync(IApplicationDbContext db, Guid campusId,
        int requiredAmbassadors, int requiredVouches, CancellationToken ct = default)
    {
        var ambassadors = db.Users.Where(u => u.CampusId == campusId && u.Role == UserRole.Ambassador && u.Status == AccountStatus.Active);
        var active = await ambassadors.CountAsync(ct);
        var qualified = await ambassadors.CountAsync(u => u.EmailVerifiedAt != null && u.OnboardingCompletedAt != null &&
            u.AmbassadorApprovedAt != null && u.AmbassadorApprovedByArchitectId != null &&
            db.Vouches.Count(v => v.VoucherUserId == u.Id && v.TargetUserId != u.Id && v.Traits != CharacterTrait.None &&
                (v.Traits & ~ValidTraits) == CharacterTrait.None && v.TargetUser.CampusId == campusId &&
                v.TargetUser.EmailVerifiedAt != null && v.TargetUser.OnboardingCompletedAt != null &&
                (v.TargetUser.Status == AccountStatus.Active || v.TargetUser.Status == AccountStatus.InIncubation) &&
                !db.Blocks.Any(b => b.BlockerId == v.VoucherUserId && b.BlockedUserId == v.TargetUserId ||
                                   b.BlockerId == v.TargetUserId && b.BlockedUserId == v.VoucherUserId)) >= requiredVouches, ct);
        return LaunchReadinessCalculator.CalculateReadiness(active, qualified, requiredAmbassadors);
    }
}
