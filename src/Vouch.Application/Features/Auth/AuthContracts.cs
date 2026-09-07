using Vouch.Domain.Enums;

namespace Vouch.Application.Features.Auth;

public record RegisterRequest(
    string Email,
    string Password,
    string FullName,
    string Faculty,
    string Department,
    int AcademicYear,
    string CampusCode
);

public record LoginRequest(
    string Email,
    string Password
);

public record CompleteOnboardingRequest(
    string Bio,
    List<string> DeepValues,
    List<IntellectualInterest> IntellectualInterests,
    string? PhotoBase64
);

public record AuthResponse(
    Guid UserId,
    string Email,
    string FullName,
    string Role,
    string Status,
    double TrustScore,
    bool HasFoundingMemberBadge,
    string Token
);

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task CompleteOnboardingAsync(Guid userId, CompleteOnboardingRequest request, CancellationToken ct = default);
    Task RequestAccountDeletionAsync(Guid userId, CancellationToken ct = default); // NFR-6
}
