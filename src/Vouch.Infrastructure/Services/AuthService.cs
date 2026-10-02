using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IEmailLookup _emailLookup;
    private readonly IPhotoStorageService _photoStorage;

    public AuthService(
        IApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IEmailLookup emailLookup,
        IPhotoStorageService photoStorage)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _emailLookup = emailLookup;
        _photoStorage = photoStorage;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var campus = await _context.Campuses.FirstOrDefaultAsync(c => c.Code == request.CampusCode, ct)
            ?? throw new KeyNotFoundException($"Campus '{request.CampusCode}' not found.");

        // REQ-1: Users must verify identity via a .ac.lk or university-approved email domain
        var normalizedEmail = _emailLookup.Normalize(request.Email);
        if (!normalizedEmail.EndsWith(campus.DomainPattern, StringComparison.OrdinalIgnoreCase) &&
            !normalizedEmail.EndsWith(".ac.lk", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Registration requires a verified university email matching '{campus.DomainPattern}' or '.ac.lk'.");
        }

        // REQ-A5: Soft Launch Gate: general registration locked until campus reaches 30 active Ambassadors with vouches
        if (!campus.IsSoftLaunchUnlocked)
        {
            if (request.InviteToken is null)
                throw new InvalidOperationException("General registration is locked. A valid ambassador invite token is required.");

            var invite = await _context.AmbassadorInvites
                .FirstOrDefaultAsync(i => i.Token == request.InviteToken &&
                                          i.CampusId == campus.Id &&
                                          !i.IsUsed &&
                                          i.ExpiresAt > DateTimeOffset.UtcNow, ct)
                ?? throw new InvalidOperationException("The invite token is invalid or has expired.");

            invite.IsUsed = true;
            invite.UsedAt = DateTimeOffset.UtcNow;
        }

        var lookupHash = _emailLookup.Hash(normalizedEmail);
        var emailExists = await _context.Users.AnyAsync(u => u.EmailLookupHash == lookupHash, ct);
        if (emailExists)
        {
            throw new InvalidOperationException("An account with this university email already exists.");
        }

        var isAmbassador = request.InviteToken is not null; // if they had an invite, they're an ambassador

        var user = new User
        {
            CampusId = campus.Id,
            Email = normalizedEmail, // Persistence converters protect PII; services use readable values.
            EmailLookupHash = lookupHash,
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            FullName = request.FullName.Trim(),
            Faculty = request.Faculty.Trim(),
            Department = request.Department.Trim(),
            AcademicYear = request.AcademicYear,
            Role = isAmbassador ? UserRole.Ambassador : UserRole.Seeker,
            // REQ-A1 & REQ-2: Ambassadors exempt from 3-vouch gate (Active immediately)
            Status = isAmbassador ? AccountStatus.Active : AccountStatus.InIncubation,
            // REQ-A4: Founding Member badge
            HasFoundingMemberBadge = isAmbassador
        };

        _context.Users.Add(user);
        try { await _context.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
            { SqlState: "23505", ConstraintName: "IX_Users_EmailLookupHash" })
        {
            // Concurrent registrations are resolved by the database uniqueness constraint.
            throw new InvalidOperationException("An account with this university email already exists.");
        }

        var token = _jwtTokenService.GenerateToken(user);

        return new AuthResponse(
            UserId: user.Id,
            Email: user.Email,
            FullName: user.FullName,
            Role: user.Role.ToString(),
            Status: user.Status.ToString(),
            TrustScore: user.TrustScore,
            HasFoundingMemberBadge: user.HasFoundingMemberBadge,
            Token: token
        );
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var lookupHash = _emailLookup.Hash(request.Email);
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.EmailLookupHash == lookupHash, ct)
            ?? throw new UnauthorizedAccessException("Invalid email or password.");

        if (user.Status == AccountStatus.Suspended)
        {
            throw new InvalidOperationException("Your account has been suspended due to community guidelines violations.");
        }

        if (!_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        var token = _jwtTokenService.GenerateToken(user);

        return new AuthResponse(
            UserId: user.Id,
            Email: user.Email,
            FullName: user.FullName,
            Role: user.Role.ToString(),
            Status: user.Status.ToString(),
            TrustScore: user.TrustScore,
            HasFoundingMemberBadge: user.HasFoundingMemberBadge,
            Token: token
        );
    }

    public async Task CompleteOnboardingAsync(Guid userId, CompleteOnboardingRequest request, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        if (!string.IsNullOrEmpty(request.Bio))
        {
            if (request.Bio.Length > 280)
                throw new ArgumentException("Bio must not exceed 280 characters.");
            user.Bio = request.Bio;
        }
        else
        {
            if (request.Bio?.Length > 280)
                throw new ArgumentException("Bio must not exceed 280 characters.");
            user.Bio = request.Bio ?? string.Empty;
        }
        user.DeepValues = request.DeepValues;
        user.IntellectualInterests = request.IntellectualInterests;
        // Step 21: Photo uploaded via dedicated PUT /api/profile/photo endpoint (REQ-17)

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Step 21 — REQ-17: Stores the user photo and creates the blurred abstract variant.
    /// Returns the public URLs for both versions.
    /// </summary>
    public async Task<(string OriginalUrl, string AbstractUrl)> UploadPhotoAsync(
        Guid userId, Stream stream, string contentType, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var (originalUrl, abstractUrl) = await _photoStorage.StorePhotoAsync(userId, stream, contentType, ct);

        user.OriginalPhotoUrl = originalUrl;
        user.OilPaintingAbstractPhotoUrl = abstractUrl;

        await _context.SaveChangesAsync(ct);

        return (originalUrl, abstractUrl);
    }

    public async Task RequestAccountDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        // NFR-6: Mark deletion requested, completed within 30 days
        user.Status = AccountStatus.DeletionRequested;
        user.DeletionRequestedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync(ct);
    }
}
