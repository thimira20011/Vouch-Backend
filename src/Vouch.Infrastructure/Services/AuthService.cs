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
    private readonly IEncryptionService _encryption;

    public AuthService(
        IApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IEncryptionService encryption)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _encryption = encryption;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var campus = await _context.Campuses.FirstOrDefaultAsync(c => c.Code == request.CampusCode, ct)
            ?? throw new KeyNotFoundException($"Campus '{request.CampusCode}' not found.");

        // REQ-1: Users must verify identity via a .ac.lk or university-approved email domain
        var normalizedEmail = request.Email.ToLower().Trim();
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

        // NFR-9: Encrypt PII before storing
        var encryptedEmail = _encryption.Encrypt(normalizedEmail);
        var emailExists = await _context.Users.AnyAsync(u => u.Email == encryptedEmail, ct);
        if (emailExists)
        {
            throw new InvalidOperationException("An account with this university email already exists.");
        }

        var isAmbassador = request.InviteToken is not null; // if they had an invite, they're an ambassador

        var user = new User
        {
            CampusId = campus.Id,
            Email = _encryption.Encrypt(normalizedEmail),     // NFR-9: PII encrypted at rest
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            FullName = _encryption.Encrypt(request.FullName.Trim()), // NFR-9
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
        await _context.SaveChangesAsync(ct);

        var token = _jwtTokenService.GenerateToken(user);

        return new AuthResponse(
            UserId: user.Id,
            Email: _encryption.Decrypt(user.Email),           // Decrypt for response
            FullName: _encryption.Decrypt(user.FullName),     // Decrypt for response
            Role: user.Role.ToString(),
            Status: user.Status.ToString(),
            TrustScore: user.TrustScore,
            HasFoundingMemberBadge: user.HasFoundingMemberBadge,
            Token: token
        );
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        // Encrypt the lookup email to match stored encrypted value (NFR-9)
        var encryptedEmail = _encryption.Encrypt(request.Email.ToLower().Trim());
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == encryptedEmail, ct)
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
            Email: _encryption.Decrypt(user.Email),        // Decrypt PII for response
            FullName: _encryption.Decrypt(user.FullName),  // Decrypt PII for response
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
            user.Bio = _encryption.Encrypt(request.Bio); // NFR-9: encrypt PII
        }
        else
        {
            if (request.Bio?.Length > 280)
                throw new ArgumentException("Bio must not exceed 280 characters.");
            user.Bio = request.Bio ?? string.Empty;
        }
        user.DeepValues = request.DeepValues;
        user.IntellectualInterests = request.IntellectualInterests;

        if (!string.IsNullOrEmpty(request.PhotoBase64))
        {
            user.OriginalPhotoUrl = request.PhotoBase64;
            // Generate abstract initial placeholder for Slow-Burn (REQ-17)
            user.OilPaintingAbstractPhotoUrl = request.PhotoBase64; 
        }

        await _context.SaveChangesAsync(ct);
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
