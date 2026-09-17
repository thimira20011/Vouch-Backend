using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Moderation;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;

namespace Vouch.Infrastructure.Services;

public class ModerationService : IModerationService
{
    private readonly IApplicationDbContext _context;

    public ModerationService(IApplicationDbContext context)
    {
        _context = context;
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
        var ambassadors = await _context.Users
            .AsNoTracking()
            .Where(u => u.CampusId == campusId && u.Role == UserRole.Ambassador && u.Status == AccountStatus.Active)
            .Include(u => u.VouchesGiven)
            .ToListAsync(ct);

        var activeAmbassadorCount = ambassadors.Count;
        var ambassadorsMeetingVouchTarget = ambassadors.Count(a => a.VouchesGiven.Count >= campus.RequiredVouchesPerAmbassador);

        var readiness = LaunchReadinessCalculator.CalculateReadiness(
            activeAmbassadorCount,
            ambassadorsMeetingVouchTarget,
            campus.RequiredAmbassadorsForLaunch
        );

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

        return new ArchitectDashboardDto(
            LaunchReadiness: readiness,
            TotalActiveUsers: totalActive,
            TotalInIncubationUsers: totalIncubation,
            PendingReportsCount: pendingReports.Count,
            CriticalReports: pendingReports.Where(r => r.SeverityScore >= 4).ToList(),
            TrustScoreAnomalies: new List<string>()
        );
    }

    public async Task<AmbassadorInviteDto> CreateAmbassadorInviteAsync(
        Guid architectId,
        CreateAmbassadorInviteRequest request,
        CancellationToken ct = default)
    {
        var campus = await _context.Campuses
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.CampusId, ct)
            ?? throw new KeyNotFoundException($"Campus '{request.CampusId}' not found.");

        // Generate cryptographically random 64-byte URL-safe token (REQ-A1)
        var tokenBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(64);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-").Replace("/", "_").Replace("=", ""); // URL-safe Base64

        var invite = new Vouch.Domain.Entities.AmbassadorInvite
        {
            CampusId = campus.Id,
            IssuedByArchitectId = architectId,
            Token = token,
            IntendedEmail = request.IntendedEmail,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };

        _context.AmbassadorInvites.Add(invite);
        await _context.SaveChangesAsync(ct);

        return new AmbassadorInviteDto(
            InviteId: invite.Id,
            Token: invite.Token,
            IntendedEmail: invite.IntendedEmail,
            ExpiresAt: invite.ExpiresAt
        );
    }
}
