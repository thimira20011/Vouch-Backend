using System.Security.Claims;
using Vouch.Api.Middleware;
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

            var vouch = await trustService.SubmitVouchAsync(voucherId, request, ct);
            return Results.Created($"/api/vouches/{vouch.Id}", vouch);
        })
        .AddEndpointFilter<ValidationFilter<SubmitVouchRequest>>()
        .WithName("SubmitVouch")
        .WithSummary("Vouch for a peer confirming character traits");

        group.MapGet("/me", async (ClaimsPrincipal principal, ITrustService trustService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            var summary = await trustService.GetUserTrustSummaryAsync(userId, ct);
            return Results.Ok(summary);
        })
        .WithName("GetMyTrustSummary")
        .WithSummary("Get current user trust score, vouches received, and incubation status");

        group.MapGet("/user/{userId:guid}", async (Guid userId, ITrustService trustService, CancellationToken ct) =>
        {
            var summary = await trustService.GetUserTrustSummaryAsync(userId, ct);
            return Results.Ok(summary);
        })
        .WithName("GetUserTrustSummary")
        .WithSummary("Get public character card and verified vouches for a user");

        return app;
    }
}
