using System.Security.Claims;
using Vouch.Api.Middleware;
using Vouch.Application.Features.Vouching;

namespace Vouch.Api.Endpoints;

public static class VouchEndpoints
{
    public static IEndpointRouteBuilder MapVouchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vouches").WithTags("Vouching & Trust Graph").RequireAuthorization().RequireRateLimiting("auth_standard");

        group.MapPost("/requests", async (RequestPeerVouchRequest request, ClaimsPrincipal principal, ITrustService service, CancellationToken ct) =>
        {
            var result = await service.RequestVouchAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), request, ct);
            return Results.Created("/api/vouches/requests/sent", result);
        }).AddEndpointFilter<ValidationFilter<RequestPeerVouchRequest>>().WithName("RequestPeerVouch")
          .WithSummary("Privately request a vouch from an eligible campus peer");

        group.MapGet("/requests/incoming", async (ClaimsPrincipal principal, ITrustService service, CancellationToken ct, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.GetRequestsAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), true, page, pageSize, ct)))
            .WithName("GetIncomingVouchRequests");
        group.MapGet("/requests/sent", async (ClaimsPrincipal principal, ITrustService service, CancellationToken ct, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.GetRequestsAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), false, page, pageSize, ct)))
            .WithName("GetSentVouchRequests");
        group.MapPost("/requests/{requestId:guid}/dismiss", async (Guid requestId, ClaimsPrincipal principal, ITrustService service, CancellationToken ct) =>
        {
            await service.ResolveRequestAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), requestId, true, ct);
            return Results.NoContent();
        }).WithName("DismissVouchRequest");
        group.MapPost("/requests/{requestId:guid}/cancel", async (Guid requestId, ClaimsPrincipal principal, ITrustService service, CancellationToken ct) =>
        {
            await service.ResolveRequestAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), requestId, false, ct);
            return Results.NoContent();
        }).WithName("CancelVouchRequest");

        group.MapPost("/", async (SubmitVouchRequest request, ClaimsPrincipal principal, ITrustService trustService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var voucherId)) return Results.Unauthorized();

            var vouch = await trustService.SubmitVouchAsync(voucherId, request, ct);
            return Results.Created($"/api/vouches/{vouch.Id}", vouch);
        })
        .AddEndpointFilter<ValidationFilter<SubmitVouchRequest>>()
        .WithName("SubmitVouch")
        .AddEndpointFilter<EligibleMemberFilter>()
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

        group.MapGet("/user/{userId:guid}", async (Guid userId, ClaimsPrincipal principal, ITrustService trustService, CancellationToken ct) =>
        {
            var viewerId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var summary = await trustService.GetUserTrustSummaryAsync(userId, ct, viewerId);
            return Results.Ok(summary);
        })
        .WithName("GetUserTrustSummary")
        .AddEndpointFilter<EligibleMemberFilter>()
        .WithSummary("Get public character card and verified vouches for a user");

        return app;
    }
}
