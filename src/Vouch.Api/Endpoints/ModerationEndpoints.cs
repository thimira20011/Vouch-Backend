using System.Security.Claims;
using Vouch.Api.Middleware;
using Vouch.Application.Features.Moderation;
using Vouch.Domain.Enums;

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
        .AddEndpointFilter<ValidationFilter<CreateReportRequest>>()
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
        architectGroup.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        architectGroup.MapGet("/campuses/{campusId:guid}/reports", async (Guid campusId, ClaimsPrincipal principal,
            IModerationService service, CancellationToken ct, ReportStatus status = ReportStatus.Pending, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.GetReportsAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), campusId, status, page, pageSize, ct)))
            .WithName("GetCampusReports");

        architectGroup.MapGet("/reports/{id:guid}", async (Guid id, ClaimsPrincipal principal, IModerationService service, CancellationToken ct) =>
            Results.Ok(await service.GetReportAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), id, ct)))
            .WithName("GetReportEvidence");

        architectGroup.MapGet("/users/{userId:guid}/moderation", async (Guid userId, ClaimsPrincipal principal,
            IModerationService service, CancellationToken ct, int page = 1, int pageSize = 20) =>
            Results.Ok(await service.GetAccountReviewAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), userId, page, pageSize, ct)))
            .WithName("GetAccountModerationReview");

        foreach (var reinstate in new[] { false, true })
        {
            architectGroup.MapPost(reinstate ? "/users/{userId:guid}/reinstate" : "/users/{userId:guid}/suspend",
                async (Guid userId, AccountModerationBody body, ClaimsPrincipal principal, IModerationService service, CancellationToken ct) =>
                {
                    await service.ChangeSuspensionAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), userId, reinstate, body, ct);
                    return Results.NoContent();
                }).AddEndpointFilter<ValidationFilter<AccountModerationBody>>()
                .WithName(reinstate ? "ReinstateAccount" : "SuspendAccount");
        }

        architectGroup.MapPost("/ambassadors/{userId:guid}/approve", async (Guid userId, ClaimsPrincipal principal,
            IModerationService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var architectId)) return Results.Unauthorized();
            await service.ApproveAmbassadorAsync(architectId, userId, ct);
            return Results.Ok(new { message = "Ambassador manually approved." });
        }).WithName("ApproveAmbassador");

        architectGroup.MapGet("/dashboard/{campusId:guid}", async (Guid campusId, ClaimsPrincipal principal, IModerationService moderationService, CancellationToken ct) =>
        {
            var dashboard = await moderationService.GetArchitectDashboardAsync(campusId, ct, Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
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
        .AddEndpointFilter<ValidationFilter<ResolveReportBody>>()
        .WithName("ResolveReport")
        .WithSummary("Uphold or dismiss a report (3 upheld decisions within 90 days suspend immediately)");

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
        .AddEndpointFilter<ValidationFilter<CreateAmbassadorInviteRequest>>()
        .RequireAuthorization("ArchitectOnly")
        .WithName("CreateAmbassadorInvite")
        .WithSummary("Issue a single-use ambassador invite token (Architect only, REQ-A1)");

        return app;
    }
}
