using System.Security.Claims;
using Vouch.Application.Features.Vouching;

namespace Vouch.Api.Endpoints;

public static class VouchEndpoints
{
    public static IEndpointRouteBuilder MapVouchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vouches").WithTags("Vouching & Trust Graph").RequireAuthorization();

        group.MapPost("/", async (SubmitVouchRequest request, ClaimsPrincipal principal, ITrustService trustService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var voucherId)) return Results.Unauthorized();

            try
            {
                var vouch = await trustService.SubmitVouchAsync(voucherId, request, ct);
                return Results.Created($"/api/vouches/{vouch.Id}", vouch);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
        .WithName("SubmitVouch")
        .WithSummary("Vouch for a peer confirming character traits");

        group.MapGet("/me", async (ClaimsPrincipal principal, ITrustService trustService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                var summary = await trustService.GetUserTrustSummaryAsync(userId, ct);
                return Results.Ok(summary);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .WithName("GetMyTrustSummary")
        .WithSummary("Get current user trust score, vouches received, and incubation status");

        group.MapGet("/user/{userId:guid}", async (Guid userId, ITrustService trustService, CancellationToken ct) =>
        {
            try
            {
                var summary = await trustService.GetUserTrustSummaryAsync(userId, ct);
                return Results.Ok(summary);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .WithName("GetUserTrustSummary")
        .WithSummary("Get public character card and verified vouches for a user");

        return app;
    }
}
