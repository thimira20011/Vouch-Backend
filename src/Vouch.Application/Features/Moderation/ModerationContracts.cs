using Vouch.Domain.Enums;
using Vouch.Domain.Services;

namespace Vouch.Application.Features.Moderation;

public record CreateReportRequest(
    Guid ReportedUserId,
    Guid? MessageId,
    ReportCategory Category,
    string Details
);

public record BlockUserRequest(
    Guid TargetUserId
);

public record ReportDto(
    Guid Id,
    Guid ReporterId,
    string ReporterEmail,
    Guid ReportedUserId,
    string ReportedUserName,
    ReportCategory Category,
    string Details,
    int SeverityScore,
    ReportStatus Status,
    DateTimeOffset CreatedAt
);

public record ArchitectDashboardDto(
    LaunchReadinessMetrics LaunchReadiness,
    int TotalActiveUsers,
    int TotalInIncubationUsers,
    int PendingReportsCount,
    IReadOnlyList<ReportDto> CriticalReports,
    IReadOnlyList<string> TrustScoreAnomalies
);

public record ResolveReportBody(bool Uphold, string? Notes);

public record CreateAmbassadorInviteRequest(
    Guid CampusId,
    string? IntendedEmail = null
);

public record AmbassadorInviteDto(
    Guid InviteId,
    string Token,
    string? IntendedEmail,
    DateTimeOffset ExpiresAt
);

public interface IModerationService
{
    Task<ReportDto> SubmitReportAsync(Guid reporterId, CreateReportRequest request, CancellationToken ct = default);
    Task<bool> BlockUserAsync(Guid blockerId, BlockUserRequest request, CancellationToken ct = default);
    Task<bool> ResolveReportAsync(Guid architectId, Guid reportId, bool uphold, string? notes, CancellationToken ct = default);
    Task<ArchitectDashboardDto> GetArchitectDashboardAsync(Guid campusId, CancellationToken ct = default);
    Task<AmbassadorInviteDto> CreateAmbassadorInviteAsync(Guid architectId, CreateAmbassadorInviteRequest request, CancellationToken ct = default);
}
