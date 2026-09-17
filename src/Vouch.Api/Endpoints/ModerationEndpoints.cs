using System.Security.Claims;
using Vouch.Application.Features.Moderation;

namespace Vouch.Api.Endpoints;

public static class ModerationEndpoints
{
    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder app)
    {
        var moderationGroup = app.MapGroup("/api/moderation").WithTags("Safety & Moderation").RequireAuthorization();

        moderationGroup.MapPost("/report", async (CreateReportRequest request, ClaimsPrincipal principal, IModerationService moderationService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var reporterId)) return Results.Unauthorized();

            var report = await moderationService.SubmitReportAsync(reporterId, request, ct);
            return Results.Created($"/api/moderation/report/{report.Id}", report);
        })
        .WithName("SubmitReport")
        .WithSummary("Report a profile or message (soft-hides reported user)");

        moderationGroup.MapPost("/block", async (BlockUserRequest request, ClaimsPrincipal principal, IModerationService moderationService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var blockerId)) return Results.Unauthorized();

            await moderationService.BlockUserAsync(blockerId, request, ct);
            return Results.Ok(new { message = "User blocked successfully." });
        })
        .WithName("BlockUser")
        .WithSummary("Block user from interactions, profiles, and matches");

        var architectGroup = app.MapGroup("/api/architect").WithTags("The Architect (Admin)").RequireAuthorization(p => p.RequireRole("Architect"));

        architectGroup.MapGet("/dashboard/{campusId:guid}", async (Guid campusId, IModerationService moderationService, CancellationToken ct) =>
        {
            var dashboard = await moderationService.GetArchitectDashboardAsync(campusId, ct);
            return Results.Ok(dashboard);
        })
        .WithName("GetArchitectDashboard")
        .WithSummary("View Launch Readiness Score, pending reports, and trust anomalies");

        architectGroup.MapPost("/reports/{id:guid}/resolve", async (Guid id, ResolveReportBody body, ClaimsPrincipal principal, IModerationService moderationService, CancellationToken ct) =>
        {
            var architectIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(architectIdStr, out var architectId)) return Results.Unauthorized();

            await moderationService.ResolveReportAsync(architectId, id, body.Uphold, body.Notes, ct);
            return Results.Ok(new { message = body.Uphold ? "Report upheld." : "Report dismissed." });
        })
        .WithName("ResolveReport")
        .WithSummary("Uphold or dismiss a moderation report (3 upheld triggers 90-day auto-suspension)");

        // REQ-A1: Architect issues single-use ambassador invite tokens
        moderationGroup.MapPost("/invites", async (
            CreateAmbassadorInviteRequest request,
            ClaimsPrincipal principal,
            IModerationService moderationService,
            CancellationToken ct) =>
        {
            var architectIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(architectIdStr, out var architectId)) return Results.Unauthorized();

            var invite = await moderationService.CreateAmbassadorInviteAsync(architectId, request, ct);
            return Results.Created($"/api/moderation/invites/{invite.InviteId}", invite);
        })
        .RequireAuthorization("ArchitectOnly")
        .WithName("CreateAmbassadorInvite")
        .WithSummary("Issue a single-use ambassador invite token (Architect only, REQ-A1)");

        return app;
    }
}
