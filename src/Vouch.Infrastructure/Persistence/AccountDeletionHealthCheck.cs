using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Persistence;

public sealed class AccountDeletionHealthCheck(IServiceScopeFactory scopes, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); var now = clock.GetUtcNow();
            if (await db.AccountDeletions.AnyAsync(d => d.DeadlineAt <= now && (d.CompletedAt == null || d.BackupSubjectId != null), ct) ||
                await db.Users.AnyAsync(u => u.DeletionRequestedAt <= now.AddDays(-30) &&
                    !db.AccountDeletions.Any(d => d.UserId == u.Id && d.CompletedAt == null), ct))
                return HealthCheckResult.Unhealthy("Account deletion deadline is overdue.");
            if (await db.AccountDeletions.AnyAsync(d => d.CompletedAt == null && d.RequestedAt <= now.AddDays(-1), ct) ||
                await db.Users.AnyAsync(u => (u.Status == AccountStatus.DeletionRequested || u.DeletionRequestedAt != null) &&
                    !db.AccountDeletions.Any(d => d.UserId == u.Id && d.CompletedAt == null), ct))
                return HealthCheckResult.Degraded("Account deletion queue requires attention before its deadline.");
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return HealthCheckResult.Unhealthy("Account deletion storage requires attention."); }
    }
}
