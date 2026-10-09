using FluentValidation;
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

public partial class TrustService(ApplicationDbContext context, ISlowBurnNotificationService notificationService,
    ILogger<TrustService> logger, TimeProvider clock) : ITrustService
{
    public async Task<VouchDto> SubmitVouchAsync(Guid voucherUserId, SubmitVouchRequest request, CancellationToken ct = default)
    {
        await new SubmitVouchRequestValidator().ValidateAndThrowAsync(request, ct);
        if (voucherUserId == request.TargetUserId) throw new ArgumentException("You cannot vouch for yourself.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var campusId = await LockCallerCampusAsync(voucherUserId, ct);
        var target = await ResourceAccess.RequirePeerAsync(context, voucherUserId, request.TargetUserId, allowIncubation: true, ct: ct);
        var now = clock.GetUtcNow();
        // Read current derived metrics only after the campus lock, never a stale tracked counter.
        await TrustMetrics.RecalculateAsync(context, campusId, now, ct);
        var voucher = await context.Users.AsNoTracking().SingleAsync(u => u.Id == voucherUserId, ct);
        if (await context.Vouches.AnyAsync(v => v.TargetUserId == target.Id && v.VoucherUserId == voucher.Id, ct))
            throw new InvalidOperationException("You have already vouched for this person. Re-vouching is not permitted.");
        var qualifying = TrustMetrics.Qualifying(context);
        var mutualVouchers = await qualifying.Where(v => v.TargetUserId == target.Id &&
            qualifying.Any(a => a.TargetUserId == voucher.Id && a.VoucherUserId == v.VoucherUserId))
            .Select(v => v.VoucherUserId).Distinct().CountAsync(ct);
        var flagged = mutualVouchers >= TrustScoreCalculator.CliqueMutualVouchersThreshold;
        var record = new VouchRecord
        {
            TargetUserId = target.Id, VoucherUserId = voucher.Id, Traits = request.Traits, Note = request.Note,
            CreatedAt = now, BaseWeight = TrustScoreCalculator.BaseVouchWeight,
            VoucherBonus = voucher.ActiveVouchesReceivedCount >= TrustScoreCalculator.HighTrustVoucherThreshold ? TrustScoreCalculator.HighTrustVoucherBonus : 0,
            CliqueDampeningMultiplier = flagged ? TrustScoreCalculator.CliqueDampeningMultiplier : 1,
            IsCliqueFlagged = flagged, MutualVoucherCountAtSubmission = mutualVouchers,
            IsZeroWeightDueToAccountAge = now - voucher.CreatedAt < TimeSpan.FromDays(TrustScoreCalculator.MinAccountAgeDaysForWeight),
            FinalCalculatedWeight = TrustScoreCalculator.CalculateVouchWeight(voucher, mutualVouchers, now)
        };
        var oldScore = await context.Users.Where(u => u.Id == target.Id).Select(u => u.TrustScore).SingleAsync(ct);
        context.Vouches.Add(record);
        await context.VouchRequests.Where(r => r.RequesterId == target.Id && r.RequestedVoucherId == voucher.Id &&
            r.Status == PeerVouchRequestStatus.Pending && r.ExpiresAt > now).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, PeerVouchRequestStatus.Fulfilled).SetProperty(r => r.ResolvedAt, now), ct);
        await context.SaveChangesAsync(ct); // Recomputes totals, activation and launch readiness in this transaction.
        var newScore = await context.Users.Where(u => u.Id == target.Id).Select(u => u.TrustScore).SingleAsync(ct);
        var recent = await TrustMetrics.Qualifying(context).Where(v => v.TargetUserId == target.Id && v.CreatedAt >= now.AddHours(-48))
            .SumAsync(v => (double?)v.FinalCalculatedWeight, ct) ?? 0;
        await transaction.CommitAsync(ct);
        if (TrustScoreCalculator.IsAnomalyVelocity(recent))
        {
            logger.LogWarning("Trust anomaly. UserId={UserId} ScoreIncrease48h={ScoreIncrease}", target.Id, recent);
            try { await notificationService.NotifyTrustScoreAlertToArchitectAsync(target.Id, oldScore, newScore, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogError(ex, "Trust alert delivery failed after committed vouch. UserId={UserId}", target.Id); }
        }
        return new(record.Id, voucher.Id, voucher.FullName, record.Traits, record.Note, record.FinalCalculatedWeight, record.CreatedAt);
    }

    public async Task<TrustScoreSummaryDto> GetUserTrustSummaryAsync(Guid userId, CancellationToken ct = default, Guid? viewerId = null)
    {
        var viewer = viewerId ?? userId;
        if (viewer != userId) await ResourceAccess.RequirePeerAsync(context, viewer, userId, allowIncubation: true, ct: ct);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");
        if (viewer == userId) SessionService.EnsureUsable(user);
        var qualifying = TrustMetrics.Qualifying(context).Where(v => v.TargetUserId == userId);
        var count = await qualifying.CountAsync(ct);
        var score = Math.Max(0, TrustScoreCalculator.ApplyScoreCap(await qualifying.SumAsync(v => (double?)v.FinalCalculatedWeight, ct) ?? 0));
        var recent = await qualifying.Where(v => !context.Blocks.Any(b => b.BlockerId == viewer && b.BlockedUserId == v.VoucherUserId ||
            b.BlockedUserId == viewer && b.BlockerId == v.VoucherUserId))
            .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).Take(10)
            .Select(v => new VouchDto(v.Id, v.VoucherUserId, v.VoucherUser.FullName, v.Traits, v.Note, v.FinalCalculatedWeight, v.CreatedAt)).ToListAsync(ct);
        return new(user.Id, score, count, user.Status != AccountStatus.InIncubation, recent);
    }

    private async Task<Guid> LockCallerCampusAsync(Guid userId, CancellationToken ct)
    {
        var campusId = await context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (Guid?)u.CampusId).SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Account is unavailable.");
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {campusId} FOR UPDATE", ct);
        return campusId;
    }
}
