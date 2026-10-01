using System.Security.Claims;
using Vouch.Application.Features.Auth;

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
