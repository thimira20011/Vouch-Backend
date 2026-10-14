using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common;
using Vouch.Application.Features.Moderation;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public partial class ModerationService
{
    private async Task<Guid> LockCampusAsync(Guid userId, CancellationToken ct)
    {
        var campus = await _context.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => (Guid?)u.CampusId).SingleOrDefaultAsync(ct)
            ?? throw new EligibilityException("Account is unavailable.");
        await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {campus} FOR UPDATE", ct);
        if (!await _context.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.CampusId == campus, ct))
            throw new EligibilityException("Account campus changed; retry the request.");
        return campus;
    }

    private async Task<User> ArchitectAsync(Guid architectId, CancellationToken ct)
    {
        var user = await _context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == architectId, ct)
            ?? throw new EligibilityException("An active Architect is required.");
        return await ResourceAccess.RequireArchitectAsync(_context, architectId, user.CampusId, ct);
    }

    private IQueryable<Report> Reports(Guid campusId) => ModerationMetrics.ScopedReports(_context, campusId);
    private static ReportDto Dto(Report r) => new(r.Id, r.ReporterId, r.Reporter.Email, r.ReportedUserId,
        r.ReportedUser.FullName, r.Category, r.Details, r.SeverityScore, r.Status, r.CreatedAt,
        r.RelatedMessageId, r.ResolvedByArchitectId, r.ResolvedAt, r.ArchitectNotes);

    private IQueryable<Message> Evidence(Report report) => _context.Messages.AsNoTracking().Where(m =>
        m.Id == report.RelatedMessageId && m.Type == MessageType.Text && m.SenderId == report.ReportedUserId &&
        (m.Conversation.UserAId == report.ReporterId && m.Conversation.UserBId == report.ReportedUserId ||
         m.Conversation.UserBId == report.ReporterId && m.Conversation.UserAId == report.ReportedUserId) &&
        m.Conversation.UserA.CampusId == m.Conversation.UserB.CampusId);

    private Task<int> WindowCountAsync(Guid campusId, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        Reports(campusId).CountAsync(r => r.ReportedUserId == userId && r.Status == ReportStatus.Upheld &&
            r.ResolvedAt >= now.AddDays(-90) && r.ResolvedAt <= now, ct);

    public async Task<ReportDto> SubmitReportAsync(Guid reporterId, CreateReportRequest request, CancellationToken ct = default)
    {
        await new CreateReportRequestValidator().ValidateAndThrowAsync(request, ct);
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(reporterId, ct);
        var target = await ResourceAccess.RequirePeerAsync(_context, reporterId, request.ReportedUserId, safetyAction: true, ct: ct);
        var reporter = await _context.Users.AsNoTracking().SingleAsync(u => u.Id == reporterId, ct);
        var report = new Report
        {
            ReporterId = reporterId, ReportedUserId = target.Id, RelatedMessageId = request.MessageId,
            Category = request.Category, Details = request.Details, CreatedAt = _clock.GetUtcNow(),
            SeverityScore = request.Category switch
            {
                ReportCategory.Harassment or ReportCategory.Impersonation => 5,
                ReportCategory.InappropriateContent => 4, ReportCategory.Spam => 3, _ => 2
            }
        };
        if (request.MessageId.HasValue && !await Evidence(report).AnyAsync(ct))
            throw new EligibilityException("Reported message is unavailable.");
        _context.Reports.Add(report);
        await _notifications.QueueReportAsync(report, reporter.CampusId, ct);
        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        _logger.LogInformation("Report submitted. ReportId={ReportId} Category={Category} Severity={Severity}", report.Id, report.Category, report.SeverityScore);
        // The no-tracking users supply the response without attaching stale user entities.
        return new(report.Id, reporter.Id, reporter.Email, target.Id, target.FullName, report.Category,
            report.Details, report.SeverityScore, report.Status, report.CreatedAt, report.RelatedMessageId);
    }

    public async Task<bool> ResolveReportAsync(Guid architectId, Guid reportId, bool uphold, string? notes, CancellationToken ct = default)
    {
        await new ResolveReportBodyValidator().ValidateAndThrowAsync(new(uphold, notes), ct);
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        var campus = await LockCampusAsync(architectId, ct);
        await ResourceAccess.RequireArchitectAsync(_context, architectId, campus, ct);
        var report = await Reports(campus).SingleOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new EligibilityException("Report is unavailable for this campus.");
        await _context.Entry(report).ReloadAsync(ct);
        var status = uphold ? ReportStatus.Upheld : ReportStatus.Dismissed;
        if (report.Status != ReportStatus.Pending)
        {
            if (report.Status != status || report.ArchitectNotes != notes)
                throw new InvalidOperationException("This report already has a different completed decision.");
            await transaction.CommitAsync(ct);
            return true;
        }
        if (uphold && report.RelatedMessageId.HasValue && !await Evidence(report).AnyAsync(ct))
            throw new EligibilityException("Reported message is unavailable.");
        var now = _clock.GetUtcNow();
        report.Status = status; report.ArchitectNotes = notes;
        report.ResolvedAt = now; report.ResolvedByArchitectId = architectId;
        await _context.SaveChangesAsync(ct); // The decision must be included in the database count below.
        var subject = await _context.Users.SingleAsync(u => u.Id == report.ReportedUserId, ct);
        await _context.Entry(subject).ReloadAsync(ct);
        var previous = subject.Status;
        var suspend = uphold && previous is AccountStatus.Active or AccountStatus.InIncubation &&
            await WindowCountAsync(campus, subject.Id, now, ct) >= 3;
        if (suspend)
        {
            subject.StatusBeforeSuspension = previous; subject.SuspendedAt = now;
            subject.Status = AccountStatus.Suspended;
        }
        AddAudit(campus, architectId, subject, report.Id,
            uphold ? ModerationAction.ReportUpheld : ModerationAction.ReportDismissed, previous,
            notes ?? (uphold ? "Report upheld." : "Report dismissed."), now);
        if (suspend) AddAudit(campus, architectId, subject, report.Id, ModerationAction.AutomaticallySuspended,
            previous, "Three upheld decisions within the rolling 90-day resolution window.", now);
        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private void AddAudit(Guid campus, Guid actor, User subject, Guid? reportId, ModerationAction action,
        AccountStatus previous, string reason, DateTimeOffset now) => _context.ModerationAudits.Add(new()
        {
            CampusId = campus, ArchitectId = actor, SubjectUserId = subject.Id, ReportId = reportId,
            Action = action, PreviousStatus = previous, ResultingStatus = subject.Status, Reason = reason, CreatedAt = now
        });

    private static void ValidatePage(int page, int size)
    {
        if (page is < 1 or > 10000 || size is < 1 or > 50)
            throw new ArgumentException("Page must be 1–10000 and pageSize must be 1–50.");
    }

    public async Task<ReportPage> GetReportsAsync(Guid architectId, Guid campusId, ReportStatus status = ReportStatus.Pending,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        await ResourceAccess.RequireArchitectAsync(_context, architectId, campusId, ct);
        ValidatePage(page, pageSize);
        if (!Enum.IsDefined(status)) throw new ArgumentException("Report status is invalid.");
        var query = Reports(campusId).AsNoTracking().Where(r => r.Status == status);
        var count = await query.CountAsync(ct);
        var items = await query.Include(r => r.Reporter).Include(r => r.ReportedUser)
            .OrderByDescending(r => r.SeverityScore).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(items.Select(Dto).ToList(), count, page, pageSize);
    }

    public async Task<ReportEvidenceDto> GetReportAsync(Guid architectId, Guid reportId, CancellationToken ct = default)
    {
        var architect = await ArchitectAsync(architectId, ct);
        var report = await Reports(architect.CampusId).AsNoTracking().Include(r => r.Reporter).Include(r => r.ReportedUser)
            .SingleOrDefaultAsync(r => r.Id == reportId, ct) ?? throw new EligibilityException("Report is unavailable for this campus.");
        var body = report.RelatedMessageId.HasValue ? await Evidence(report).Select(m => m.Body).SingleOrDefaultAsync(ct) : null;
        return new(Dto(report), body is not null, body);
    }

    public async Task<AccountModerationReviewDto> GetAccountReviewAsync(Guid architectId, Guid userId,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var architect = await ArchitectAsync(architectId, ct);
        ValidatePage(page, pageSize);
        var subject = await _context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.CampusId == architect.CampusId, ct)
            ?? throw new EligibilityException("Account is unavailable for this campus.");
        var reports = Reports(architect.CampusId).Where(r => r.ReportedUserId == userId);
        var audit = _context.ModerationAudits.AsNoTracking().Where(a => a.CampusId == architect.CampusId && a.SubjectUserId == userId &&
            (a.ReportId == null || a.Report!.Reporter.CampusId == architect.CampusId && a.Report.ReportedUser.CampusId == architect.CampusId));
        var total = await audit.CountAsync(ct);
        var items = await audit.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(a => new ModerationAuditDto(a.Id, a.ArchitectId,
                a.ReportId, a.Action, a.PreviousStatus, a.ResultingStatus, a.Reason, a.CreatedAt)).ToListAsync(ct);
        return new(subject.Id, subject.FullName, subject.Status, subject.StatusBeforeSuspension, subject.SuspendedAt,
            await reports.CountAsync(r => r.Status == ReportStatus.Upheld, ct),
            await WindowCountAsync(architect.CampusId, userId, _clock.GetUtcNow(), ct),
            await reports.CountAsync(r => r.Status == ReportStatus.Pending, ct), items, total, page, pageSize);
    }

    public async Task ChangeSuspensionAsync(Guid architectId, Guid userId, bool reinstate, AccountModerationBody body, CancellationToken ct = default)
    {
        await new AccountModerationBodyValidator().ValidateAndThrowAsync(body, ct);
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        var campus = await LockCampusAsync(architectId, ct);
        await ResourceAccess.RequireArchitectAsync(_context, architectId, campus, ct);
        var subject = await _context.Users.SingleOrDefaultAsync(u => u.Id == userId && u.CampusId == campus, ct)
            ?? throw new EligibilityException("Account is unavailable for this campus.");
        await _context.Entry(subject).ReloadAsync(ct);
        if (subject.Role == UserRole.Architect || subject.Status == AccountStatus.DeletionRequested)
            throw new InvalidOperationException("This account cannot be changed through suspension review.");
        var action = reinstate ? ModerationAction.Reinstated : ModerationAction.ManuallySuspended;
        var last = await _context.ModerationAudits.AsNoTracking().Where(a => a.CampusId == campus && a.SubjectUserId == userId &&
            (a.Action == ModerationAction.ManuallySuspended || a.Action == ModerationAction.Reinstated || a.Action == ModerationAction.AutomaticallySuspended))
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        var already = reinstate ? subject.Status is AccountStatus.Active or AccountStatus.InIncubation : subject.Status == AccountStatus.Suspended;
        if (already)
        {
            if (last?.Action != action || last.ArchitectId != architectId || last.Reason != body.Reason)
                throw new InvalidOperationException("This account already has a different moderation state.");
            await transaction.CommitAsync(ct);
            return;
        }
        var previous = subject.Status;
        var now = _clock.GetUtcNow();
        if (last is not null && now <= last.CreatedAt) now = last.CreatedAt.AddTicks(10);
        if (reinstate)
        {
            if (previous != AccountStatus.Suspended) throw new InvalidOperationException("Only suspended accounts can be reinstated.");
            subject.Status = UniversityIdentity.IsOnboarded(subject) &&
                (subject.StatusBeforeSuspension == AccountStatus.Active || subject.StatusBeforeSuspension is null && subject.IncubationCompletedAt.HasValue)
                ? AccountStatus.Active : AccountStatus.InIncubation;
            subject.StatusBeforeSuspension = null; subject.SuspendedAt = null;
        }
        else
        {
            if (previous is not (AccountStatus.Active or AccountStatus.InIncubation))
                throw new InvalidOperationException("Only available accounts can be suspended.");
            subject.StatusBeforeSuspension = previous; subject.SuspendedAt = now;
            subject.Status = AccountStatus.Suspended;
        }
        await _context.SaveChangesAsync(ct); // Includes session revocation and trust/visibility recalculation.
        AddAudit(campus, architectId, subject, null, action, previous, body.Reason, now);
        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
