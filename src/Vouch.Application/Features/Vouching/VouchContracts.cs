using Vouch.Domain.Enums;
using Vouch.Domain.Entities;

namespace Vouch.Application.Features.Vouching;

public record SubmitVouchRequest(
    Guid TargetUserId,
    CharacterTrait Traits,
    string? Note
);

public record VouchDto(
    Guid Id,
    Guid VoucherId,
    string VoucherName,
    CharacterTrait Traits,
    string? Note,
    double FinalWeight,
    DateTimeOffset CreatedAt
);

public record TrustScoreSummaryDto(
    Guid UserId,
    double TrustScore,
    int TotalVouchesReceived,
    bool IsIncubationComplete,
    IReadOnlyList<VouchDto> RecentVouches
);

public interface ITrustService
{
    Task<VouchDto> SubmitVouchAsync(Guid voucherUserId, SubmitVouchRequest request, CancellationToken ct = default);
    Task<TrustScoreSummaryDto> GetUserTrustSummaryAsync(Guid userId, CancellationToken ct = default, Guid? viewerId = null);
    Task<PeerVouchRequestDto> RequestVouchAsync(Guid requesterId, RequestPeerVouchRequest request, CancellationToken ct = default);
    Task<PeerVouchRequestPage> GetRequestsAsync(Guid userId, bool incoming, int page = 1, int pageSize = 20, CancellationToken ct = default);
    Task ResolveRequestAsync(Guid userId, Guid requestId, bool dismiss, CancellationToken ct = default);
}

public record RequestPeerVouchRequest(Guid RequestedVoucherId);
public record PeerVouchRequestDto(Guid Id, Guid RequesterId, string RequesterName, Guid RequestedVoucherId,
    string RequestedVoucherName, PeerVouchRequestStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ResolvedAt);
public record PeerVouchRequestPage(IReadOnlyList<PeerVouchRequestDto> Items, int TotalCount, int Page, int PageSize);
