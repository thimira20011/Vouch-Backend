using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Common;
using Vouch.Application.Features.Auth;
using Vouch.Application.Features.Matching;
using Vouch.Application.Features.Messaging;
using Vouch.Application.Features.Moderation;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.SignalR;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class SessionsAndAccessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private User _alice = null!, _bob = null!, _outsider = null!, _foreign = null!, _architect = null!;
    private Conversation _conversation = null!;
    private Guid _foreignCampus;
    private int _clientNumber;
    private string _photoRoot = null!;
    private const string Password = "SessionTestPassword123!";

    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync();
        await using var db = postgres.CreateContext();
        var first = new Campus { Name = "First", Code = "FIRST", DomainPattern = "@first.ac.lk" };
        var second = new Campus { Name = "Second", Code = "SECOND", DomainPattern = "@second.ac.lk" };
        _foreignCampus = second.Id;
        db.Campuses.AddRange(first, second);
        var hash = new BCryptPasswordHasher().HashPassword(Password);
        User Member(string name, Guid campus) => new()
        {
            CampusId = campus, Email = $"{name}@{(campus == first.Id ? "first" : "second")}.ac.lk", FullName = name,
            PasswordHash = hash, Faculty = "Science", Department = "Computing", AcademicYear = 2,
            Status = AccountStatus.Active, EmailVerifiedAt = DateTimeOffset.UtcNow, OnboardingCompletedAt = DateTimeOffset.UtcNow,
            DeepValues = ["Sincerity"], IntellectualInterests = [IntellectualInterest.Science]
        };
        _alice = Member("alice", first.Id); _bob = Member("bob", first.Id); _outsider = Member("outsider", first.Id);
        _foreign = Member("foreign", second.Id); _architect = Member("architect", first.Id); _architect.Role = UserRole.Architect;
        db.Users.AddRange(_alice, _bob, _outsider, _foreign, _architect);
        _conversation = new Conversation { UserAId = _alice.Id, UserBId = _bob.Id };
        db.Conversations.Add(_conversation);
        db.Matches.Add(new DailyMatch { CampusId = first.Id, UserAId = _alice.Id, UserBId = _bob.Id, CycleDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await db.SaveChangesAsync();
        _photoRoot = Path.Combine(Path.GetTempPath(), "vouch-private-photos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_photoRoot);
        _factory = new(postgres.Database.ConnectionString, new() { ["PhotoStorage:BasePath"] = _photoRoot });
    }
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        Directory.Delete(_photoRoot, recursive: true);
    }
    private HttpClient Client(string? token = null)
    {
        var client = _factory.Client($"198.51.100.{Interlocked.Increment(ref _clientNumber)}");
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
    private async Task<AuthResponse> LoginAsync(User user)
    {
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
    private async Task<AuthResponse> RefreshAsync(string token)
    {
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    [Fact]
    public async Task TokensAreShortLivedMinimalAndRefreshSecretsAreOnlyHashed()
    {
        var auth = await LoginAsync(_alice);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(auth.Token);
        Assert.InRange(jwt.ValidTo - DateTime.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
        Assert.NotNull(auth.RefreshToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "status" or "founding_member" || c.Value == _alice.Email || c.Value == _alice.FullName);
        Assert.Contains(jwt.Claims, c => c.Type == "sid");
        await using var db = postgres.CreateContext();
        var stored = await db.RefreshTokens.SingleAsync();
        Assert.Equal(SingleUseToken.Hash(auth.RefreshToken!), stored.TokenHash);
        Assert.NotEqual(auth.RefreshToken, stored.TokenHash);
        Assert.InRange(auth.RefreshTokenExpiresAt!.Value - auth.AccessTokenExpiresAt!.Value, TimeSpan.FromDays(29), TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task RefreshRotatesAndReuseRevokesTheEntireFamily()
    {
        var original = await LoginAsync(_alice);
        var rotated = await RefreshAsync(original.RefreshToken!);
        Assert.NotEqual(original.RefreshToken, rotated.RefreshToken);
        using var replay = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(original.RefreshToken!))).StatusCode);
        using var authenticated = Client(rotated.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authenticated.GetAsync("/api/auth/identity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(rotated.RefreshToken!))).StatusCode);
        await using var db = postgres.CreateContext();
        Assert.NotNull((await db.AuthSessions.SingleAsync()).RevokedAt);
        Assert.Equal(2, await db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task ParallelRefreshHasOneWinnerAndReplayInvalidatesItsSession()
    {
        var auth = await LoginAsync(_alice);
        using var first = Client(); using var second = Client();
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken!)),
            second.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken!)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);
        var winner = (await responses.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<AuthResponse>())!;
        using var stale = Client(winner.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/auth/identity")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutRevokesAccessAndRefreshWithoutAffectingUnrelatedSessions(bool all)
    {
        var first = await LoginAsync(_alice); var second = await LoginAsync(_alice); var other = await LoginAsync(_bob);
        using var client = Client(first.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(all ? "/api/auth/logout-all" : "/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/identity")).StatusCode);
        using var refresh = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await refresh.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken!))).StatusCode);
        using var secondClient = Client(second.Token); using var otherClient = Client(other.Token);
        Assert.Equal(all ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, (await secondClient.GetAsync("/api/auth/identity")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/auth/identity")).StatusCode);
    }

    [Theory]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.DeletionRequested)]
    public async Task StateChangeRevokesIssuedSessionsEvenAfterReinstatement(AccountStatus status)
    {
        var auth = await LoginAsync(_alice);
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _alice.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status));
        using var client = Client(auth.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/identity")).StatusCode);
        using var login = Client();
        Assert.Contains((await login.PostAsJsonAsync("/api/auth/login", new LoginRequest(_alice.Email, Password))).StatusCode,
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        await db.Users.Where(u => u.Id == _alice.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.Active));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/identity")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await login.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken!))).StatusCode);
    }

    [Fact]
    public async Task IncubationKeepsIdentityOnboardingAndSafetyActionsButNotMessaging()
    {
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _alice.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.InIncubation));
        var auth = await LoginAsync(_alice);
        using var client = Client(auth.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/identity")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/onboarding", new CompleteOnboardingRequest("Bio", ["Sincerity"], [IntellectualInterest.Science]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/conversations/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/moderation/block", new BlockUserRequest(_bob.Id))).StatusCode);
    }

    [Fact]
    public async Task RefreshExpiresAndLegacyTokensWithoutSessionsFail()
    {
        var auth = await LoginAsync(_alice);
        _factory.Clock.Advance(TimeSpan.FromDays(31));
        using var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken!))).StatusCode);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(VouchApiFactory.Settings(postgres.Database.ConnectionString)).Build();
        using var legacy = Client(new JwtTokenService(settings).GenerateToken(_alice));
        Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync("/api/auth/identity")).StatusCode);
    }

    [Fact]
    public async Task DeletedUserAndChangedRoleOrCampusInvalidateAuthentication()
    {
        var auth = await LoginAsync(_outsider);
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _outsider.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Architect));
        using var client = Client(auth.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/identity")).StatusCode);
        var next = await LoginAsync(_outsider);
        await db.Users.Where(u => u.Id == _outsider.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.CampusId, _foreignCampus));
        client.DefaultRequestHeaders.Authorization = new("Bearer", next.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/identity")).StatusCode);
        var foreign = await LoginAsync(_foreign);
        await db.Users.Where(u => u.Id == _foreign.Id).ExecuteDeleteAsync();
        using var deleted = Client(foreign.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await deleted.GetAsync("/api/auth/identity")).StatusCode);
    }

    [Fact]
    public async Task OutsiderCannotReadSendPauseOrSubscribeToAConversation()
    {
        var auth = await LoginAsync(_outsider);
        using var client = Client(auth.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/conversations/{_conversation.Id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/conversations/{_conversation.Id}/messages", new SendMessageRequest("An intrusion"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/conversations/{_conversation.Id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/wingman/icebreakers/conversation/{_conversation.Id}")).StatusCode);
        using var socket = await ConnectAsync(auth.Token);
        var result = await InvokeAsync(socket, "JoinConversation", _conversation.Id.ToString());
        Assert.True(result.TryGetProperty("error", out _));
        Assert.Empty(_factory.Services.GetRequiredService<RealtimeConnections>().Snapshot().Single().Conversations);
    }

    [Fact]
    public async Task CrossCampusTrustVouchesReportsBlocksAndArchitectOperationsAreDenied()
    {
        using var client = Client((await LoginAsync(_alice)).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/vouches/user/{_foreign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/vouches/", new { targetUserId = _foreign.Id, traits = CharacterTrait.Sincere, note = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/moderation/block", new BlockUserRequest(_foreign.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/moderation/report", new CreateReportRequest(_foreign.Id, null, ReportCategory.Harassment, "Cross-campus report"))).StatusCode);
        using var architect = Client((await LoginAsync(_architect)).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await architect.GetAsync($"/api/architect/dashboard/{_foreignCampus}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await architect.PostAsJsonAsync("/api/moderation/invites", new CreateAmbassadorInviteRequest(_foreignCampus, _foreign.Email))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await architect.PostAsync($"/api/architect/ambassadors/{_foreign.Id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await architect.PostAsync($"/api/matches/campus/{_foreignCampus}/generate-daily", null)).StatusCode);
    }

    [Fact]
    public async Task BlockImmediatelyHidesCachedMatchTrustConversationAndLettersInBothDirections()
    {
        using var alice = Client((await LoginAsync(_alice)).Token); using var bob = Client((await LoginAsync(_bob)).Token);
        Assert.True((await alice.GetFromJsonAsync<TodayConnectionResponse>("/api/matches/today"))!.HasMatch);
        Assert.True((await bob.GetFromJsonAsync<TodayConnectionResponse>("/api/matches/today"))!.HasMatch);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/moderation/block", new BlockUserRequest(_bob.Id))).StatusCode);
        foreach (var client in new[] { alice, bob })
        {
            Assert.False((await client.GetFromJsonAsync<TodayConnectionResponse>("/api/matches/today"))!.HasMatch);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/conversations/{_conversation.Id}/messages")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/conversations/{_conversation.Id}/messages", new SendMessageRequest("Blocked letter"))).StatusCode);
            var list = await client.GetFromJsonAsync<PagedResult<ConversationDto>>("/api/conversations/");
            Assert.Empty(list!.Items); Assert.Equal(0, list.TotalCount);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/vouches/user/{_bob.Id}")).StatusCode);
        // Reporting remains available as a safety action after blocking.
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/moderation/report", new CreateReportRequest(_bob.Id, null, ReportCategory.Harassment, "Safety report"))).StatusCode);
    }

    [Fact]
    public async Task ForeignConversationAndUnrelatedMessageReportDoNotLeakData()
    {
        await using var db = postgres.CreateContext();
        var foreignConversation = new Conversation { UserAId = _alice.Id, UserBId = _foreign.Id };
        db.Conversations.Add(foreignConversation);
        var privateMessage = new Message { ConversationId = _conversation.Id, SenderId = _bob.Id, Body = "Private" };
        db.Messages.Add(privateMessage);
        await db.SaveChangesAsync();
        using var client = Client((await LoginAsync(_alice)).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/conversations/{foreignConversation.Id}/messages")).StatusCode);
        using var outsider = Client((await LoginAsync(_outsider)).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/moderation/report", new CreateReportRequest(_bob.Id, privateMessage.Id, ReportCategory.Harassment, "Unrelated message"))).StatusCode);
        Assert.Empty(await db.Reports.ToListAsync());
    }

    [Fact]
    public async Task PhotosRequireSessionCampusBlockAndFullRevealForOriginal()
    {
        Directory.CreateDirectory(Path.Combine(_photoRoot, "original"));
        Directory.CreateDirectory(Path.Combine(_photoRoot, "abstract"));
        byte[] image = [0xff, 0xd8, 0xff, 0xd9];
        await File.WriteAllBytesAsync(Path.Combine(_photoRoot, "original", $"{_bob.Id:N}.jpg"), image);
        await File.WriteAllBytesAsync(Path.Combine(_photoRoot, "abstract", $"{_bob.Id:N}.jpg"), image);
        var original = $"/photos/original/{_bob.Id:N}.jpg"; var abstractUrl = $"/photos/abstract/{_bob.Id:N}.jpg";
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _bob.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.OriginalPhotoUrl, original).SetProperty(u => u.OilPaintingAbstractPhotoUrl, abstractUrl));
        using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(original)).StatusCode);
        using var alice = Client((await LoginAsync(_alice)).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(original)).StatusCode);
        var abstractResponse = await alice.GetAsync(abstractUrl);
        Assert.Equal(HttpStatusCode.OK, abstractResponse.StatusCode);
        Assert.Equal(image, await abstractResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", abstractResponse.Content.Headers.ContentType!.MediaType);
        Assert.True(abstractResponse.Headers.CacheControl!.NoStore);
        await db.Conversations.Where(c => c.Id == _conversation.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentClarityStage, RevealClarityStage.Full100));
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(original)).StatusCode);
        using var foreign = Client((await LoginAsync(_foreign)).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync(abstractUrl)).StatusCode);
        await alice.PostAsJsonAsync("/api/moderation/block", new BlockUserRequest(_bob.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(original)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync(abstractUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/photos/original/not-a-guid.jpg")).StatusCode);
    }

    [Fact]
    public async Task RealtimeSubscriptionsAndDeliveryRecheckBlocksAndRevocation()
    {
        var auth = await LoginAsync(_alice);
        using var socket = await ConnectAsync(auth.Token);
        Assert.False((await InvokeAsync(socket, "JoinConversation", _conversation.Id.ToString())).TryGetProperty("error", out _));
        await using var scope = _factory.Services.CreateAsyncScope();
        var notifications = scope.ServiceProvider.GetRequiredService<ISlowBurnNotificationService>();
        await notifications.NotifyClarityStageUpdatedAsync(_conversation.Id, RevealClarityStage.Quarter25);
        Assert.Equal("ClarityStageUpdated", (await ReceiveAsync(socket)).GetProperty("target").GetString());
        using var client = Client(auth.Token);
        await client.PostAsJsonAsync("/api/moderation/block", new BlockUserRequest(_bob.Id));
        Assert.Empty(_factory.Services.GetRequiredService<RealtimeConnections>().Snapshot().Single().Conversations);
        Assert.True((await InvokeAsync(socket, "JoinConversation", _conversation.Id.ToString())).TryGetProperty("error", out _));
        await notifications.NotifyMessageDeliveredAsync(_alice.Id, new Message { ConversationId = _conversation.Id, SenderId = _bob.Id, Body = "Must not deliver" });
        // Next frame must be the hub completion, never the blocked event.
        Assert.Equal(3, (await InvokeAsync(socket, "LeaveConversation", _conversation.Id.ToString())).GetProperty("type").GetInt32());
        await client.PostAsync("/api/auth/logout", null);
        Assert.Empty(_factory.Services.GetRequiredService<RealtimeConnections>().Snapshot());
    }

    [Fact]
    public async Task DatabaseRevocationPreventsEventsOnPreviouslyConnectedSockets()
    {
        var auth = await LoginAsync(_alice);
        using var socket = await ConnectAsync(auth.Token);
        await InvokeAsync(socket, "JoinConversation", _conversation.Id.ToString());
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _alice.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.Suspended));
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISlowBurnNotificationService>().NotifyClarityStageUpdatedAsync(_conversation.Id, RevealClarityStage.Full100);
        Assert.Empty(_factory.Services.GetRequiredService<RealtimeConnections>().Snapshot());
    }

    [Fact]
    public async Task CampusDeploymentBoundaryRefusesOtherCampusAndSharedPrivateData()
    {
        var boundary = new CampusBoundary(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tenancy:CampusCode"] = "FIRST" }).Build());
        await using var db = postgres.CreateContext();
        await boundary.RequireAsync(db, _alice.CampusId);
        await Assert.ThrowsAsync<Vouch.Application.Common.EligibilityException>(() => boundary.RequireAsync(db, _foreignCampus));
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.VerifyDeploymentAsync(db));
        await db.Users.Where(u => u.CampusId == _foreignCampus).ExecuteDeleteAsync();
        var invite = new AmbassadorInvite { CampusId = _foreignCampus, IssuedByArchitectId = _architect.Id, TokenHash = SingleUseToken.Hash("test-only"), IntendedEmail = "private@second.ac.lk", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.AmbassadorInvites.Add(invite);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.VerifyDeploymentAsync(db));
        await db.AmbassadorInvites.Where(i => i.Id == invite.Id).ExecuteDeleteAsync();
        await boundary.VerifyDeploymentAsync(db);
    }

    [Fact]
    public async Task DashboardAndMessageHistoryExcludeForeignLegacyRecords()
    {
        await using var db = postgres.CreateContext();
        var local = new Report { ReporterId = _alice.Id, ReportedUserId = _bob.Id, Details = "Valid local report", SeverityScore = 5 };
        var foreign = new Report { ReporterId = _foreign.Id, ReportedUserId = _foreign.Id, Details = "Private foreign report", SeverityScore = 5 };
        var malformed = new Report { ReporterId = _foreign.Id, ReportedUserId = _bob.Id, Details = "Foreign reporter identity", SeverityScore = 5 };
        db.Reports.AddRange(local, foreign, malformed);
        db.Messages.AddRange(new Message { ConversationId = _conversation.Id, SenderId = _bob.Id, Body = "Valid member letter" },
            new Message { ConversationId = _conversation.Id, SenderId = _foreign.Id, Body = "Private foreign sender" });
        await db.SaveChangesAsync();
        using var architect = Client((await LoginAsync(_architect)).Token);
        var dashboard = (await architect.GetFromJsonAsync<ArchitectDashboardDto>($"/api/architect/dashboard/{_alice.CampusId}"))!;
        Assert.Equal(1, dashboard.PendingReportsCount);
        Assert.Equal(local.Id, Assert.Single(dashboard.CriticalReports).Id);
        foreach (var id in new[] { foreign.Id, malformed.Id })
            Assert.Equal(HttpStatusCode.Forbidden, (await architect.PostAsJsonAsync($"/api/architect/reports/{id}/resolve", new ResolveReportBody(false, null))).StatusCode);
        using var alice = Client((await LoginAsync(_alice)).Token);
        var messages = (await alice.GetFromJsonAsync<PagedResult<MessageDto>>($"/api/conversations/{_conversation.Id}/messages"))!;
        Assert.Equal(1, messages.TotalCount);
        Assert.Equal("Valid member letter", Assert.Single(messages.Items).Body);
    }

    [Fact]
    public async Task ReadinessRejectsDisabledSessionRevocationTrigger()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        await using var db = postgres.CreateContext();
        try
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" DISABLE TRIGGER vouch_revoke_changed_sessions");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/healthz")).StatusCode);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ENABLE TRIGGER vouch_revoke_changed_sessions"); }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    private async Task<WebSocket> ConnectAsync(string token)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
        var socket = await client.ConnectAsync(new Uri("ws://localhost/hubs/vouch"), CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        var handshake = await ReceiveAsync(socket);
        Assert.False(handshake.TryGetProperty("error", out _));
        return socket;
    }

    [Fact]
    public async Task CachedMatchMustStillBelongToItsCampusAndParticipants()
    {
        using var client = Client((await LoginAsync(_alice)).Token);
        var match = (await client.GetFromJsonAsync<TodayConnectionResponse>("/api/matches/today"))!.Match!;
        await using var db = postgres.CreateContext();
        await db.Matches.Where(m => m.Id == match.MatchId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CampusId, _foreignCampus));
        Assert.False((await client.GetFromJsonAsync<TodayConnectionResponse>("/api/matches/today"))!.HasMatch);
    }

    [Fact]
    public async Task ArchitectAlertsReachOnlyCurrentArchitectsOfTheAffectedCampus()
    {
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _foreign.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Architect));
        using var local = await ConnectAsync((await LoginAsync(_architect)).Token);
        using var foreign = await ConnectAsync((await LoginAsync(_foreign)).Token);
        using var ordinary = await ConnectAsync((await LoginAsync(_outsider)).Token);
        // Invocations also ensure the registry has completed each OnConnected callback.
        await InvokeAsync(local, "LeaveConversation", _conversation.Id.ToString());
        await InvokeAsync(foreign, "LeaveConversation", _conversation.Id.ToString());
        await InvokeAsync(ordinary, "LeaveConversation", _conversation.Id.ToString());
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISlowBurnNotificationService>().NotifyTrustScoreAlertToArchitectAsync(_alice.Id, 0, 4);
        Assert.Equal("TrustScoreAnomalyDetected", (await ReceiveAsync(local)).GetProperty("target").GetString());
        Assert.Equal(3, (await InvokeAsync(foreign, "LeaveConversation", _conversation.Id.ToString())).GetProperty("type").GetInt32());
        Assert.Equal(3, (await InvokeAsync(ordinary, "LeaveConversation", _conversation.Id.ToString())).GetProperty("type").GetInt32());
    }
    private static async Task<JsonElement> InvokeAsync(WebSocket socket, string method, string argument)
    {
        var invocation = JsonSerializer.Serialize(new { type = 1, invocationId = "1", target = method, arguments = new[] { argument } }) + '\u001e';
        await socket.SendAsync(Encoding.UTF8.GetBytes(invocation), WebSocketMessageType.Text, true, CancellationToken.None);
        return await ReceiveAsync(socket);
    }
    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count).TrimEnd('\u001e');
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
