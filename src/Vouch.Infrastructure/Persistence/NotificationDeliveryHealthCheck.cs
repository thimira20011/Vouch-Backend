using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vouch.Domain.Entities;

namespace Vouch.Infrastructure.Persistence;

public sealed class NotificationDeliveryHealthCheck(IServiceScopeFactory scopes, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = clock.GetUtcNow();
            var overdue = await db.Notifications.AnyAsync(n => n.Kind == NotificationKind.HighSeverityReport && n.CancelledAt == null &&
                n.EmailCompletedAt == null && n.DeadlineAt < now, ct);
            return overdue ? HealthCheckResult.Unhealthy("High-severity notification email delivery is overdue.") : HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return HealthCheckResult.Unhealthy("Notification delivery storage requires attention."); }
    }
}
