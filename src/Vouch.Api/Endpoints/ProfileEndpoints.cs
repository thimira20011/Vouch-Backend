using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.Services;

namespace Vouch.Api.Endpoints;

public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profile").WithTags("Profile").RequireAuthorization();
        app.MapGet("/photos/{variant}/{file}", async (string variant, string file, ClaimsPrincipal principal,
            IPhotoStorageService photos, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            var stage = variant switch { "abstract" => RevealClarityStage.Abstract0, "quarter" => RevealClarityStage.Quarter25,
                "sixty" => RevealClarityStage.Sixty60, "original" => RevealClarityStage.Full100, _ => (RevealClarityStage)(-1) };
            if (!Enum.IsDefined(stage) || !PhotoUrls.TryParseFile(file, out var ownerId, out var generation)) return Results.NotFound();
            var bytes = await photos.ReadPhotoAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), ownerId, generation, stage, ct);
            return bytes is null ? Results.NotFound() : Results.File(bytes, "image/jpeg", enableRangeProcessing: false);
        }).RequireAuthorization().WithName("GetAuthorizedPhoto");

        group.MapGet("/photo", async (ClaimsPrincipal principal, IApplicationDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var id = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, ct);
            SessionService.EnsureUsable(user);
            if (!UniversityIdentity.IsOnboarded(user)) return Results.Forbid();
            return Results.Ok(Urls(user.OriginalPhotoUrl, id));
        }).WithName("GetMyPhoto");

        group.MapPut("/photo", async (HttpRequest request, ClaimsPrincipal principal, IAuthService auth, CancellationToken ct) =>
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            if (request.ContentLength > 6 * 1024 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 6 * 1024 * 1024;
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Request must be multipart/form-data." });
            IFormCollection form;
            try { form = await request.ReadFormAsync(ct); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "Invalid multipart photo or file exceeds the 5 MB limit." }); }
            catch (BadHttpRequestException ex) { return Results.StatusCode(ex.StatusCode); }
            if (form.Files.Count != 1 || form.Files[0].Name != "photo" || form.Files[0].Length == 0)
                return Results.BadRequest(new { error = "Include exactly one non-empty photo file." });
            var file = form.Files[0];
            if (file.Length > PhotoProcessingService.MaxUploadBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await using var stream = file.OpenReadStream();
            var owner = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var urls = await auth.UploadPhotoAsync(owner, stream, file.ContentType, ct);
            return Results.Ok(Urls(urls.OriginalUrl, owner));
        }).RequireRateLimiting("auth_standard").WithName("UploadPhoto")
          .WithSummary("Replace the private original and abstract/25%/60% reveal images")
          .Accepts<IFormFile>("multipart/form-data");

        group.MapDelete("/photo", async (ClaimsPrincipal principal, IPhotoStorageService photos, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            await photos.DeletePhotoAsync(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), ct);
            return Results.NoContent();
        }).RequireRateLimiting("auth_standard").WithName("DeleteMyPhoto");
        return app;
    }
    private static object Urls(string? original, Guid owner) => new
    {
        originalUrl = PhotoUrls.ForStage(original, owner, RevealClarityStage.Full100),
        abstractUrl = PhotoUrls.ForStage(original, owner, RevealClarityStage.Abstract0),
        quarterUrl = PhotoUrls.ForStage(original, owner, RevealClarityStage.Quarter25),
        sixtyUrl = PhotoUrls.ForStage(original, owner, RevealClarityStage.Sixty60)
    };
}
