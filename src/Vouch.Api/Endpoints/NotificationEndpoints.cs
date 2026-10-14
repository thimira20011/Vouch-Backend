using System.Security.Claims;
using Vouch.Application.Features.Notifications;

namespace Vouch.Api.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization().RequireRateLimiting("auth_standard");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/", async (ClaimsPrincipal principal, INotificationService service, CancellationToken ct,
            int page = 1, int pageSize = 20, bool unreadOnly = false) =>
            Results.Ok(await service.GetAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), page, pageSize, unreadOnly, ct)))
            .WithName("GetNotifications");
        group.MapPost("/{id:guid}/read", async (Guid id, ClaimsPrincipal principal, INotificationService service, CancellationToken ct) =>
        {
            await service.ReadAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), id, ct);
            return Results.NoContent();
        }).WithName("ReadNotification");
        group.MapGet("/delivery", async (ClaimsPrincipal principal, INotificationService service, CancellationToken ct) =>
            Results.Ok(await service.GetDeliveryStatusAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), ct)))
            .RequireAuthorization("ArchitectOnly").WithName("GetNotificationDeliveryStatus");
        return app;
    }
}
