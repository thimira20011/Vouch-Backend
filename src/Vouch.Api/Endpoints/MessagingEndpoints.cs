using System.Security.Claims;
using Vouch.Api.Middleware;
using Vouch.Application.Features.Messaging;

namespace Vouch.Api.Endpoints;

public static class MessagingEndpoints
{
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations").WithTags("Letter Messaging & Slow-Burn").RequireAuthorization();

        // Step 14: page + pageSize as query params with defaults (20 conversations per page)
        group.MapGet("/", async (
            ClaimsPrincipal principal,
            IMessagingService messagingService,
            int page = 1,
            int pageSize = 20,
            CancellationToken ct = default) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var result = await messagingService.GetUserConversationsAsync(userId, page, pageSize, ct);
            return Results.Ok(result);
        })
        .WithName("GetUserConversations")
        .WithSummary("Get active and paused conversations (paginated)");

        // Step 14: page + pageSize as query params with defaults (50 messages per page)
        group.MapGet("/{id:guid}/messages", async (
            Guid id,
            ClaimsPrincipal principal,
            IMessagingService messagingService,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var result = await messagingService.GetConversationMessagesAsync(userId, id, page, pageSize, ct);
            return Results.Ok(result);
        })
        .WithName("GetConversationMessages")
        .WithSummary("Get full letter message history (paginated)");

        group.MapPost("/{id:guid}/messages", async (Guid id, SendMessageRequest body, ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var message = await messagingService.SendMessageAsync(userId, id, body, ct);
            return Results.Created($"/api/conversations/{id}/messages/{message.Id}", message);
        })
        .AddEndpointFilter<ValidationFilter<SendMessageRequest>>()
        .WithName("SendMessage")
        .WithSummary("Send letter-style message (evaluates Slow-Burn progression)");

        group.MapPost("/{id:guid}/pause", async (Guid id, ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            await messagingService.PauseConversationAsync(userId, id, ct);
            return Results.Ok(new { message = "Conversation paused. No rush, take your time." });
        })
        .WithName("PauseConversation")
        .WithSummary("Pause chat with gentle non-accusatory notice (REQ-21)");

        group.MapPost("/{id:guid}/resume", async (Guid id, ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            await messagingService.ResumeConversationAsync(userId, id, ct);
            return Results.Ok(new { message = "Conversation resumed." });
        })
        .WithName("ResumeConversation")
        .WithSummary("Resume previously paused chat");

        return app;
    }
}
