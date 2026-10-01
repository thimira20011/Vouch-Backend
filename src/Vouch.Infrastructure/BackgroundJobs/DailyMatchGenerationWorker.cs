using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Matching;

namespace Vouch.Infrastructure.BackgroundJobs;

/// <summary>
/// Step 23 — Optimization: Moves daily match generation off the HTTP request path.
/// Runs once per day at 00:05 UTC for every active campus.
///
/// Previously, match generation was triggered on-demand by the Architect endpoint
/// POST /api/matches/campus/{id}/generate-daily. That endpoint still works as a
/// manual override (useful for testing or emergency re-generation).
///
/// Uses PeriodicTimer with an initial delay calculated to the next 00:05 UTC window
/// so ticks are predictable and logs cluster at midnight rather than scattered
/// throughout the day based on app restart time.
/// </summary>
public sealed class DailyMatchGenerationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyMatchGenerationWorker> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = GetInitialDelay();

    public DailyMatchGenerationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DailyMatchGenerationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DailyMatchGenerationWorker started. First run in {Delay:hh\\:mm\\:ss} (targeting 00:05 UTC).",
            InitialDelay);

        await Task.Delay(InitialDelay, stoppingToken);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            _logger.LogInformation("DailyMatchGenerationWorker: starting daily match run at {UtcNow:u}.", DateTimeOffset.UtcNow);

            try
            {
                await GenerateMatchesForAllCampusesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailyMatchGenerationWorker: unhandled error during daily match generation.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task GenerateMatchesForAllCampusesAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var matchService = scope.ServiceProvider.GetRequiredService<IMatchService>();

        // Fetch all campuses with at least one active user — avoids running the
        // full pairing algorithm for empty campuses.
        var activeCampusIds = await context.Users
            .AsNoTracking()
            .Where(u => u.Status == Vouch.Domain.Enums.AccountStatus.Active)
            .Select(u => u.CampusId)
            .Distinct()
            .ToListAsync(ct);

        _logger.LogInformation("DailyMatchGenerationWorker: generating matches for {Count} active campus(es).", activeCampusIds.Count);

        foreach (var campusId in activeCampusIds)
        {
            try
            {
                await matchService.GenerateDailyMatchesForCampusAsync(campusId, ct);
                _logger.LogInformation("DailyMatchGenerationWorker: matches generated for campus {CampusId}.", campusId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailyMatchGenerationWorker: failed to generate matches for campus {CampusId}.", campusId);
                // Continue to next campus — one failure should not block others
            }
        }
    }

    /// <summary>Calculates delay until the next 00:05 UTC tick.</summary>
    private static TimeSpan GetInitialDelay()
    {
        var now = DateTimeOffset.UtcNow;
        var nextRun = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 5, 0, TimeSpan.Zero);
        if (nextRun <= now)
            nextRun = nextRun.AddDays(1);
        return nextRun - now;
    }
}
