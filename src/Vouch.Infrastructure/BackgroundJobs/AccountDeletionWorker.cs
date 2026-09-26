using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.BackgroundJobs;

/// <summary>
/// Step 17 — NFR-6: Hard-deletes accounts that have been in DeletionRequested
/// status for more than 30 days.
///
/// Deletion order respects FK constraints (all foreign keys use Restrict):
///   Messages → Conversations → VouchRecords → Reports → Blocks → DailyMatches → User
///
/// Runs daily at 01:05 UTC (offset from ConversationInactivityWorker at 00:05
/// so they don't compete for DB connections).
///
/// Uses ExecuteDeleteAsync (EF Core 7+ bulk delete) for efficiency — avoids
/// loading entire entity graphs into memory.
/// </summary>
public sealed class AccountDeletionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountDeletionWorker> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = GetInitialDelay();

    public AccountDeletionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<AccountDeletionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AccountDeletionWorker starting. First run in {Delay}.",
            InitialDelay);

        await Task.Delay(InitialDelay, stoppingToken);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            await RunDeletionsAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDeletionsAsync(CancellationToken ct)
    {
        _logger.LogInformation("AccountDeletionWorker: starting account deletion pass at {UtcNow}.", DateTimeOffset.UtcNow);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);

            // Find user IDs eligible for hard deletion
            var userIds = await db.Users
                .Where(u => u.Status == AccountStatus.DeletionRequested
                         && u.DeletionRequestedAt != null
                         && u.DeletionRequestedAt <= cutoff)
                .Select(u => u.Id)
                .ToListAsync(ct);

            if (userIds.Count == 0)
            {
                _logger.LogInformation("AccountDeletionWorker: no accounts due for deletion.");
                return;
            }

            _logger.LogInformation(
                "AccountDeletionWorker: {Count} account(s) scheduled for hard deletion.",
                userIds.Count);

            // Process each user individually so one failure doesn't block others
            foreach (var userId in userIds)
            {
                await DeleteUserDataAsync(db, userId, ct);
            }

            _logger.LogInformation("AccountDeletionWorker: deletion pass complete.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AccountDeletionWorker: cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AccountDeletionWorker: unhandled error during deletion pass.");
        }
    }

    private async Task DeleteUserDataAsync(ApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("AccountDeletionWorker: hard-deleting user {UserId}.", userId);

            // 1. Messages sent by this user (FK: Message.SenderId → User, Restrict)
            await db.Messages
                .Where(m => m.SenderId == userId)
                .ExecuteDeleteAsync(ct);

            // 2. Conversations the user participated in (cascade deletes their Messages via EF config)
            //    Must delete messages in those conversations first to avoid FK violation
            var convIds = await db.Conversations
                .Where(c => c.UserAId == userId || c.UserBId == userId)
                .Select(c => c.Id)
                .ToListAsync(ct);

            if (convIds.Count > 0)
            {
                // Delete remaining messages in those conversations from the other participant
                await db.Messages
                    .Where(m => convIds.Contains(m.ConversationId))
                    .ExecuteDeleteAsync(ct);

                // Now delete the conversations themselves
                await db.Conversations
                    .Where(c => c.UserAId == userId || c.UserBId == userId)
                    .ExecuteDeleteAsync(ct);
            }

            // 3. Vouch records (given and received — both use Restrict)
            await db.Vouches
                .Where(v => v.VoucherUserId == userId || v.TargetUserId == userId)
                .ExecuteDeleteAsync(ct);

            // 4. Reports (submitted by or against this user — both use Restrict)
            await db.Reports
                .Where(r => r.ReporterId == userId || r.ReportedUserId == userId)
                .ExecuteDeleteAsync(ct);

            // 5. Blocks (BlockedUsers and BlockedByUsers use Cascade, but we're
            //    deleting the user anyway so explicit delete is cleaner / faster)
            await db.Blocks
                .Where(b => b.BlockerId == userId || b.BlockedUserId == userId)
                .ExecuteDeleteAsync(ct);

            // 6. Daily matches
            await db.Matches
                .Where(m => m.UserAId == userId || m.UserBId == userId)
                .ExecuteDeleteAsync(ct);

            // 7. Ambassador invites created by/for this user (if any)
            await db.AmbassadorInvites
                .Where(i => i.IntendedEmail != null && db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => u.Email)
                    .Contains(i.IntendedEmail))
                .ExecuteDeleteAsync(ct);

            // 8. Finally: the user record itself
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation("AccountDeletionWorker: user {UserId} hard-deleted successfully.", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AccountDeletionWorker: failed to delete user {UserId}. Will retry next run.", userId);
            // Don't rethrow — process remaining users
        }
    }

    /// <summary>
    /// Targets 01:05 UTC — offset by 1 hour from ConversationInactivityWorker (00:05 UTC)
    /// so both workers don't hammer the DB simultaneously.
    /// </summary>
    private static TimeSpan GetInitialDelay()
    {
        var now = DateTimeOffset.UtcNow;
        var todayTarget = new DateTimeOffset(now.Year, now.Month, now.Day, 1, 5, 0, TimeSpan.Zero);
        var nextTarget = todayTarget <= now ? todayTarget.AddDays(1) : todayTarget;
        var delay = nextTarget - now;

        return delay < TimeSpan.FromSeconds(30) ? delay.Add(TimeSpan.FromDays(1)) : delay;
    }
}
