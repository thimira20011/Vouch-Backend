using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Application.Features.Matching;
using Vouch.Application.Features.Messaging;
using Vouch.Application.Features.Moderation;
using Vouch.Application.Features.Vouching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.BackgroundJobs;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ApiSmokeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private Guid _campusId;

    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync();
        await using var db = postgres.CreateContext();
        var campus = new Campus { Name = "Test Campus", Code = "TEST", DomainPattern = "@test.ac.lk", IsSoftLaunchUnlocked = true };
        _campusId = campus.Id;
        db.Campuses.Add(campus);
        db.Reflections.Add(new DailyReflection
        {
            Category = IntellectualInterest.Philosophy, Quote = "A test reflection", Author = "Test",
            ThoughtProvokingQuestion = "What did you learn?"
        });
        await db.SaveChangesAsync();
        _factory = new VouchApiFactory(postgres.Database.ConnectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static RegisterRequest Registration() => new("s@test.ac.lk", "TestPassword123!", "Test User", "Science", "Computing", 2, "TEST");

    [Fact]
    public async Task Database_MigratesFromInitialVersionToLatestAndExecutesPostgresQueries()
    {
        await using var db = postgres.CreateContext();
        Assert.True(postgres.FirstMigrationApplied);
        Assert.Equal(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("Test Campus", await db.Campuses.AsNoTracking().Where(c => c.Id == _campusId).Select(c => c.Name).SingleAsync());
    }

    [Fact]
    public async Task HealthCheck_UsesIsolatedPostgresAndSucceeds()
    {
        using var client = _factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Equal(postgres.Database.Name, scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetDbConnection().Database);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.ToListAsync());
    }

    [Fact]
    public async Task Register_Onboard_AndRetrieveToday_ExecutesApiAndDatabase()
    {
        using var client = _factory.Client();
        var response = await client.PostAsJsonAsync("/api/auth/register", Registration());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(registered);
        Assert.Equal("InIncubation", registered.Status);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token);
        await VerifyAsync(client, registered.Email);
        var onboard = await client.PostAsJsonAsync("/api/auth/onboarding", new CompleteOnboardingRequest(
            "Short test bio", ["Sincerity"], [IntellectualInterest.Philosophy]));
        Assert.Equal(HttpStatusCode.OK, onboard.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/matches/today")).StatusCode); // verified seekers still need three vouches
        await using var db = postgres.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Id == registered.UserId);
        Assert.Equal(registered.Email, user.Email);
        await db.Database.OpenConnectionAsync();
        await using var raw = db.Database.GetDbConnection().CreateCommand();
        raw.CommandText = "SELECT \"Email\" FROM \"Users\" LIMIT 1";
        Assert.StartsWith("vouch:v1:", (string)(await raw.ExecuteScalarAsync())!);
        Assert.Equal(["Sincerity"], user.DeepValues);
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401()
    {
        using var client = _factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("unknown@test.ac.lk", "WrongPassword123!"))).StatusCode);
    }

    [Fact]
    public async Task Login_AfterRegistration_ReturnsAccessToken()
    {
        using var client = _factory.Client();
        var request = Registration();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(request.Email, request.Password))).StatusCode);
    }

    [Fact]
    public async Task UnicodeLimits_RoundTripAndTokensContainNoIdentityPii()
    {
        using var client = _factory.Client();
        var request = Registration() with { FullName = new string('ස', 120) };
        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(request.FullName, registered.FullName);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(registered.Token);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type is "email" or "unique_name" || claim.Value == request.FullName || claim.Value == request.Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token);
        await VerifyAsync(client, registered.Email);
        var bio = new string('ස', 280);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/onboarding", new CompleteOnboardingRequest(bio, ["Sincerity"], [IntellectualInterest.Science]))).StatusCode);
        await using var db = postgres.CreateContext();
        var stored = await db.Users.SingleAsync();
        Assert.Equal(bio, stored.Bio);
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"FullName\", \"Bio\", \"DeepValues\", \"Faculty\" FROM \"Users\"";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var i = 0; i < 4; i++) Assert.StartsWith("vouch:v1:", reader.GetString(i));
    }

    [Fact]
    public async Task ConcurrentNormalizedRegistration_ProducesOneAccountAndControlledConflict()
    {
        using var first = _factory.Client("198.51.100.71");
        using var second = _factory.Client("198.51.100.72");
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/auth/register", Registration()),
            second.PostAsJsonAsync("/api/auth/register", Registration() with { Email = "S@TEST.AC.LK" }));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).OrderBy(s => s).ToArray());
        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync());
        using var scope = _factory.Services.CreateScope();
        var login = await scope.ServiceProvider.GetRequiredService<IAuthService>().LoginAsync(new(" S@TEST.AC.LK ", Registration().Password));
        Assert.Equal("s@test.ac.lk", login.Email);
    }

    [Fact]
    public async Task CorruptedIdentity_ReturnsControlledServerErrorWithoutProtectedValues()
    {
        using var client = _factory.Client();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", Registration())).StatusCode);
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"FullName\" = 'vouch:v1:v1:corrupted-test-payload'");
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Registration().Email, Registration().Password));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("corrupted-test-payload", body);
        Assert.DoesNotContain(Registration().Email, body);
    }

    [Fact]
    public async Task ProtectedQueries_ReturnReadableMatchMessageTrustAndArchitectDtos()
    {
        await using var db = postgres.CreateContext();
        User CreateUser(string email, string name) => new()
        {
            CampusId = _campusId, Email = email, FullName = name, PasswordHash = "test-unused", Faculty = "Science", Department = "Computing",
            AcademicYear = 2, Bio = "Readable biography", DeepValues = ["Sincerity"], IntellectualInterests = [IntellectualInterest.Philosophy], Status = AccountStatus.Active,
            EmailVerifiedAt = DateTimeOffset.UtcNow, OnboardingCompletedAt = DateTimeOffset.UtcNow
        };
        var first = CreateUser("first@test.ac.lk", "First Person");
        var second = CreateUser("second@test.ac.lk", "Second Person");
        second.TrustScore = 20;
        db.Users.AddRange(first, second);
        var conversation = new Conversation { UserAId = first.Id, UserBId = second.Id };
        db.Conversations.Add(conversation);
        db.Matches.Add(new DailyMatch { CampusId = _campusId, UserAId = first.Id, UserBId = second.Id, CycleDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await db.SaveChangesAsync();
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var match = (await provider.GetRequiredService<IMatchService>().GetTodayConnectionAsync(first.Id)).Match!;
        Assert.Equal(second.FullName, match.MatchedUserFullName);
        Assert.Equal(second.Bio, match.Bio);
        Assert.Equal(second.Faculty, match.Faculty);
        var messaging = provider.GetRequiredService<IMessagingService>();
        var sent = await messaging.SendMessageAsync(first.Id, conversation.Id, new("A private letter"));
        Assert.Equal(first.FullName, sent.SenderName);
        Assert.Equal("A private letter", (await messaging.GetConversationMessagesAsync(second.Id, conversation.Id)).Items.Single().Body);
        Assert.Equal(second.FullName, (await messaging.GetUserConversationsAsync(first.Id)).Items.Single().OtherUserName);
        var trust = provider.GetRequiredService<ITrustService>();
        await trust.SubmitVouchAsync(first.Id, new(second.Id, CharacterTrait.Sincere, "Private endorsement"));
        Assert.Equal(first.FullName, (await trust.GetUserTrustSummaryAsync(second.Id)).RecentVouches.Single().VoucherName);
        var moderation = provider.GetRequiredService<IModerationService>();
        var report = await moderation.SubmitReportAsync(first.Id, new(second.Id, null, ReportCategory.Harassment, "Private report details"));
        Assert.Equal(first.Email, report.ReporterEmail);
        var dashboard = await moderation.GetArchitectDashboardAsync(_campusId);
        Assert.Equal(second.FullName, dashboard.CriticalReports.Single().ReportedUserName);
        Assert.Equal("Private report details", dashboard.CriticalReports.Single().Details);
        Assert.NotEmpty(dashboard.TrustScoreAnomalies);
        Assert.All(dashboard.TrustScoreAnomalies, anomaly => { Assert.Equal(second.FullName, anomaly.UserName); Assert.Equal(second.Email, anomaly.Email); });
        await db.Database.OpenConnectionAsync();
        await using var raw = db.Database.GetDbConnection().CreateCommand();
        raw.CommandText = "SELECT \"Body\" FROM \"Messages\" UNION ALL SELECT \"Details\" FROM \"Reports\" UNION ALL SELECT \"Note\" FROM \"Vouches\"";
        await using var reader = await raw.ExecuteReaderAsync();
        while (await reader.ReadAsync()) Assert.StartsWith("vouch:v1:", reader.GetString(0));
    }

    [Fact]
    public async Task Permissions_RequireValidJwtAndArchitectRole()
    {
        using var client = _factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/vouches/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/vouches/me")).StatusCode);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(VouchApiFactory.Settings(postgres.Database.ConnectionString)).Build();
        var token = new JwtTokenService(settings).GenerateToken(new User
        {
            CampusId = _campusId, Email = "s@test.ac.lk", PasswordHash = "unused", FullName = "Test",
            Faculty = "Test", Department = "Test", Role = UserRole.Seeker
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/architect/dashboard/{_campusId}")).StatusCode);
    }

    [Fact]
    public async Task RateLimits_ArePerClientAndReturnJsonWithRetryAfter()
    {
        using var first = _factory.Client("198.51.100.10");
        using var second = _factory.Client("198.51.100.20");
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized,
            (await first.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"))).StatusCode);
        var rejected = await first.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal("application/json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await second.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"))).StatusCode);
    }

    [Fact]
    public async Task ForwardedAddress_IsAcceptedOnlyFromConfiguredProxy()
    {
        using var untrusted = _factory.Client("198.51.100.30");
        for (var i = 0; i < 5; i++) await untrusted.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"));
        untrusted.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.40");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await untrusted.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"))).StatusCode);
        using var trusted = _factory.Client("127.0.0.1");
        trusted.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.50");
        for (var i = 0; i < 5; i++) await trusted.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await trusted.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"))).StatusCode);
        trusted.DefaultRequestHeaders.Remove("X-Forwarded-For");
        trusted.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.60");
        Assert.Equal(HttpStatusCode.Unauthorized, (await trusted.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.ac.lk", "wrong"))).StatusCode);
    }

    [Fact]
    public void Harness_DisablesWorkersAndReplacesExternalServices()
    {
        using var client = _factory.Client();
        var workers = _factory.Services.GetServices<IHostedService>();
        Assert.DoesNotContain(workers, worker => worker is AccountDeletionWorker or ConversationInactivityWorker or DailyMatchGenerationWorker);
        Assert.IsType<RecordingEmailService>(_factory.Services.GetRequiredService<IEmailService>());
        Assert.IsType<TestWingmanService>(_factory.Services.GetRequiredService<IAiWingmanService>());
        Assert.IsType<DisabledPhotoStorage>(_factory.Services.GetRequiredService<IPhotoStorageService>());
    }

    private async Task VerifyAsync(HttpClient client, string email)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/verification/request", null)).StatusCode);
        var token = _factory.Services.GetRequiredService<RecordingEmailService>().VerificationTokens[email];
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/verification/confirm", new VerifyEmailRequest(token))).StatusCode);
    }
}
