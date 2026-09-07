using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Api.Endpoints;

public static class WingmanEndpoints
{
    public static IEndpointRouteBuilder MapWingmanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wingman").WithTags("AI Wingman (Icebreaker Service)").RequireAuthorization();

        group.MapGet("/icebreakers/conversation/{conversationId:guid}", async (
            Guid conversationId,
            ClaimsPrincipal principal,
            IApplicationDbContext context,
            IAiWingmanService wingmanService,
            CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var conversation = await context.Conversations
                .Include(c => c.UserA)
                .Include(c => c.UserB)
                .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

            if (conversation == null) return Results.NotFound(new { error = "Conversation not found." });

            if (conversation.UserAId != userId && conversation.UserBId != userId)
            {
                return Results.Forbid();
            }

            var otherUser = conversation.UserAId == userId ? conversation.UserB : conversation.UserA;
            var currentUser = conversation.UserAId == userId ? conversation.UserA : conversation.UserB;

            var suggestions = await wingmanService.GenerateIcebreakersAsync(currentUser, otherUser, ct);
            return Results.Ok(suggestions);
        })
        .WithName("GetIcebreakers")
        .WithSummary("Generate 3 considered icebreaker questions grounded in shared interests");

        return app;
    }
}
