using System.Security.Claims;
using Vouch.Application.Features.Auth;

namespace Vouch.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication & Onboarding");

        group.MapPost("/register", async (RegisterRequest request, IAuthService authService, CancellationToken ct) =>
        {
            try
            {
                var response = await authService.RegisterAsync(request, ct);
                return Results.Created($"/api/users/{response.UserId}", response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .WithName("Register")
        .WithSummary("Register with .ac.lk university email")
        .RequireRateLimiting("auth_strict");

        group.MapPost("/login", async (LoginRequest request, IAuthService authService, CancellationToken ct) =>
        {
            try
            {
                var response = await authService.LoginAsync(request, ct);
                return Results.Ok(response);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403);
            }
        })
        .WithName("Login")
        .WithSummary("Login to account")
        .RequireRateLimiting("auth_strict");

        group.MapPost("/onboarding", async (CompleteOnboardingRequest request, ClaimsPrincipal principal, IAuthService authService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            try
            {
                await authService.CompleteOnboardingAsync(userId, request, ct);
                return Results.Ok(new { message = "Onboarding completed successfully." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .RequireAuthorization()
        .WithName("CompleteOnboarding")
        .WithSummary("Submit Deep Values, Intellectual Interests, and Bio")
        .RequireRateLimiting("auth_standard");

        group.MapPost("/delete-account", async (ClaimsPrincipal principal, IAuthService authService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            await authService.RequestAccountDeletionAsync(userId, ct);
            return Results.Ok(new { message = "Account scheduled for permanent deletion within 30 days." });
        })
        .RequireAuthorization()
        .WithName("RequestAccountDeletion")
        .WithSummary("Request permanent account deletion (NFR-6)")
        .RequireRateLimiting("auth_standard");

        return app;
    }
}
