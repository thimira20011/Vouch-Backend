using Microsoft.EntityFrameworkCore;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Moderation;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public class ModerationService : IModerationService
{
    private readonly IApplicationDbContext _context;
    private readonly IEmailService _emailService;
    private readonly string? _architectEmail;
    private readonly ILogger<ModerationService> _logger;

    public ModerationService(
        IApplicationDbContext context,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<ModerationService> logger)
    {
        _context = context;
        _emailService = emailService;
        _architectEmail = configuration["Smtp:ArchitectEmail"];
        _logger = logger;
    }

    public async Task<ReportDto> SubmitReportAsync(Guid reporterId, CreateReportRequest request, CancellationToken ct = default)
    {
        var reporter = await _context.Users.FirstOrDefaultAsync(u => u.Id == reporterId, ct)
            ?? throw new KeyNotFoundException("Reporter not found.");

        var reportedUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == request.ReportedUserId, ct)
            ?? throw new KeyNotFoundException("Reported user not found.");

        var severity = request.Category switch
        {
            ReportCategory.Harassment => 5,
            ReportCategory.Impersonation => 5,
            ReportCategory.InappropriateContent => 4,
            ReportCategory.Spam => 3,
            _ => 2
        };

        var report = new Report
        {
            ReporterId = reporterId,
            ReportedUserId = request.ReportedUserId,
            RelatedMessageId = request.MessageId,
            Category = request.Category,
            Details = request.Details,
            SeverityScore = severity,
            Status = ReportStatus.Pending
        };

        _context.Reports.Add(report);

        // NFR-11: Reported profiles shall be automatically soft-hidden from matchmaking pending review
        reportedUser.IsSoftHiddenFromMatchmaking = true;

        await _context.SaveChangesAsync(ct);

        // Step 22: structured logging
        if (severity >= 4)
            _logger.LogWarning("High-severity report submitted. Category={Category} Severity={Severity} ReporterId={ReporterId} ReportedUserId={ReportedUserId}",
                request.Category, severity, reporterId, request.ReportedUserId);
        else
            _logger.LogInformation("Report submitted. Category={Category} Severity={Severity} ReporterId={ReporterId} ReportedUserId={ReportedUserId}",
                request.Category, severity, reporterId, request.ReportedUserId);

        // NFR-12: High-severity reports (>= 4) trigger an immediate email alert to the Architect
        if (severity >= 4 && !string.IsNullOrWhiteSpace(_architectEmail))
        {
            var subject = $"[Vouch Alert] High-Severity Report: {request.Category} (Severity {severity}/5)";
            var body = string.Join(Environment.NewLine,
                "A high-severity report has been submitted on Vouch.",
                string.Empty,
                $"Category  : {request.Category}",
                $"Severity  : {severity}/5",
                $"Reporter  : {reporter.Email} (ID: {reporter.Id})",
                $"Reported  : {reportedUser.FullName} (ID: {reportedUser.Id})",
                $"Details   : {request.Details}",
                $"Submitted : {DateTimeOffset.UtcNow:u}",
                string.Empty,
                "Please review this report in the Architect dashboard.");
            // Fire-and-forget — SmtpEmailService never throws; failure is only logged
            await _emailService.SendAsync(_architectEmail, subject, body, ct);
        }

        return new ReportDto(
            Id: report.Id,
            ReporterId: reporter.Id,
            ReporterEmail: reporter.Email,
            ReportedUserId: reportedUser.Id,
            ReportedUserName: reportedUser.FullName,
            Category: report.Category,
            Details: report.Details,
            SeverityScore: report.SeverityScore,
            Status: report.Status,
            CreatedAt: report.CreatedAt
        );
    }

    public async Task<bool> BlockUserAsync(Guid blockerId, BlockUserRequest request, CancellationToken ct = default)
    {
        if (blockerId == request.TargetUserId)
        {
            throw new InvalidOperationException("You cannot block yourself.");
        }

        var exists = await _context.Blocks
            .AnyAsync(b => b.BlockerId == blockerId && b.BlockedUserId == request.TargetUserId, ct);

        if (!exists)
        {
            _context.Blocks.Add(new Block
            {
                BlockerId = blockerId,
                BlockedUserId = request.TargetUserId
            });
            await _context.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<bool> ResolveReportAsync(Guid architectId, Guid reportId, bool uphold, string? notes, CancellationToken ct = default)
    {
        var report = await _context.Reports
            .Include(r => r.ReportedUser)
            .FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        report.Status = uphold ? ReportStatus.Upheld : ReportStatus.Dismissed;
        report.ArchitectNotes = notes;
        report.ResolvedAt = DateTimeOffset.UtcNow;

        if (uphold)
        {
            report.ReportedUser.UpheldReportsCount += 1;

            // NFR-14: Three upheld reports within a 90-day window shall result in automatic account suspension
            var ninetyDaysAgo = DateTimeOffset.UtcNow.AddDays(-90);
            var upheldReportsCountIn90Days = await _context.Reports
                .Where(r => r.ReportedUserId == report.ReportedUserId &&
                            r.Status == ReportStatus.Upheld &&
                            r.CreatedAt >= ninetyDaysAgo)
                .CountAsync(ct);

            if (upheldReportsCountIn90Days >= 3)
            {
                report.ReportedUser.Status = AccountStatus.Suspended;
            }
        }
        else
        {
            // If dismissed and no other pending reports, unhide
            var otherPending = await _context.Reports
                .AnyAsync(r => r.ReportedUserId == report.ReportedUserId &&
                               r.Id != reportId &&
                               r.Status == ReportStatus.Pending, ct);

            if (!otherPending)
            {
                report.ReportedUser.IsSoftHiddenFromMatchmaking = false;
            }
        }

        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ArchitectDashboardDto> GetArchitectDashboardAsync(Guid campusId, CancellationToken ct = default)
    {
        var campus = await _context.Campuses
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == campusId, ct)
            ?? throw new KeyNotFoundException("Campus not found.");

        // Count ambassadors and vouches (REQ-A2, REQ-A5)
        var readiness = await LaunchEligibility.CalculateAsync(_context, campusId,
            campus.RequiredAmbassadorsForLaunch, campus.RequiredVouchesPerAmbassador, ct);

        // Note: campus launch readiness is calculated live here and returned to the Architect.
        // Persisting it is done only when a vouch is submitted (a state-changing event),
        // not on every read — a GET method should never write to the database.

        var totalActive = await _context.Users.CountAsync(u => u.CampusId == campusId && u.Status == AccountStatus.Active, ct);
        var totalIncubation = await _context.Users.CountAsync(u => u.CampusId == campusId && u.Status == AccountStatus.InIncubation, ct);

        var pendingReports = await _context.Reports
            .AsNoTracking()
            .Include(r => r.Reporter)
            .Include(r => r.ReportedUser)
            .Where(r => r.Status == ReportStatus.Pending)
            .OrderByDescending(r => r.SeverityScore)
            .Take(20)
            .Select(r => new ReportDto(
                r.Id,
                r.ReporterId,
                r.Reporter.Email,
                r.ReportedUserId,
                r.ReportedUser.FullName,
                r.Category,
                r.Details,
                r.SeverityScore,
                r.Status,
                r.CreatedAt
            ))
            .ToListAsync(ct);

        // Step 20 — Trust Score Anomaly Detection
        // Signal 1: VelocitySpike — user received > 3.0 trust points in the last 48 hours (REQ-10)
        var velocityAnomalyThreshold = TrustScoreCalculator.AnomalyScoreThreshold48Hours;
        var fortyEightHoursAgo = DateTimeOffset.UtcNow.AddHours(-48);

        var velocityAnomalies = await _context.Vouches
            .AsNoTracking()
            .Where(v => v.TargetUser.CampusId == campusId
                     && v.CreatedAt >= fortyEightHoursAgo)
            .GroupBy(v => new { v.TargetUserId, v.TargetUser.FullName, v.TargetUser.Email, v.TargetUser.TrustScore, v.TargetUser.ActiveVouchesReceivedCount })
            .Where(g => g.Sum(v => v.FinalCalculatedWeight) > velocityAnomalyThreshold)
            .Select(g => new TrustScoreAnomalyDto(
                g.Key.TargetUserId,
                g.Key.FullName,
                g.Key.Email,
                g.Key.TrustScore,
                g.Key.ActiveVouchesReceivedCount,
                "VelocitySpike",
                $"Received {g.Sum(v => v.FinalCalculatedWeight):F2} trust points in 48 hours (threshold: {velocityAnomalyThreshold})",
                DateTimeOffset.UtcNow))
            .ToListAsync(ct);

        // Signal 2: AtScoreCap — user reached the 20.0 maximum cap (REQ-7)
        var scoreCap = TrustScoreCalculator.MaxTrustScoreCap;
        var capAnomalies = await _context.Users
            .AsNoTracking()
            .Where(u => u.CampusId == campusId
                     && u.TrustScore >= scoreCap
                     && u.Status == AccountStatus.Active)
            .Select(u => new TrustScoreAnomalyDto(
                u.Id,
                u.FullName,
                u.Email,
                u.TrustScore,
                u.ActiveVouchesReceivedCount,
                "AtScoreCap",
                $"Trust score has reached the maximum cap of {scoreCap}. Verify legitimacy before accepting further vouches.",
                DateTimeOffset.UtcNow))
            .ToListAsync(ct);

        // Signal 3: ScoreVouchMismatch — trust score > 15 with fewer than 5 vouches
        // Mathematically impossible with standard weights (max 1.2 per vouch × 5 = 6.0)
        // Indicates data corruption or injection attempt
        var mismatchAnomalies = await _context.Users
            .AsNoTracking()
            .Where(u => u.CampusId == campusId
                     && u.TrustScore > 15.0
                     && u.ActiveVouchesReceivedCount < 5)
            .Select(u => new TrustScoreAnomalyDto(
                u.Id,
                u.FullName,
                u.Email,
                u.TrustScore,
                u.ActiveVouchesReceivedCount,
                "ScoreVouchMismatch",
                $"Trust score of {u.TrustScore:F2} is inconsistent with {u.ActiveVouchesReceivedCount} recorded vouch(es). Investigate for data integrity issues.",
                DateTimeOffset.UtcNow))
            .ToListAsync(ct);

        // Merge and deduplicate: if a user appears in multiple signals, keep all entries
        // (useful for the Architect to see all flags against one user)
        var allAnomalies = velocityAnomalies
            .Concat(capAnomalies)
            .Concat(mismatchAnomalies)
            .OrderByDescending(a => a.TrustScore)
            .ToList();

        return new ArchitectDashboardDto(
            LaunchReadiness: readiness,
            TotalActiveUsers: totalActive,
            TotalInIncubationUsers: totalIncubation,
            PendingReportsCount: pendingReports.Count,
            CriticalReports: pendingReports.Where(r => r.SeverityScore >= 4).ToList(),
            TrustScoreAnomalies: allAnomalies
        );
    }

    public async Task ApproveAmbassadorAsync(Guid architectId, Guid userId, CancellationToken ct = default)
    {
        var architect = await _context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == architectId, ct);
        var user = await _context.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        if (architect is null || architect.Role != UserRole.Architect || architect.Status != AccountStatus.Active)
            throw new Vouch.Application.Common.EligibilityException("An active Architect must approve the ambassador.");
        if (!UniversityIdentity.IsOnboarded(user) || user.Status is not (AccountStatus.InIncubation or AccountStatus.Active))
            throw new Vouch.Application.Common.EligibilityException("Ambassador approval requires verified onboarding and an available account.");
        if (user.Role == UserRole.Architect) throw new ArgumentException("An Architect cannot be converted to an ambassador.");
        user.Role = UserRole.Ambassador;
        user.Status = AccountStatus.Active;
        user.HasFoundingMemberBadge = true;
        user.AmbassadorApprovedByArchitectId ??= architectId;
        user.AmbassadorApprovedAt ??= DateTimeOffset.UtcNow;
        user.IncubationCompletedAt ??= DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
        _logger.LogInformation("Ambassador manually approved. UserId={UserId} ArchitectId={ArchitectId}", userId, architectId);
    }

    public async Task<AmbassadorInviteDto> CreateAmbassadorInviteAsync(
        Guid architectId,
        CreateAmbassadorInviteRequest request,
        CancellationToken ct = default)
    {
        var architect = await _context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == architectId, ct);
        if (architect is null || architect.Role != UserRole.Architect || architect.Status != AccountStatus.Active)
            throw new Vouch.Application.Common.EligibilityException("Only an active Architect may approve ambassador invitations.");
        await new Vouch.Application.Features.Moderation.CreateAmbassadorInviteRequestValidator().ValidateAndThrowAsync(request, ct);
        var campus = await _context.Campuses
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.CampusId, ct)
            ?? throw new KeyNotFoundException($"Campus '{request.CampusId}' not found.");

        var intendedEmail = request.IntendedEmail!.Trim().ToLowerInvariant();
        if (!UniversityIdentity.IsApprovedEmail(intendedEmail, campus.DomainPattern))
            throw new ArgumentException("Invite email must use the selected campus's approved mailbox domain.");
        var token = SingleUseToken.Create();

        var invite = new Vouch.Domain.Entities.AmbassadorInvite
        {
            CampusId = campus.Id,
            IssuedByArchitectId = architectId,
            TokenHash = SingleUseToken.Hash(token),
            IntendedEmail = intendedEmail,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };

        _context.AmbassadorInvites.Add(invite);
        await _context.SaveChangesAsync(ct);

        return new AmbassadorInviteDto(
            InviteId: invite.Id,
            Token: token,
            IntendedEmail: invite.IntendedEmail,
            ExpiresAt: invite.ExpiresAt
        );
    }
}
