using Microsoft.EntityFrameworkCore;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Persistence;

public static class ModerationMetrics
{
    public static IQueryable<Report> ScopedReports(ApplicationDbContext db, Guid campusId) => db.Reports.Where(r =>
        r.Reporter.CampusId == campusId && r.ReportedUser.CampusId == campusId);

    public static async Task RecalculateAsync(ApplicationDbContext db, Guid campusId, CancellationToken ct)
    {
        var reports = ScopedReports(db, campusId);
        await db.Users.Where(u => u.CampusId == campusId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.UpheldReportsCount, u => reports.Count(r => r.ReportedUserId == u.Id && r.Status == ReportStatus.Upheld))
            .SetProperty(u => u.IsSoftHiddenFromMatchmaking, u => u.Status == AccountStatus.Suspended || u.Status == AccountStatus.DeletionRequested ||
                reports.Any(r => r.ReportedUserId == u.Id && r.Status == ReportStatus.Pending)), ct);
        var tracked = db.ChangeTracker.Entries<User>().Where(e => e.State != EntityState.Deleted && e.Entity.CampusId == campusId).ToArray();
        var ids = tracked.Select(e => e.Entity.Id).ToArray();
        var values = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.UpheldReportsCount, u.IsSoftHiddenFromMatchmaking }).ToListAsync(ct);
        foreach (var entry in tracked)
        {
            var value = values.SingleOrDefault(u => u.Id == entry.Entity.Id);
            if (value is null) continue;
            entry.Entity.UpheldReportsCount = value.UpheldReportsCount;
            entry.Entity.IsSoftHiddenFromMatchmaking = value.IsSoftHiddenFromMatchmaking;
        }
    }
}
