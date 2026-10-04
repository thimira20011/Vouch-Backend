using System.Security.Claims;
using Vouch.Api.Middleware;
using Vouch.Application.Features.Auth;

namespace Vouch.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication & Onboarding");

        group.MapGet("/onboarding/options", () => Results.Ok(new
        {
            deepValues = OnboardingOptions.DeepValues,
            intellectualInterests = Enum.GetValues<Vouch.Domain.Enums.IntellectualInterest>().Select(i => new { id = (int)i, name = i.ToString() })
        })).WithName("GetOnboardingOptions");

        group.MapGet("/identity", async (ClaimsPrincipal principal, IAuthService service, CancellationToken ct) =>
            Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                ? Results.Ok(await service.GetIdentityStatusAsync(id, ct)) : Results.Unauthorized())
            .RequireAuthorization().WithName("GetIdentityStatus");

        group.MapPost("/verification/request", async (ClaimsPrincipal principal, IAuthService service, CancellationToken ct) =>
        {
            if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Results.Unauthorized();
            await service.RequestEmailVerificationAsync(id, ct);
            return Results.Ok(new { message = "Verification email sent. The token expires in 30 minutes." });
        }).RequireAuthorization().RequireRateLimiting("auth_strict").WithName("RequestEmailVerification");

        group.MapPost("/verification/confirm", async (VerifyEmailRequest request, ClaimsPrincipal principal, IAuthService service, CancellationToken ct) =>
            Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                ? Results.Ok(await service.VerifyEmailAsync(id, request, ct)) : Results.Unauthorized())
            .AddEndpointFilter<ValidationFilter<VerifyEmailRequest>>()
            .RequireAuthorization().RequireRateLimiting("auth_strict").WithName("VerifyEmail");

        group.MapPost("/register", async (RegisterRequest request, IAuthService authService, CancellationToken ct) =>
        {
            var response = await authService.RegisterAsync(request, ct);
            return Results.Created($"/api/users/{response.UserId}", response);
        })
        .AddEndpointFilter<ValidationFilter<RegisterRequest>>()
        .WithName("Register")
        .WithSummary("Register with .ac.lk university email")
        .RequireRateLimiting("auth_strict");

        group.MapPost("/login", async (LoginRequest request, IAuthService authService, CancellationToken ct) =>
        {
            var response = await authService.LoginAsync(request, ct);
            return Results.Ok(response);
        })
        .AddEndpointFilter<ValidationFilter<LoginRequest>>()
        .WithName("Login")
        .WithSummary("Login to account")
        .RequireRateLimiting("auth_strict");

        group.MapPost("/onboarding", async (CompleteOnboardingRequest request, ClaimsPrincipal principal, IAuthService authService, CancellationToken ct) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdStr, out var userId)) return Results.Unauthorized();

            await authService.CompleteOnboardingAsync(userId, request, ct);
            return Results.Ok(new { message = "Onboarding completed successfully." });
        })
        .AddEndpointFilter<ValidationFilter<CompleteOnboardingRequest>>()
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
