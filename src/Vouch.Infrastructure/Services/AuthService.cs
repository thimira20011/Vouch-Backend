using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.Services;

public class AuthService(ApplicationDbContext context, IPasswordHasher passwordHasher, IJwtTokenService jwtTokenService,
    IEmailLookup emailLookup, IPhotoStorageService photoStorage, IVerificationEmailSender verificationSender, TimeProvider clock) : IAuthService
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        await new RegisterRequestValidator().ValidateAndThrowAsync(request, ct);
        var normalizedEmail = emailLookup.Normalize(request.Email);
        var lookupHash = emailLookup.Hash(normalizedEmail);
        var campus = await context.Campuses.AsNoTracking().SingleOrDefaultAsync(c => c.Code == request.CampusCode, ct)
            ?? throw new KeyNotFoundException("Campus not found.");
        if (!UniversityIdentity.IsApprovedEmail(normalizedEmail, campus.DomainPattern))
            throw new ArgumentException("Email must use the selected campus's approved mailbox domain.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(campus.Id, ct);
        campus = await context.Campuses.AsNoTracking().SingleAsync(c => c.Id == campus.Id, ct);
        if (await context.Users.AnyAsync(u => u.EmailLookupHash == lookupHash, ct))
            throw new InvalidOperationException("An account with this university email already exists.");
        AmbassadorInvite? invite = null;
        var now = clock.GetUtcNow();
        if (request.InviteToken is not null)
        {
            var digest = SingleUseToken.Hash(request.InviteToken);
            invite = await context.AmbassadorInvites.AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == digest &&
                i.CampusId == campus.Id && i.IntendedEmailLookupHash == lookupHash && !i.IsUsed && i.ExpiresAt > now, ct)
                ?? throw new InvalidOperationException("The invite token is invalid or has expired.");
        }
        else if (!campus.IsSoftLaunchUnlocked)
            throw new InvalidOperationException("General registration is locked. A valid ambassador invite token is required.");
        var user = new User
        {
            CampusId = campus.Id, Email = normalizedEmail, EmailLookupHash = lookupHash,
            PasswordHash = passwordHasher.HashPassword(request.Password), FullName = request.FullName.Trim(),
            Faculty = request.Faculty.Trim(), Department = request.Department.Trim(), AcademicYear = request.AcademicYear,
            Role = invite is null ? UserRole.Seeker : UserRole.Ambassador,
            Status = AccountStatus.InIncubation, HasFoundingMemberBadge = invite is not null,
            AmbassadorApprovedByArchitectId = invite?.IssuedByArchitectId, AmbassadorApprovedAt = invite?.CreatedAt
        };
        if (invite is not null)
        {
            var redeemed = await context.AmbassadorInvites.Where(i => i.Id == invite.Id && !i.IsUsed && i.ExpiresAt > now &&
                i.IntendedEmailLookupHash == lookupHash && i.CampusId == campus.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.IsUsed, true).SetProperty(i => i.UsedAt, now).SetProperty(i => i.RedeemedByUserId, user.Id), ct);
            if (redeemed != 1) throw new InvalidOperationException("The invite token is invalid or has expired.");
        }
        context.Users.Add(user);
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
            { SqlState: "23505", ConstraintName: "IX_Users_EmailLookupHash" })
        { throw new InvalidOperationException("An account with this university email already exists."); }
        await transaction.CommitAsync(ct);
        return Response(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var lookupHash = emailLookup.Hash(request.Email);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.EmailLookupHash == lookupHash, ct)
            ?? throw new UnauthorizedAccessException("Invalid email or password.");
        if (!passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid email or password.");
        EnsureAccountUsable(user);
        return Response(user);
    }

    public async Task RequestEmailVerificationAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(user.CampusId, ct);
        user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        EnsureAccountUsable(user);
        if (user.EmailVerifiedAt is not null) return;
        var campus = await context.Campuses.AsNoTracking().SingleAsync(c => c.Id == user.CampusId, ct);
        if (!UniversityIdentity.IsApprovedEmail(user.Email, campus.DomainPattern))
            throw new ArgumentException("This account's email does not match the campus's approved domain.");
        var now = clock.GetUtcNow();
        var challenge = await context.EmailVerifications.SingleOrDefaultAsync(v => v.UserId == userId, ct);
        if (challenge is not null && challenge.CreatedAt > now.AddMinutes(-1))
            throw new InvalidOperationException("Wait one minute before requesting another verification email.");
        var token = SingleUseToken.Create();
        if (challenge is null)
        {
            challenge = new EmailVerification { UserId = userId, CampusId = user.CampusId, EmailLookupHash = user.EmailLookupHash, TokenHash = SingleUseToken.Hash(token) };
            context.EmailVerifications.Add(challenge);
        }
        challenge.CampusId = user.CampusId;
        challenge.EmailLookupHash = user.EmailLookupHash;
        challenge.TokenHash = SingleUseToken.Hash(token);
        challenge.CreatedAt = now;
        challenge.ExpiresAt = now.AddMinutes(30);
        challenge.UsedAt = null;
        await context.SaveChangesAsync(ct);
        // Unavailable SMTP rolls back the challenge and cooldown. A post-send commit failure
        // may leave an unusable email; requesting a fresh challenge recovers.
        await verificationSender.SendVerificationAsync(user.Email, token, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IdentityStatusResponse> VerifyEmailAsync(Guid userId, VerifyEmailRequest request, CancellationToken ct = default)
    {
        await new VerifyEmailRequestValidator().ValidateAndThrowAsync(request, ct);
        var identity = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(identity.CampusId, ct);
        var user = await context.Users.SingleAsync(u => u.Id == userId, ct);
        EnsureAccountUsable(user);
        var campus = await context.Campuses.AsNoTracking().SingleAsync(c => c.Id == user.CampusId, ct);
        if (!UniversityIdentity.IsApprovedEmail(user.Email, campus.DomainPattern))
            throw new ArgumentException("This account's email does not match the campus's approved domain.");
        var now = clock.GetUtcNow();
        var digest = SingleUseToken.Hash(request.Token);
        var consumed = await context.EmailVerifications.Where(v => v.UserId == userId && v.CampusId == user.CampusId &&
            v.EmailLookupHash == user.EmailLookupHash && v.TokenHash == digest && v.UsedAt == null && v.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.UsedAt, now), ct);
        if (consumed != 1) throw new InvalidOperationException("The verification token is invalid or has expired.");
        user.EmailVerifiedAt = now;
        await ActivateIfEligibleAsync(user, now, ct);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Identity(user);
    }

    public async Task CompleteOnboardingAsync(Guid userId, CompleteOnboardingRequest request, CancellationToken ct = default)
    {
        await new CompleteOnboardingRequestValidator().ValidateAndThrowAsync(request, ct);
        var identity = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockCampusAsync(identity.CampusId, ct);
        var user = await context.Users.SingleAsync(u => u.Id == userId, ct);
        EnsureAccountUsable(user);
        if (user.EmailVerifiedAt is null) throw new EligibilityException("Verify your university email before completing onboarding.");
        user.Bio = request.Bio ?? string.Empty;
        user.DeepValues = request.DeepValues.ToList();
        user.IntellectualInterests = request.IntellectualInterests.ToList();
        var now = clock.GetUtcNow();
        user.OnboardingCompletedAt ??= now;
        await ActivateIfEligibleAsync(user, now, ct);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ActivateIfEligibleAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        if (user.Status != AccountStatus.InIncubation || !UniversityIdentity.IsOnboarded(user)) return;
        var approvedAmbassador = user.Role == UserRole.Ambassador && user.AmbassadorApprovedByArchitectId != null && user.AmbassadorApprovedAt != null;
        var eligibleVouches = await context.Vouches.CountAsync(v => v.TargetUserId == user.Id && v.VoucherUser.CampusId == user.CampusId &&
            v.VoucherUserId != user.Id && v.Traits != CharacterTrait.None && (v.Traits & ~LaunchEligibility.ValidTraits) == CharacterTrait.None &&
            v.VoucherUser.Status == AccountStatus.Active && v.VoucherUser.EmailVerifiedAt != null && v.VoucherUser.OnboardingCompletedAt != null, ct);
        user.ActiveVouchesReceivedCount = eligibleVouches;
        if (approvedAmbassador || eligibleVouches >= 3)
        {
            user.Status = AccountStatus.Active;
            user.IncubationCompletedAt ??= now;
        }
    }

    public async Task<IdentityStatusResponse> GetIdentityStatusAsync(Guid userId, CancellationToken ct = default)
        => Identity(await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new KeyNotFoundException("User not found."));

    public async Task<(string OriginalUrl, string AbstractUrl)> UploadPhotoAsync(Guid userId, Stream stream, string contentType, CancellationToken ct = default)
    {
        var user = await context.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        EnsureAccountUsable(user);
        if (!UniversityIdentity.IsOnboarded(user)) throw new EligibilityException("Complete verified onboarding before uploading a photo.");
        var urls = await photoStorage.StorePhotoAsync(userId, stream, contentType, ct);
        user.OriginalPhotoUrl = urls.OriginalUrl;
        user.OilPaintingAbstractPhotoUrl = urls.AbstractUrl;
        await context.SaveChangesAsync(ct);
        return urls;
    }

    public async Task RequestAccountDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await context.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        user.Status = AccountStatus.DeletionRequested;
        user.DeletionRequestedAt ??= clock.GetUtcNow();
        await context.SaveChangesAsync(ct);
    }

    private Task LockCampusAsync(Guid campusId, CancellationToken ct)
        => context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\" = {campusId} FOR UPDATE", ct);
    private static void EnsureAccountUsable(User user)
    {
        if (user.Status is AccountStatus.Suspended or AccountStatus.DeletionRequested)
            throw new EligibilityException("This account is unavailable for authentication or onboarding.");
    }
    private AuthResponse Response(User user) => new(user.Id, user.Email, user.FullName, user.Role.ToString(), user.Status.ToString(),
        user.TrustScore, user.HasFoundingMemberBadge, jwtTokenService.GenerateToken(user), user.EmailVerifiedAt != null, user.OnboardingCompletedAt != null);
    private static IdentityStatusResponse Identity(User user) => new(user.EmailVerifiedAt != null, user.OnboardingCompletedAt != null, user.Status.ToString());
}
