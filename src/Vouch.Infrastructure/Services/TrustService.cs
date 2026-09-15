using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Vouching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;

namespace Vouch.Infrastructure.Services;

public class TrustService : ITrustService
{
    private readonly IApplicationDbContext _context;
    private readonly ISlowBurnNotificationService _notificationService;

    public TrustService(IApplicationDbContext context, ISlowBurnNotificationService notificationService)
    {
        _context = context;
        _notificationService = notificationService;
    }

    public async Task<VouchDto> SubmitVouchAsync(
        Guid voucherUserId,
        SubmitVouchRequest request,
        CancellationToken ct = default)
    {
        if (voucherUserId == request.TargetUserId)
        {
            throw new InvalidOperationException("You cannot vouch for yourself.");
        }

        var voucher = await _context.Users
            .Include(u => u.VouchesReceived)
            .FirstOrDefaultAsync(u => u.Id == voucherUserId, ct)
            ?? throw new KeyNotFoundException("Voucher user not found.");

        var target = await _context.Users
            .Include(u => u.VouchesReceived)
            .FirstOrDefaultAsync(u => u.Id == request.TargetUserId, ct)
            ?? throw new KeyNotFoundException("Target user not found.");

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
        if (target.Status == AccountStatus.InIncubation && target.ActiveVouchesReceivedCount >= 3)
        {
            target.Status = AccountStatus.Active;
            target.IncubationCompletedAt = now;
        }

        // REQ-A6: Persist campus launch readiness here (on vouch submission) — not on the GET dashboard read
        var campus = await _context.Campuses.FirstOrDefaultAsync(c => c.Id == target.CampusId, ct);
        if (campus is not null)
        {
            var ambassadors = await _context.Users
                .Include(u => u.VouchesGiven)
                .Where(u => u.CampusId == campus.Id && u.Role == UserRole.Ambassador && u.Status == AccountStatus.Active)
                .ToListAsync(ct);

            var activeAmbassadorCount = ambassadors.Count;
            var ambassadorsMeetingVouchTarget = ambassadors.Count(a => a.VouchesGiven.Count >= campus.RequiredVouchesPerAmbassador);
            var readiness = Vouch.Domain.Services.LaunchReadinessCalculator.CalculateReadiness(
                activeAmbassadorCount, ambassadorsMeetingVouchTarget, campus.RequiredAmbassadorsForLaunch);

            campus.LaunchReadinessScore = readiness.LaunchReadinessPercentage;
            campus.IsSoftLaunchUnlocked = readiness.IsGatePassed;
        }

        await _context.SaveChangesAsync(ct);

        // REQ-10: Anomaly check: alert if score increases by > 3 points in 48 hours
        var fortyEightHoursAgo = now.AddHours(-48);
        var recentWeightsSum = await _context.Vouches
            .Where(v => v.TargetUserId == target.Id && v.CreatedAt >= fortyEightHoursAgo)
            .SumAsync(v => v.FinalCalculatedWeight, ct);

        if (TrustScoreCalculator.IsAnomalyVelocity(recentWeightsSum))
        {
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

    public async Task<TrustScoreSummaryDto> GetUserTrustSummaryAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users
            .AsNoTracking()
            .Include(u => u.VouchesReceived)
                .ThenInclude(v => v.VoucherUser)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var recentVouches = user.VouchesReceived
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
