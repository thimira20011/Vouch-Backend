using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouch.Infrastructure.Services;

namespace Vouch.Infrastructure.BackgroundJobs;

// Start immediately, then retry durable work every minute; never wait until day 30.
public sealed class AccountDeletionWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<AccountDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AccountDeletionProcessor>().ProcessDueAsync(ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Account deletion pass failed; durable pending work will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
