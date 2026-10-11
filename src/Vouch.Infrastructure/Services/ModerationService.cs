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

public partial class ModerationService : IModerationService
{
    private readonly ApplicationDbContext _context;
    private readonly IEmailService _emailService;
    private readonly string? _architectEmail;
    private readonly ILogger<ModerationService> _logger;
    private readonly IRealtimeConnections? _realtime;
    private readonly TimeProvider _clock;

    public ModerationService(
        ApplicationDbContext context,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<ModerationService> logger, IRealtimeConnections? realtime = null, TimeProvider? clock = null)
    {
        _context = context;
        _emailService = emailService;
        _architectEmail = configuration["Smtp:ArchitectEmail"];
        _logger = logger;
        _realtime = realtime;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<bool> BlockUserAsync(Guid blockerId, BlockUserRequest request, CancellationToken ct = default)
    {
        await ResourceAccess.RequirePeerAsync(_context, blockerId, request.TargetUserId, safetyAction: true, ct: ct);
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

        var conversationIds = await _context.Conversations.Where(c => c.UserAId == blockerId && c.UserBId == request.TargetUserId ||
            c.UserBId == blockerId && c.UserAId == request.TargetUserId).Select(c => c.Id).ToListAsync(ct);
        foreach (var conversationId in conversationIds) _realtime?.RevokeConversation(conversationId);

        return true;
    }

    public async Task<ArchitectDashboardDto> GetArchitectDashboardAsync(Guid campusId, CancellationToken ct = default, Guid? architectId = null)
    {
        await ResourceAccess.RequireArchitectAsync(_context, architectId ?? Guid.Empty, campusId, ct);
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

        var pendingCount = await ModerationMetrics.ScopedReports(_context, campusId)
            .CountAsync(r => r.Status == ReportStatus.Pending, ct);
        var pendingReports = await _context.Reports
            .AsNoTracking()
            .Include(r => r.Reporter)
            .Include(r => r.ReportedUser)
            .Where(r => r.Status == ReportStatus.Pending && r.ReportedUser.CampusId == campusId && r.Reporter.CampusId == campusId)
            .OrderByDescending(r => r.SeverityScore)
            .ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
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
            .Where(v => v.TargetUser.CampusId == campusId && v.VoucherUser.CampusId == campusId
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
            PendingReportsCount: pendingCount,
            CriticalReports: pendingReports.Where(r => r.SeverityScore >= 4).ToList(),
            TrustScoreAnomalies: allAnomalies
        );
    }

    public async Task ApproveAmbassadorAsync(Guid architectId, Guid userId, CancellationToken ct = default)
    {
        var architect = await _context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == architectId, ct);
        var user = await _context.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        await ResourceAccess.RequireArchitectAsync(_context, architectId, user.CampusId, ct);
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
        await ResourceAccess.RequireArchitectAsync(_context, architectId, request.CampusId, ct);
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
