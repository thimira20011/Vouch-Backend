using System.Security.Claims;
using Vouch.Application.Features.Messaging;

namespace Vouch.Api.Endpoints;

public static class MessagingEndpoints
{
    public static IEndpointRouteBuilder MapMessagingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations").WithTags("Letter Messaging & Slow-Burn").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var conversations = await messagingService.GetUserConversationsAsync(userId, ct);
            return Results.Ok(conversations);
        })
        .WithName("GetUserConversations")
        .WithSummary("Get active and paused conversations");

        group.MapGet("/{id:guid}/messages", async (Guid id, ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                var messages = await messagingService.GetConversationMessagesAsync(userId, id, ct);
                return Results.Ok(messages);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .WithName("GetConversationMessages")
        .WithSummary("Get full letter message history");

        group.MapPost("/{id:guid}/messages", async (Guid id, SendMessageRequest body, ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                var message = await messagingService.SendMessageAsync(userId, id, body, ct);
                return Results.Created($"/api/conversations/{id}/messages/{message.Id}", message);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .WithName("SendMessage")
        .WithSummary("Send letter-style message (evaluates Slow-Burn progression)");

        group.MapPost("/{id:guid}/pause", async (Guid id, ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                await messagingService.PauseConversationAsync(userId, id, ct);
                return Results.Ok(new { message = "Conversation paused. No rush, take your time." });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("PauseConversation")
        .WithSummary("Pause chat with gentle non-accusatory notice (REQ-21)");

        group.MapPost("/{id:guid}/resume", async (Guid id, ClaimsPrincipal principal, IMessagingService messagingService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                await messagingService.ResumeConversationAsync(userId, id, ct);
                return Results.Ok(new { message = "Conversation resumed." });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        })
        .WithName("ResumeConversation")
        .WithSummary("Resume previously paused chat");

        return app;
    }
}
