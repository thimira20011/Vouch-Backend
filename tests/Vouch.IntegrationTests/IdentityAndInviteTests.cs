using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vouch.Application.Features.Auth;
using Vouch.Application.Features.Moderation;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class IdentityAndInviteTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private Guid _campusId;
    private Guid _architectId;
    private int _clientNumber;

    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync();
        await using var db = postgres.CreateContext();
        var campus = new Campus { Code = "TEST", Name = "Test", DomainPattern = "@test.ac.lk", RequiredAmbassadorsForLaunch = 1 };
        _campusId = campus.Id;
        db.Campuses.Add(campus);
        var architect = User("architect@test.ac.lk", UserRole.Architect);
        _architectId = architect.Id;
        db.Users.Add(architect);
        await db.SaveChangesAsync();
        _factory = new(postgres.Database.ConnectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();
    private HttpClient Client(string? token = null)
    {
        var client = _factory.Client($"198.51.100.{++_clientNumber}");
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private User User(string email, UserRole role = UserRole.Seeker) => new()
    {
        CampusId = _campusId, Email = email, FullName = "Test User", PasswordHash = "unused", Faculty = "Science", Department = "Computing",
        AcademicYear = 2, Role = role, Status = AccountStatus.Active, EmailVerifiedAt = DateTimeOffset.UtcNow,
        OnboardingCompletedAt = DateTimeOffset.UtcNow, DeepValues = ["Sincerity"], IntellectualInterests = [IntellectualInterest.Science]
    };
    private static RegisterRequest Registration(string email, string? invite = null) => new(email, "TestPassword123!", "Test User", "Science", "Computing", 2, "TEST", invite);
    private static CompleteOnboardingRequest Onboarding() => new("Optional photo", ["Sincerity"], [IntellectualInterest.Science]);

    private async Task<AmbassadorInviteDto> InviteAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModerationService>().CreateAmbassadorInviteAsync(_architectId, new(_campusId, email));
    }
    private async Task<AuthResponse> RegisterAsync(string email)
    {
        var invite = await InviteAsync(email);
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/register", Registration(email, invite.Token));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
    private async Task<string> SendAsync(AuthResponse registered)
    {
        using var client = Client(registered.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/verification/request", null)).StatusCode);
        return _factory.Services.GetRequiredService<RecordingEmailService>().VerificationTokens[registered.Email];
    }
    private async Task VerifyAndOnboardAsync(AuthResponse registered)
    {
        var token = await SendAsync(registered);
        using var client = Client(registered.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/onboarding", Onboarding())).StatusCode);
    }
    private async Task UnlockAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Campuses.Where(c => c.Id == _campusId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsSoftLaunchUnlocked, true));
    }

    [Theory]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    [InlineData(false, "")]
    [InlineData(true, " ")]
    public async Task SuppliedInvalidInvitesNeverGrantPrivilege(bool unlocked, string token)
    {
        if (unlocked) await UnlockAsync();
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/register", Registration("student@test.ac.lk", token));
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Conflict });
        await using var db = postgres.CreateContext();
        Assert.False(await db.Users.AnyAsync(u => u.Role == UserRole.Ambassador));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("wrong-email")]
    [InlineData("wrong-campus")]
    public async Task InviteMustBeCurrentUnusedAndBoundToCampusAndEmail(string scenario)
    {
        var invite = await InviteAsync("student@test.ac.lk");
        await using var db = postgres.CreateContext();
        var stored = await db.AmbassadorInvites.SingleAsync();
        switch (scenario)
        {
            case "expired": stored.ExpiresAt = _factory.Clock.GetUtcNow(); break;
            case "used": stored.IsUsed = true; break;
            case "wrong-email": stored.IntendedEmail = "other@test.ac.lk"; break;
            case "wrong-campus":
                var other = new Campus { Code = "OTHER", Name = "Other", DomainPattern = "@other.ac.lk" };
                db.Campuses.Add(other); stored.CampusId = other.Id; break;
        }
        await db.SaveChangesAsync();
        using var client = Client();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", Registration("student@test.ac.lk", invite.Token))).StatusCode);
        Assert.Equal(SingleUseToken.Hash(invite.Token), stored.TokenHash);
        Assert.NotEqual(invite.Token, stored.TokenHash);
    }

    [Fact]
    public async Task ConcurrentRedemptionProducesExactlyOneAccountAndAuditRecord()
    {
        var invite = await InviteAsync("student@test.ac.lk");
        using var first = Client(); using var second = Client();
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/auth/register", Registration("student@test.ac.lk", invite.Token)),
            second.PostAsJsonAsync("/api/auth/register", Registration("STUDENT@TEST.AC.LK", invite.Token)));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order().ToArray());
        await using var db = postgres.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Role == UserRole.Ambassador);
        var stored = await db.AmbassadorInvites.SingleAsync();
        Assert.True(stored.IsUsed);
        Assert.Equal(user.Id, stored.RedeemedByUserId);
        Assert.Equal(_architectId, user.AmbassadorApprovedByArchitectId);
        Assert.NotNull(user.AmbassadorApprovedAt);
        Assert.Equal(AccountStatus.InIncubation, user.Status);
        Assert.Null(user.EmailVerifiedAt);
    }

    [Fact]
    public async Task RegistrationFailureRollsBackInviteRedemption()
    {
        var invite = await InviteAsync("student@test.ac.lk");
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_registration_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected test persistence failure'; END $$;
            CREATE TRIGGER fail_registration_test BEFORE INSERT ON "Users" FOR EACH ROW EXECUTE FUNCTION fail_registration_test();
            """);
        try
        {
            using var client = Client();
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.PostAsJsonAsync("/api/auth/register", Registration("student@test.ac.lk", invite.Token))).StatusCode);
            Assert.False((await db.AmbassadorInvites.AsNoTracking().SingleAsync()).IsUsed);
            Assert.Equal(1, await db.Users.CountAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_registration_test ON \"Users\"; DROP FUNCTION fail_registration_test()"); }
        using var retry = Client();
        Assert.Equal(HttpStatusCode.Created, (await retry.PostAsJsonAsync("/api/auth/register", Registration("student@test.ac.lk", invite.Token))).StatusCode);
    }

    [Theory]
    [InlineData("student@other.ac.lk")]
    [InlineData("student@eviltest.ac.lk")]
    [InlineData("student@test.ac.lk.evil.org")]
    [InlineData("student@sub.test.ac.lk")]
    public async Task CampusCannotBeSpoofedUsingUniversitySuffix(string email)
    {
        await UnlockAsync(); using var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", Registration(email))).StatusCode);
    }

    [Fact]
    public async Task UniversityOwnershipAndOnboardingAreRequiredEvenWithValidInvite()
    {
        var user = await RegisterAsync("student@test.ac.lk");
        using var client = Client(user.Token);
        Assert.False(user.EmailVerified); Assert.False(user.OnboardingCompleted);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/matches/today")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/onboarding", Onboarding())).StatusCode);
        var token = await SendAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(token))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/matches/today")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/onboarding", Onboarding())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/matches/today")).StatusCode); // original token works with current DB eligibility
        var status = (await client.GetFromJsonAsync<IdentityStatusResponse>("/api/auth/identity"))!;
        Assert.True(status.EmailVerified); Assert.True(status.OnboardingCompleted); Assert.Equal("Active", status.Status);
        await using var db = postgres.CreateContext();
        var stored = await db.Users.SingleAsync(u => u.Id == user.UserId);
        Assert.Null(stored.OriginalPhotoUrl);
        Assert.Equal(SingleUseToken.Hash(token), (await db.EmailVerifications.SingleAsync()).TokenHash);
    }

    [Fact]
    public async Task VerificationExpiryResendAndWrongUserAreEnforced()
    {
        var first = await RegisterAsync("first@test.ac.lk"); var second = await RegisterAsync("second@test.ac.lk");
        var original = await SendAsync(first);
        using var otherClient = Client(second.Token);
        Assert.Equal(HttpStatusCode.Conflict, (await otherClient.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(original))).StatusCode);
        using var client = Client(first.Token);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/auth/verification/request", null)).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var replacement = await SendAsync(first);
        Assert.NotEqual(original, replacement);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(original))).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(replacement))).StatusCode);
        Assert.False((await client.GetFromJsonAsync<IdentityStatusResponse>("/api/auth/identity"))!.EmailVerified);
    }

    [Fact]
    public async Task ParallelVerificationConsumesTheChallengeOnce()
    {
        var user = await RegisterAsync("student@test.ac.lk"); var token = await SendAsync(user);
        using var first = Client(user.Token); using var second = Client(user.Token);
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(token)),
            second.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(token)));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order().ToArray());
    }

    [Fact]
    public async Task DeliveryFailureDoesNotVerifyOrConsumeCooldown()
    {
        var user = await RegisterAsync("student@test.ac.lk");
        var sender = _factory.Services.GetRequiredService<RecordingEmailService>(); sender.FailVerification = true;
        using var client = Client(user.Token);
        var failed = await client.PostAsync("/api/auth/verification/request", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain(user.Email, await failed.Content.ReadAsStringAsync());
        await using var db = postgres.CreateContext(); Assert.Empty(await db.EmailVerifications.ToListAsync());
        sender.FailVerification = false;
        await SendAsync(user);
    }

    [Fact]
    public async Task ReadinessUsesEveryActiveAmbassadorAndRecalculatesWhenEligibilityChanges()
    {
        var ambassador = await RegisterAsync("ambassador@test.ac.lk"); await VerifyAndOnboardAsync(ambassador);
        await using var db = postgres.CreateContext();
        var first = User("peer1@test.ac.lk"); first.Status = AccountStatus.InIncubation;
        var second = User("peer2@test.ac.lk"); second.Status = AccountStatus.InIncubation;
        db.Users.AddRange(first, second); await db.SaveChangesAsync();
        db.Vouches.AddRange(new VouchRecord { VoucherUserId = ambassador.UserId, TargetUserId = first.Id, Traits = CharacterTrait.Sincere },
            new VouchRecord { VoucherUserId = ambassador.UserId, TargetUserId = second.Id, Traits = CharacterTrait.Sincere });
        await db.SaveChangesAsync();
        Assert.True((await db.Campuses.SingleAsync()).IsSoftLaunchUnlocked);
        var extra = User("extra@test.ac.lk", UserRole.Ambassador);
        extra.AmbassadorApprovedAt = DateTimeOffset.UtcNow; extra.AmbassadorApprovedByArchitectId = _architectId;
        db.Users.Add(extra); await db.SaveChangesAsync();
        Assert.False((await db.Campuses.SingleAsync()).IsSoftLaunchUnlocked);
        extra.Status = AccountStatus.Suspended; await db.SaveChangesAsync();
        Assert.True((await db.Campuses.SingleAsync()).IsSoftLaunchUnlocked);
        second.OnboardingCompletedAt = null; await db.SaveChangesAsync();
        Assert.False((await db.Campuses.SingleAsync()).IsSoftLaunchUnlocked);
        second.OnboardingCompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        Assert.True((await db.Campuses.SingleAsync()).IsSoftLaunchUnlocked);
        db.Vouches.Remove(await db.Vouches.FirstAsync()); await db.SaveChangesAsync();
        Assert.False((await db.Campuses.SingleAsync()).IsSoftLaunchUnlocked);
        using var scope = _factory.Services.CreateScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<IModerationService>().GetArchitectDashboardAsync(_campusId)).LaunchReadiness.IsGatePassed);
    }

    [Fact]
    public async Task OrdinaryRegistrationStaysInIncubationAndInviteIssuanceRequiresArchitectAndEmail()
    {
        await UnlockAsync(); using var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/register", Registration("student@test.ac.lk"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal("Seeker", user.Role); Assert.Equal("InIncubation", user.Status); Assert.False(user.HasFoundingMemberBadge);
        using var scope = _factory.Services.CreateScope(); var moderation = scope.ServiceProvider.GetRequiredService<IModerationService>();
        await Assert.ThrowsAsync<Vouch.Application.Common.EligibilityException>(() => moderation.CreateAmbassadorInviteAsync(user.UserId, new(_campusId, user.Email)));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => moderation.CreateAmbassadorInviteAsync(_architectId, new(_campusId)));
        await Assert.ThrowsAsync<ArgumentException>(() => moderation.CreateAmbassadorInviteAsync(_architectId, new(_campusId, "student@other.ac.lk")));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("campus")]
    public async Task ChallengeCannotVerifyChangedIdentity(string change)
    {
        var registered = await RegisterAsync("student@test.ac.lk"); var token = await SendAsync(registered);
        await using var db = postgres.CreateContext(); var user = await db.Users.SingleAsync(u => u.Id == registered.UserId);
        if (change == "email") user.Email = "changed@test.ac.lk";
        else
        {
            var campus = new Campus { Code = "OTHER", Name = "Other approved campus", DomainPattern = "@test.ac.lk" };
            db.Campuses.Add(campus); user.CampusId = campus.Id;
        }
        await db.SaveChangesAsync();
        using var client = Client(registered.Token);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(token))).StatusCode);
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).EmailVerifiedAt);
    }

    [Fact]
    public async Task ManualApprovalIsAuditedAndRequiresVerifiedOnboardingAndCurrentArchitect()
    {
        await using var db = postgres.CreateContext();
        var user = User("existing@test.ac.lk", UserRole.Ambassador); user.Status = AccountStatus.InIncubation; user.OnboardingCompletedAt = null;
        db.Users.Add(user); await db.SaveChangesAsync();
        using var scope = _factory.Services.CreateScope();
        var moderation = scope.ServiceProvider.GetRequiredService<IModerationService>();
        await Assert.ThrowsAsync<Vouch.Application.Common.EligibilityException>(() => moderation.ApproveAmbassadorAsync(_architectId, user.Id));
        user.OnboardingCompletedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        // Each operation uses a fresh scoped context, as an HTTP request would.
        await using (var approval = _factory.Services.CreateAsyncScope())
            await approval.ServiceProvider.GetRequiredService<IModerationService>().ApproveAmbassadorAsync(_architectId, user.Id);
        var approved = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(_architectId, approved.AmbassadorApprovedByArchitectId); Assert.NotNull(approved.AmbassadorApprovedAt);
        Assert.Equal(AccountStatus.Active, approved.Status); Assert.True(approved.HasFoundingMemberBadge);
        var architect = await db.Users.SingleAsync(u => u.Id == _architectId); architect.Status = AccountStatus.Suspended; await db.SaveChangesAsync();
        await using var denied = _factory.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<Vouch.Application.Common.EligibilityException>(() =>
            denied.ServiceProvider.GetRequiredService<IModerationService>().ApproveAmbassadorAsync(_architectId, user.Id));
    }

    [Fact]
    public async Task LegacyActiveUnverifiedUsersCannotMatchOrVouch()
    {
        await using var db = postgres.CreateContext();
        var legacy = User("legacy@test.ac.lk"); legacy.EmailVerifiedAt = null;
        var verified = User("verified@test.ac.lk");
        db.Users.AddRange(legacy, verified); await db.SaveChangesAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await Assert.ThrowsAsync<Vouch.Application.Common.EligibilityException>(() =>
            services.GetRequiredService<Vouch.Application.Features.Matching.IMatchService>().GetTodayConnectionAsync(legacy.Id));
        await Assert.ThrowsAsync<Vouch.Application.Common.EligibilityException>(() =>
            services.GetRequiredService<Vouch.Application.Features.Vouching.ITrustService>().SubmitVouchAsync(legacy.Id,
                new(verified.Id, CharacterTrait.Sincere, "Not qualified")));
        await services.GetRequiredService<Vouch.Application.Features.Matching.IMatchService>().GenerateDailyMatchesForCampusAsync(_campusId);
        Assert.DoesNotContain(await db.Matches.ToListAsync(), m => m.UserAId == legacy.Id || m.UserBId == legacy.Id);
    }
}
