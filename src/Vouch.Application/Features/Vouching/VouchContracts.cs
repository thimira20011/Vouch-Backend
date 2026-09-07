using Vouch.Domain.Enums;

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
    Task<TrustScoreSummaryDto> GetUserTrustSummaryAsync(Guid userId, CancellationToken ct = default);
    Task<int> CalculateMutualVouchersAsync(Guid userAId, Guid userBId, CancellationToken ct = default);
}
