using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouch.Application.Features.Messaging;

namespace Vouch.Infrastructure.BackgroundJobs;

/// <summary>
/// Step 16 — REQ-23: Runs nightly to:
///   1. Send gentle re-engagement nudges to conversations silent for 21 days
///   2. Archive conversations silent for 30 days
///
/// Uses PeriodicTimer (24h interval) so it doesn't drift like Thread.Sleep.
/// IMessagingService is scoped, so a new DI scope is created per tick.
/// </summary>
public sealed class ConversationInactivityWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationInactivityWorker> _logger;

    // Run once per day; first tick fires after the initial delay
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    // Delay first run to just after midnight UTC so logs cluster at a predictable time
    // and the app has time to fully start before the first DB hit.
    private static readonly TimeSpan InitialDelay = GetInitialDelay();

    public ConversationInactivityWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationInactivityWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until just past midnight UTC before the first run
        _logger.LogInformation(
            "ConversationInactivityWorker starting. First run in {Delay}.",
            InitialDelay);

        await Task.Delay(InitialDelay, stoppingToken);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            await RunChecksAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunChecksAsync(CancellationToken ct)
    {
        _logger.LogInformation("ConversationInactivityWorker: running inactivity checks at {UtcNow}.", DateTimeOffset.UtcNow);

        try
        {
            // IMessagingService is scoped — must create a scope per invocation
            await using var scope = _scopeFactory.CreateAsyncScope();
            var messagingService = scope.ServiceProvider.GetRequiredService<IMessagingService>();

            await messagingService.ProcessInactivityChecksAsync(ct);

            _logger.LogInformation("ConversationInactivityWorker: inactivity checks completed.");
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown — not an error
            _logger.LogInformation("ConversationInactivityWorker: cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            // Log and continue — don't crash the host; next tick will retry
            _logger.LogError(ex, "ConversationInactivityWorker: unhandled error during inactivity checks.");
        }
    }

    /// <summary>
    /// Calculates the delay until the next 00:05 UTC so the first run
    /// happens at a predictable time regardless of when the app starts.
    /// </summary>
    private static TimeSpan GetInitialDelay()
    {
        var now = DateTimeOffset.UtcNow;
        // Target: next 00:05 UTC
        var todayTarget = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 5, 0, TimeSpan.Zero);
        var nextTarget = todayTarget <= now ? todayTarget.AddDays(1) : todayTarget;
        var delay = nextTarget - now;

        // Safety floor: always wait at least 30 seconds (avoids instant fire on midnight deploy)
        return delay < TimeSpan.FromSeconds(30) ? delay.Add(TimeSpan.FromDays(1)) : delay;
    }
}
