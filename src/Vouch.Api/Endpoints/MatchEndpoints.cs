using System.Security.Claims;
using Vouch.Application.Features.Matching;

namespace Vouch.Api.Endpoints;

public static class MatchEndpoints
{
    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/matches").WithTags("Matchmaking & Reflections").RequireAuthorization();

        group.MapGet("/today", async (ClaimsPrincipal principal, IMatchService matchService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                var response = await matchService.GetTodayConnectionAsync(userId, ct);
                return Results.Ok(response);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .WithName("GetTodayConnection")
        .WithSummary("Get 1 daily match OR No Match Today with Daily Reflection");

        group.MapPost("/{matchId:guid}/respond", async (Guid matchId, RespondMatchRequest request, ClaimsPrincipal principal, IMatchService matchService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                var acceptedBoth = await matchService.RespondToMatchAsync(userId, matchId, request.Accept, ct);
                return Results.Ok(new
                {
                    matchId,
                    accepted = request.Accept,
                    isMutualMatch = acceptedBoth,
                    message = acceptedBoth ? "Mutual connection established! Letter conversation unlocked." : "Response recorded."
                });
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
        .WithName("RespondToMatch")
        .WithSummary("Accept or decline today's connection");

        group.MapPost("/campus/{campusId:guid}/generate-daily", async (Guid campusId, IMatchService matchService, CancellationToken ct) =>
        {
            await matchService.GenerateDailyMatchesForCampusAsync(campusId, ct);
            return Results.Ok(new { message = "Daily matches generated successfully for campus." });
        })
        .RequireRole("Architect")
        .WithName("GenerateDailyMatches")
        .WithSummary("Architect trigger to compute 24-hour cycle matches");

        return app;
    }

    private static RouteHandlerBuilder RequireRole(this RouteHandlerBuilder builder, string role)
    {
        return builder.RequireAuthorization(policy => policy.RequireRole(role));
    }
}
