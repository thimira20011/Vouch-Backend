using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common;
using Vouch.Application.Features.Vouching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public partial class TrustService
{
    public const int DailyRequestLimit = 5;
    public const int PendingRequestLimit = 10;
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(7);

    public async Task<PeerVouchRequestDto> RequestVouchAsync(Guid requesterId, RequestPeerVouchRequest request, CancellationToken ct = default)
    {
        await new RequestPeerVouchRequestValidator().ValidateAndThrowAsync(request, ct);
        if (requesterId == request.RequestedVoucherId) throw new ArgumentException("You cannot request a vouch from yourself.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockCallerCampusAsync(requesterId, ct);
        await RequireRequestPairAsync(requesterId, request.RequestedVoucherId, ct);
        var now = clock.GetUtcNow();
        if (await context.Vouches.AnyAsync(v => v.TargetUserId == requesterId && v.VoucherUserId == request.RequestedVoucherId, ct))
            throw new InvalidOperationException("This peer has already vouched for you.");
        if (await context.VouchRequests.AnyAsync(r => r.RequesterId == requesterId &&
            r.RequestedVoucherId == request.RequestedVoucherId && r.CreatedAt > now.Subtract(RequestLifetime), ct))
            throw new InvalidOperationException("Wait seven days before requesting this peer again.");
        if (await context.VouchRequests.CountAsync(r => r.RequesterId == requesterId && r.CreatedAt > now.AddHours(-24), ct) >= DailyRequestLimit ||
            await context.VouchRequests.CountAsync(r => r.RequesterId == requesterId && r.Status == PeerVouchRequestStatus.Pending && r.ExpiresAt > now, ct) >= PendingRequestLimit)
            throw new InvalidOperationException("Your vouch request limit has been reached. Try again later.");
        await context.VouchRequests.Where(r => r.RequesterId == requesterId && r.RequestedVoucherId == request.RequestedVoucherId &&
            r.Status == PeerVouchRequestStatus.Pending && r.ExpiresAt <= now).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, PeerVouchRequestStatus.Expired).SetProperty(r => r.ResolvedAt, now), ct);
        var record = new PeerVouchRequest { RequesterId = requesterId, RequestedVoucherId = request.RequestedVoucherId,
            CreatedAt = now, ExpiresAt = now.Add(RequestLifetime) };
        context.VouchRequests.Add(record);
        await context.SaveChangesAsync(ct);
        var result = await RequestDtoAsync(record.Id, now, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<PeerVouchRequestPage> GetRequestsAsync(Guid userId, bool incoming, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1 || page > 10000 || pageSize < 1 || pageSize > 50) throw new ArgumentException("Use page 1–10000 and pageSize 1–50.");
        await RequireRequestCallerAsync(userId, ct);
        var now = clock.GetUtcNow();
        var query = VisibleRequests().Where(r => incoming ? r.RequestedVoucherId == userId : r.RequesterId == userId);
        var total = await query.CountAsync(ct);
        var items = await ProjectRequests(query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize), now).ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    public async Task ResolveRequestAsync(Guid userId, Guid requestId, bool dismiss, CancellationToken ct = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockCallerCampusAsync(userId, ct);
        await RequireRequestCallerAsync(userId, ct);
        var record = await VisibleRequests().AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId &&
            (dismiss ? r.RequestedVoucherId == userId : r.RequesterId == userId), ct)
            ?? throw new EligibilityException("Request is unavailable.");
        var status = dismiss ? PeerVouchRequestStatus.Dismissed : PeerVouchRequestStatus.Cancelled;
        if (record.Status == status) return;
        var now = clock.GetUtcNow();
        if (record.Status != PeerVouchRequestStatus.Pending || record.ExpiresAt <= now)
            throw new InvalidOperationException("Request is no longer pending.");
        await context.VouchRequests.Where(r => r.Id == record.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, status).SetProperty(r => r.ResolvedAt, now), ct);
        await transaction.CommitAsync(ct);
    }

    private async Task RequireRequestCallerAsync(Guid userId, CancellationToken ct)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("Account is unavailable.");
        SessionService.EnsureUsable(user);
        if (!UniversityIdentity.IsOnboarded(user)) throw new EligibilityException("Complete verified onboarding before requesting vouches.");
    }
    private async Task RequireRequestPairAsync(Guid requesterId, Guid voucherId, CancellationToken ct)
    {
        await RequireRequestCallerAsync(requesterId, ct);
        var requester = await context.Users.AsNoTracking().SingleAsync(u => u.Id == requesterId, ct);
        var voucher = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == voucherId, ct);
        if (voucher is null || requester.CampusId != voucher.CampusId || !UniversityIdentity.IsActiveMember(voucher) ||
            await ResourceAccess.IsBlockedAsync(context, requesterId, voucherId, ct)) throw new EligibilityException("Peer is unavailable.");
    }
    private IQueryable<PeerVouchRequest> VisibleRequests() => context.VouchRequests.Where(r => r.RequesterId != r.RequestedVoucherId &&
        r.Requester.CampusId == r.RequestedVoucher.CampusId && r.Requester.EmailVerifiedAt != null && r.Requester.OnboardingCompletedAt != null &&
        (r.Requester.Status == AccountStatus.Active || r.Requester.Status == AccountStatus.InIncubation) &&
        r.RequestedVoucher.Status == AccountStatus.Active && r.RequestedVoucher.EmailVerifiedAt != null && r.RequestedVoucher.OnboardingCompletedAt != null &&
        !context.Blocks.Any(b => b.BlockerId == r.RequesterId && b.BlockedUserId == r.RequestedVoucherId ||
                                b.BlockerId == r.RequestedVoucherId && b.BlockedUserId == r.RequesterId));
    private static IQueryable<PeerVouchRequestDto> ProjectRequests(IQueryable<PeerVouchRequest> requests, DateTimeOffset now) => requests
        .Select(r => new PeerVouchRequestDto(r.Id, r.RequesterId, r.Requester.FullName, r.RequestedVoucherId, r.RequestedVoucher.FullName,
            r.Status == PeerVouchRequestStatus.Pending && r.ExpiresAt <= now ? PeerVouchRequestStatus.Expired : r.Status, r.CreatedAt, r.ExpiresAt, r.ResolvedAt));
    private Task<PeerVouchRequestDto> RequestDtoAsync(Guid id, DateTimeOffset now, CancellationToken ct)
        => ProjectRequests(VisibleRequests().Where(r => r.Id == id), now).SingleAsync(ct);
}
