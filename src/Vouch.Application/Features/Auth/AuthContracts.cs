using Vouch.Domain.Enums;

namespace Vouch.Application.Features.Auth;

public record RegisterRequest(
    string Email,
    string Password,
    string FullName,
    string Faculty,
    string Department,
    int AcademicYear,
    string CampusCode,
    string? InviteToken = null  // Required during bootstrap phase (REQ-A5)
);

public record LoginRequest(
    string Email,
    string Password
);

public record VerifyEmailRequest(string Token);
public record IdentityStatusResponse(bool EmailVerified, bool OnboardingCompleted, string Status);

public record CompleteOnboardingRequest(
    string Bio,
    List<string> DeepValues,
    List<IntellectualInterest> IntellectualInterests
    // Step 21: Photo is uploaded separately via PUT /api/profile/photo (REQ-17)
);

public record AuthResponse(
    Guid UserId,
    string Email,
    string FullName,
    string Role,
    string Status,
    double TrustScore,
    bool HasFoundingMemberBadge,
    string Token,
    bool EmailVerified = false,
    bool OnboardingCompleted = false
);

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task RequestEmailVerificationAsync(Guid userId, CancellationToken ct = default);
    Task<IdentityStatusResponse> VerifyEmailAsync(Guid userId, VerifyEmailRequest request, CancellationToken ct = default);
    Task<IdentityStatusResponse> GetIdentityStatusAsync(Guid userId, CancellationToken ct = default);
    Task CompleteOnboardingAsync(Guid userId, CompleteOnboardingRequest request, CancellationToken ct = default);
    Task<(string OriginalUrl, string AbstractUrl)> UploadPhotoAsync(Guid userId, Stream stream, string contentType, CancellationToken ct = default); // Step 21 REQ-17
    Task RequestAccountDeletionAsync(Guid userId, CancellationToken ct = default); // NFR-6
}
