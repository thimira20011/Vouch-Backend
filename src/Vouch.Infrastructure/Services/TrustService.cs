using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Vouching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.Services;

public class TrustService : ITrustService
{
    private readonly IApplicationDbContext _context;
    private readonly ISlowBurnNotificationService _notificationService;
    private readonly ILogger<TrustService> _logger;

    public TrustService(
        IApplicationDbContext context,
        ISlowBurnNotificationService notificationService,
        ILogger<TrustService> logger)
    {
        _context = context;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<VouchDto> SubmitVouchAsync(
        Guid voucherUserId,
        SubmitVouchRequest request,
        CancellationToken ct = default)
    {
        if (request.Traits == CharacterTrait.None || (request.Traits & ~LaunchEligibility.ValidTraits) != CharacterTrait.None)
            throw new ArgumentException("Choose at least one valid character trait.");
        if (voucherUserId == request.TargetUserId)
        {
            throw new InvalidOperationException("You cannot vouch for yourself.");
        }
        await ResourceAccess.RequirePeerAsync(_context, voucherUserId, request.TargetUserId, allowIncubation: true, ct: ct);

        var voucher = await _context.Users
            .Include(u => u.VouchesReceived)
            .FirstOrDefaultAsync(u => u.Id == voucherUserId, ct)
            ?? throw new KeyNotFoundException("Voucher user not found.");

        var target = await _context.Users
            .Include(u => u.VouchesReceived)
            .FirstOrDefaultAsync(u => u.Id == request.TargetUserId, ct)
            ?? throw new KeyNotFoundException("Target user not found.");
        MemberEligibility.RequireActive(voucher);
        if (!UniversityIdentity.IsOnboarded(target) || target.Status is not (AccountStatus.Active or AccountStatus.InIncubation) ||
            target.CampusId != voucher.CampusId)
            throw new Vouch.Application.Common.EligibilityException("Vouches require a verified, onboarded peer on the same campus.");

        // REQ-6: A single user may vouch for any given person only once
        var existingVouch = await _context.Vouches
            .AnyAsync(v => v.TargetUserId == target.Id && v.VoucherUserId == voucher.Id, ct);

        if (existingVouch)
        {
            throw new InvalidOperationException("You have already vouched for this person. Re-vouching is not permitted.");
        }

        // REQ-8: Detect clique patterns (mutual vouchers between social circles)
        var mutualVouchers = await CalculateMutualVouchersAsync(voucher.Id, target.Id, ct);

        var now = DateTimeOffset.UtcNow;
        var finalWeight = TrustScoreCalculator.CalculateVouchWeight(voucher, mutualVouchers, now);

        var voucherAgeDays = (now - voucher.CreatedAt).TotalDays;
        var isZeroAge = voucherAgeDays < TrustScoreCalculator.MinAccountAgeDaysForWeight;
        var isCliqueDampened = mutualVouchers >= TrustScoreCalculator.CliqueMutualVouchersThreshold;

        var vouchRecord = new VouchRecord
        {
            TargetUserId = target.Id,
            VoucherUserId = voucher.Id,
            Traits = request.Traits,
            Note = request.Note,
            BaseWeight = TrustScoreCalculator.BaseVouchWeight,
            VoucherBonus = voucher.ActiveVouchesReceivedCount >= TrustScoreCalculator.HighTrustVoucherThreshold ? TrustScoreCalculator.HighTrustVoucherBonus : 0.0,
            CliqueDampeningMultiplier = isCliqueDampened ? TrustScoreCalculator.CliqueDampeningMultiplier : 1.0,
            IsZeroWeightDueToAccountAge = isZeroAge,
            FinalCalculatedWeight = finalWeight
        };

        _context.Vouches.Add(vouchRecord);

        // Update target user metrics
        var oldScore = target.TrustScore;
        var newRawScore = oldScore + finalWeight;
        target.TrustScore = TrustScoreCalculator.ApplyScoreCap(newRawScore);
        target.ActiveVouchesReceivedCount += 1;

        // REQ-2: Incubation unlock: 3 unique vouches from active users
        var eligibleVouches = await _context.Vouches.CountAsync(v => v.TargetUserId == target.Id &&
            v.VoucherUserId != target.Id && v.Traits != CharacterTrait.None && (v.Traits & ~LaunchEligibility.ValidTraits) == CharacterTrait.None &&
            v.VoucherUser.CampusId == target.CampusId && v.VoucherUser.Status == AccountStatus.Active &&
            v.VoucherUser.EmailVerifiedAt != null && v.VoucherUser.OnboardingCompletedAt != null, ct) + 1;
        target.ActiveVouchesReceivedCount = eligibleVouches;
        if (target.Status == AccountStatus.InIncubation && eligibleVouches >= 3)
        {
            target.Status = AccountStatus.Active;
            target.IncubationCompletedAt = now;
        }

        // The context recalculates readiness after persisting this vouch in the same transaction.

        await _context.SaveChangesAsync(ct);

        // REQ-10: Anomaly check: alert if score increases by > 3 points in 48 hours
        var fortyEightHoursAgo = now.AddHours(-48);
        var recentWeightsSum = await _context.Vouches
            .Where(v => v.TargetUserId == target.Id && v.CreatedAt >= fortyEightHoursAgo)
            .SumAsync(v => v.FinalCalculatedWeight, ct);

        if (TrustScoreCalculator.IsAnomalyVelocity(recentWeightsSum))
        {
            _logger.LogWarning("Trust anomaly detected. UserId={UserId} ScoreIncrease48h={ScoreIncrease:F2} OldScore={OldScore:F2} NewScore={NewScore:F2}",
                target.Id, recentWeightsSum, oldScore, target.TrustScore);
            await _notificationService.NotifyTrustScoreAlertToArchitectAsync(target.Id, oldScore, target.TrustScore, ct);
        }

        return new VouchDto(
            Id: vouchRecord.Id,
            VoucherId: voucher.Id,
            VoucherName: voucher.FullName,
            Traits: vouchRecord.Traits,
            Note: vouchRecord.Note,
            FinalWeight: vouchRecord.FinalCalculatedWeight,
            CreatedAt: vouchRecord.CreatedAt
        );
    }

    public async Task<TrustScoreSummaryDto> GetUserTrustSummaryAsync(Guid userId, CancellationToken ct = default, Guid? viewerId = null)
    {
        var viewer = viewerId ?? userId;
        if (viewer != userId) await ResourceAccess.RequirePeerAsync(_context, viewer, userId, allowIncubation: true, ct: ct);
        else SessionService.EnsureUsable(await _context.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct));
        var blockedIds = await _context.Blocks.Where(b => b.BlockerId == viewer || b.BlockedUserId == viewer)
            .Select(b => b.BlockerId == viewer ? b.BlockedUserId : b.BlockerId).ToListAsync(ct);
        var user = await _context.Users
            .AsNoTracking()
            .Include(u => u.VouchesReceived)
                .ThenInclude(v => v.VoucherUser)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var recentVouches = user.VouchesReceived
            .Where(v => v.VoucherUser.CampusId == user.CampusId && !blockedIds.Contains(v.VoucherUserId) &&
                v.VoucherUser.Status == AccountStatus.Active)
            .OrderByDescending(v => v.CreatedAt)
            .Take(10)
            .Select(v => new VouchDto(
                Id: v.Id,
                VoucherId: v.VoucherUserId,
                VoucherName: v.VoucherUser.FullName,
                Traits: v.Traits,
                Note: v.Note,
                FinalWeight: v.FinalCalculatedWeight,
                CreatedAt: v.CreatedAt
            ))
            .ToList();

        return new TrustScoreSummaryDto(
            UserId: user.Id,
            TrustScore: user.TrustScore,
            TotalVouchesReceived: user.ActiveVouchesReceivedCount,
            IsIncubationComplete: user.Status != AccountStatus.InIncubation,
            RecentVouches: recentVouches
        );
    }

    private async Task<int> CalculateMutualVouchersAsync(Guid userAId, Guid userBId, CancellationToken ct = default)
    {
        // Mutual vouchers = users who have vouched for BOTH user A and user B
        var vouchersForA = await _context.Vouches
            .Where(v => v.TargetUserId == userAId)
            .Select(v => v.VoucherUserId)
            .ToListAsync(ct);

        if (vouchersForA.Count == 0) return 0;

        var mutualCount = await _context.Vouches
            .Where(v => v.TargetUserId == userBId && vouchersForA.Contains(v.VoucherUserId))
            .CountAsync(ct);

        return mutualCount;
    }
}
