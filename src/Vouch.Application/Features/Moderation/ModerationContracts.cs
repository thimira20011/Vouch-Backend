using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Domain.Entities;

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
    DateTimeOffset CreatedAt,
    Guid? RelatedMessageId = null,
    Guid? ResolvedByArchitectId = null,
    DateTimeOffset? ResolvedAt = null,
    string? ArchitectNotes = null
);

public record ArchitectDashboardDto(
    LaunchReadinessMetrics LaunchReadiness,
    int TotalActiveUsers,
    int TotalInIncubationUsers,
    int PendingReportsCount,
    IReadOnlyList<ReportDto> CriticalReports,
    IReadOnlyList<TrustScoreAnomalyDto> TrustScoreAnomalies
);

/// <summary>
/// Step 20: A single trust score anomaly surfaced in the Architect dashboard.
/// </summary>
public record TrustScoreAnomalyDto(
    Guid UserId,
    string UserName,
    string Email,
    double TrustScore,
    int VouchCount,
    string AnomalyType,     // "VelocitySpike" | "AtScoreCap" | "ScoreVouchMismatch"
    string Description,
    DateTimeOffset DetectedAt
);

public record ResolveReportBody(bool Uphold, string? Notes);
public record AccountModerationBody(string Reason);
public record ReportPage(IReadOnlyList<ReportDto> Items, int TotalCount, int Page, int PageSize);
public record ReportEvidenceDto(ReportDto Report, bool MessageEvidenceAvailable, string? MessageBody);
public record ModerationAuditDto(Guid Id, Guid ArchitectId, Guid? ReportId, ModerationAction Action,
    AccountStatus PreviousStatus, AccountStatus ResultingStatus, string Reason, DateTimeOffset CreatedAt);
public record AccountModerationReviewDto(Guid UserId, string FullName, AccountStatus Status,
    AccountStatus? StatusBeforeSuspension, DateTimeOffset? SuspendedAt, int UpheldReportsCount,
    int UpheldReportsIn90Days, int PendingReportsCount, IReadOnlyList<ModerationAuditDto> Audit,
    int TotalAuditCount, int Page, int PageSize);

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
    Task<ArchitectDashboardDto> GetArchitectDashboardAsync(Guid campusId, CancellationToken ct = default, Guid? architectId = null);
    Task<AmbassadorInviteDto> CreateAmbassadorInviteAsync(Guid architectId, CreateAmbassadorInviteRequest request, CancellationToken ct = default);
    Task ApproveAmbassadorAsync(Guid architectId, Guid userId, CancellationToken ct = default);
    Task<ReportPage> GetReportsAsync(Guid architectId, Guid campusId, ReportStatus status = ReportStatus.Pending,
        int page = 1, int pageSize = 20, CancellationToken ct = default);
    Task<ReportEvidenceDto> GetReportAsync(Guid architectId, Guid reportId, CancellationToken ct = default);
    Task<AccountModerationReviewDto> GetAccountReviewAsync(Guid architectId, Guid userId, int page = 1, int pageSize = 20, CancellationToken ct = default);
    Task ChangeSuspensionAsync(Guid architectId, Guid userId, bool reinstate, AccountModerationBody body, CancellationToken ct = default);
}
