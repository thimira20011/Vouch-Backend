using System.Security.Claims;
using Vouch.Application.Features.Auth;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Infrastructure.Security;
using Vouch.Domain.Enums;

namespace Vouch.Api.Endpoints;

/// <summary>
/// Step 21 — REQ-17: Photo upload endpoint.
/// Accepts multipart/form-data with a single 'photo' file field (JPEG, PNG, WebP, max 5 MB).
/// Stores the original and produces a Gaussian-blurred abstract variant for the slow-burn reveal.
/// </summary>
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profile").WithTags("Profile");
        app.MapGet("/photos/{variant}/{file}", async (string variant, string file, ClaimsPrincipal principal,
            IApplicationDbContext db, IConfiguration configuration, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (variant is not ("original" or "abstract") || file.Length != 36 || !file.EndsWith(".jpg", StringComparison.Ordinal) ||
                !Guid.TryParseExact(file[..32], "N", out var ownerId)) return Results.NotFound();
            var viewerId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ownerId, ct);
            if (owner is null) return Results.NotFound();
            var expectedUrl = $"/photos/{variant}/{file}";
            if ((variant == "original" ? owner.OriginalPhotoUrl : owner.OilPaintingAbstractPhotoUrl) != expectedUrl) return Results.NotFound();
            if (viewerId == ownerId)
            {
                SessionService.EnsureUsable(owner);
                if (owner.EmailVerifiedAt is null || owner.OnboardingCompletedAt is null) return Results.Forbid();
            }
            else
            {
                await ResourceAccess.RequirePeerAsync(db, viewerId, ownerId, ct: ct);
                if (variant == "original" && !await ResourceAccess.VisibleConversations(db, viewerId).AnyAsync(c =>
                    (c.UserAId == ownerId || c.UserBId == ownerId) && c.CurrentClarityStage == RevealClarityStage.Full100, ct)) return Results.Forbid();
            }
            var root = configuration["PhotoStorage:BasePath"];
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "photos");
            var path = Path.GetFullPath(Path.Combine(root, variant, file));
            return File.Exists(path) ? Results.File(path, "image/jpeg", enableRangeProcessing: false) : Results.NotFound();
        }).RequireAuthorization().WithName("GetAuthorizedPhoto");

        // PUT /api/profile/photo
        group.MapPut("/photo", async (HttpRequest httpRequest, ClaimsPrincipal principal, IAuthService authService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId))
                return Results.Unauthorized();

            if (!httpRequest.HasFormContentType)
                return Results.BadRequest(new { error = "Request must be multipart/form-data." });

            var form = await httpRequest.ReadFormAsync(ct);
            var file = form.Files.GetFile("photo");

            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No photo file provided. Include a 'photo' field in the form." });

            await using var stream = file.OpenReadStream();
            var (originalUrl, abstractUrl) = await authService.UploadPhotoAsync(
                userId, stream, file.ContentType, ct);

            return Results.Ok(new
            {
                message = "Photo uploaded successfully.",
                originalUrl,
                abstractUrl
            });
        })
        .RequireAuthorization()
        .WithName("UploadPhoto")
        .WithSummary("Upload profile photo (REQ-17). Generates blurred abstract variant automatically.")
        .Accepts<IFormFile>("multipart/form-data")
        .Produces(200)
        .Produces(400)
        .Produces(401);

        return app;
    }
}
