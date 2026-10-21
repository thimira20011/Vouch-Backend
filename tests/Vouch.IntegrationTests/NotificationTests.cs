using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Messaging;
using Vouch.Application.Features.Moderation;
using Vouch.Application.Features.Notifications;
using Vouch.Application.Features.Vouching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Services;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class NotificationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private Guid _campus;
    private User _sender = null!, _recipient = null!, _architect = null!, _second = null!, _foreign = null!, _outsider = null!;
    private Conversation _conversation = null!;
    private readonly List<User> _peers = [];
    private int _clientNumber;
    private DateTimeOffset Now => _factory.Clock.GetUtcNow();
    private RecordingEmailService Mail => _factory.Services.GetRequiredService<RecordingEmailService>();

    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync(); _factory = new(postgres.Database.ConnectionString);
        _factory.Clock.Advance(TimeSpan.FromTicks(-(Now.Ticks % 10)));
        await using var db = postgres.CreateContext();
        var campus = new Campus { Code = "TEST", Name = "Test", DomainPattern = "@test.ac.lk" };
        var foreign = new Campus { Code = "OTHER", Name = "Other", DomainPattern = "@other.ac.lk" }; _campus = campus.Id;
        _sender = Member("sender"); _recipient = Member("recipient"); _architect = Member("architect"); _second = Member("second");
        _foreign = Member("foreign"); _outsider = Member("outsider"); _architect.Role = _second.Role = _foreign.Role = UserRole.Architect; _foreign.CampusId = foreign.Id;
        _peers.AddRange(Enumerable.Range(0, 4).Select(i => Member("peer" + i)));
        _conversation = new() { UserAId = _sender.Id, UserBId = _recipient.Id, LastMessageAt = Now.AddDays(-22) };
        db.Campuses.AddRange(campus, foreign); db.Users.AddRange(_peers.Concat([_sender, _recipient, _architect, _second, _foreign, _outsider]));
        db.Conversations.Add(_conversation); await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() => await _factory.DisposeAsync();
    private User Member(string name) => new()
    {
        CampusId = _campus, Email = name + "@test.ac.lk", FullName = name, PasswordHash = "unused", Status = AccountStatus.Active,
        EmailVerifiedAt = Now, OnboardingCompletedAt = Now, CreatedAt = Now.AddDays(-100), Faculty = "Science", Department = "Computing",
        DeepValues = ["Sincerity"], IntellectualInterests = [IntellectualInterest.Science], AcademicYear = 2
    };
    private async Task<T> UseAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        await using var scope = _factory.Services.CreateAsyncScope(); return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
    private async Task UseAsync<TService>(Func<TService, Task> action) where TService : notnull
    {
        await using var scope = _factory.Services.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<TService>());
    }
    private Task<ReportDto> ReportAsync(ReportCategory category = ReportCategory.Harassment) => UseAsync<IModerationService, ReportDto>(s =>
        s.SubmitReportAsync(_sender.Id, new(_recipient.Id, null, category, "Sensitive report details stay in the dashboard.")));
    private Task<int> DeliverAsync(CancellationToken ct = default) => UseAsync<NotificationDispatcher, int>(s => s.ProcessDueAsync(ct: ct));
    private Task<NotificationPage> InboxAsync(User user) => UseAsync<INotificationService, NotificationPage>(s => s.GetAsync(user.Id));
    private async Task<Notification> NoticeAsync()
    {
        await using var db = postgres.CreateContext(); return await db.Notifications.AsNoTracking().SingleAsync();
    }
    private async Task<HttpClient> ClientAsync(User user)
    {
        await using var db = postgres.CreateContext(); var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        var session = new AuthSession { UserId = user.Id, ExpiresAt = Now.AddDays(30) }; db.AuthSessions.Add(session); await db.SaveChangesAsync();
        var client = _factory.Client($"198.51.100.{++_clientNumber}"); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<IJwtTokenService>().GenerateToken(stored, session.Id)); return client;
    }
    private async Task InjectAsync(string table)
    {
        if (table is not ("Notifications" or "Messages")) throw new ArgumentException("Unsupported injection target.");
        await using var db = postgres.CreateContext();
        var trigger = table == "Notifications" ? "CREATE TRIGGER step12_fail BEFORE INSERT ON \"Notifications\" FOR EACH ROW EXECUTE FUNCTION step12_fail();"
            : "CREATE TRIGGER step12_fail BEFORE INSERT ON \"Messages\" FOR EACH ROW EXECUTE FUNCTION step12_fail();";
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION step12_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected notification failure'; END $$;");
        await db.Database.ExecuteSqlRawAsync(trigger);
    }
    private async Task ClearInjectionAsync(string table)
    {
        if (table is not ("Notifications" or "Messages")) throw new ArgumentException("Unsupported injection target.");
        await using var db = postgres.CreateContext();
        var command = table == "Notifications" ? "DROP TRIGGER step12_fail ON \"Notifications\"; DROP FUNCTION step12_fail();"
            : "DROP TRIGGER step12_fail ON \"Messages\"; DROP FUNCTION step12_fail();";
        await db.Database.ExecuteSqlRawAsync(command);
    }

    [Fact]
    public async Task HighSeverityReportAndEncryptedOutboxPersistBeforeAnyDelivery()
    {
        var report = await ReportAsync(); Assert.Equal(0, Mail.Calls);
        var notice = await NoticeAsync(); Assert.Equal(report.Id, notice.ReportId); Assert.Equal(report.CreatedAt.AddHours(1), notice.DeadlineAt);
        Assert.Null(notice.CompletedAt); Assert.DoesNotContain("Sensitive report", notice.Payload);
        await ReportAsync(ReportCategory.Spam);
        await using var db = postgres.CreateContext(); Assert.Single(await db.Notifications.ToListAsync()); Assert.Equal(2, await db.Reports.CountAsync());
        var raw = await db.Database.SqlQueryRaw<string>("SELECT \"Payload\" AS \"Value\" FROM \"Notifications\"").SingleAsync();
        Assert.StartsWith("vouch:v1:", raw); Assert.DoesNotContain(report.Id.ToString(), raw);
        Assert.Single((await InboxAsync(_architect)).Items); Assert.Empty((await InboxAsync(_sender)).Items);
    }

    [Fact]
    public async Task FailedOutboxInsertRollsBackReportAndVisibility()
    {
        await InjectAsync("Notifications");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => ReportAsync()); }
        finally { await ClearInjectionAsync("Notifications"); }
        await using var db = postgres.CreateContext(); Assert.Empty(await db.Reports.ToListAsync()); Assert.Empty(await db.Notifications.ToListAsync());
        Assert.False((await db.Users.SingleAsync(u => u.Id == _recipient.Id)).IsSoftHiddenFromMatchmaking); Assert.Equal(0, Mail.Calls);
    }

    [Fact]
    public async Task TrustAnomalyOutboxFailureRollsBackTheVouchAndRetryCreatesOneEvent()
    {
        async Task<VouchDto> Submit(User peer) => await UseAsync<ITrustService, VouchDto>(s => s.SubmitVouchAsync(peer.Id, new(_recipient.Id, CharacterTrait.Sincere, null)));
        foreach (var peer in _peers.Take(3)) await Submit(peer);
        await InjectAsync("Notifications");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Submit(_peers[3])); }
        finally { await ClearInjectionAsync("Notifications"); }
        await using (var db = postgres.CreateContext())
        {
            Assert.Equal(3, await db.Vouches.CountAsync()); Assert.Equal(3, (await db.Users.SingleAsync(u => u.Id == _recipient.Id)).TrustScore); Assert.Empty(await db.Notifications.ToListAsync());
        }
        var vouch = await Submit(_peers[3]); var notice = await NoticeAsync(); Assert.Equal("trust:" + vouch.Id, notice.DedupeKey);
        Assert.Equal(4, JsonSerializer.Deserialize<JsonElement>(notice.Payload).GetProperty("newScore").GetDouble());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Submit(_peers[3]));
        Assert.Single((await InboxAsync(_architect)).Items); Assert.Equal(0, Mail.Calls);
    }

    [Fact]
    public async Task OfflineArchitectInboxSurvivesRestartAndReadReceiptsAreIndependent()
    {
        await ReportAsync(); await DeliverAsync(); Assert.Equal(1, Mail.Accepted[_architect.Email]); Assert.Equal(1, Mail.Accepted[_second.Email]);
        await _factory.DisposeAsync(); _factory = new(postgres.Database.ConnectionString);
        var page = await InboxAsync(_architect); var id = page.Items.Single().Id; Assert.Equal(1, page.UnreadCount);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => UseAsync<INotificationService>(s => s.ReadAsync(_architect.Id, id))));
        Assert.Equal(0, (await InboxAsync(_architect)).UnreadCount); Assert.Equal(1, (await InboxAsync(_second)).UnreadCount);
        await using var db = postgres.CreateContext(); Assert.Equal(2, await db.NotificationReceipts.CountAsync());
        Assert.NotNull((await db.NotificationReceipts.SingleAsync(r => r.UserId == _architect.Id)).ReadAt);
        Assert.Equal(0, await DeliverAsync()); Assert.Equal(0, Mail.Calls); // Already accepted before restart.
    }

    [Fact]
    public async Task InboxPaginatesBeyondTwentyAndRequiresOwnershipForRead()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<NotificationWriter>();
            for (var i = 0; i < 25; i++) await writer.QueueTrustAsync(_campus, _recipient.Id, 0, 4, "test-" + i, default);
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync();
        }
        using var architect = await ClientAsync(_architect); using var ordinary = await ClientAsync(_outsider); using var foreign = await ClientAsync(_foreign);
        var first = (await architect.GetFromJsonAsync<NotificationPage>("/api/notifications/?pageSize=20"))!;
        var second = (await architect.GetFromJsonAsync<NotificationPage>("/api/notifications/?page=2&pageSize=20"))!;
        Assert.Equal(25, first.TotalCount); Assert.Equal(25, first.UnreadCount); Assert.Equal(5, second.Items.Count);
        Assert.Equal(25, first.Items.Concat(second.Items).Select(n => n.Id).Distinct().Count());
        var id = first.Items[0].Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.PostAsync($"/api/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsync($"/api/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await architect.PostAsync($"/api/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(24, (await architect.GetFromJsonAsync<NotificationPage>("/api/notifications/?unreadOnly=true"))!.TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await architect.GetAsync("/api/notifications/?pageSize=51")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/notifications/delivery")).StatusCode);
        using var response = await architect.GetAsync("/api/notifications/"); Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task SmtpOutageRecoversBeforeOneHourWithoutDuplicateSourceOrAcceptedEmails()
    {
        await ReportAsync(); var start = Now; Mail.FailDelivery = true; await DeliverAsync();
        while (Now < start.AddMinutes(55))
        {
            var pending = await NoticeAsync(); Assert.Null(pending.CompletedAt); Assert.Equal("SMTP_FAILED", pending.LastErrorCode);
            _factory.Clock.Advance(pending.NextAttemptAt - Now); await DeliverAsync();
        }
        Mail.FailDelivery = false; var next = await NoticeAsync(); _factory.Clock.Advance(next.NextAttemptAt - Now); await DeliverAsync();
        var complete = await NoticeAsync(); Assert.NotNull(complete.EmailCompletedAt); Assert.True(complete.EmailCompletedAt <= complete.DeadlineAt);
        Assert.Null(complete.DeadlineMissedAt); Assert.NotNull(complete.CompletedAt); Assert.Null(complete.LeaseId);
        Assert.Equal(1, Mail.Accepted[_architect.Email]); Assert.Equal(1, Mail.Accepted[_second.Email]); Assert.Equal(0, await DeliverAsync());
        await using var db = postgres.CreateContext(); Assert.Single(await db.Reports.ToListAsync()); Assert.Single(await db.Notifications.ToListAsync()); Assert.Empty(await db.ModerationAudits.ToListAsync());
    }

    [Fact]
    public async Task SuccessfulRecipientIsNotResentWhenAnotherRecipientFails()
    {
        await ReportAsync(); Mail.FailingRecipients[_second.Email] = 0;
        await DeliverAsync(); var notice = await NoticeAsync(); Assert.Null(notice.EmailCompletedAt); Assert.Equal(1, Mail.Accepted[_architect.Email]);
        _factory.Clock.Advance(notice.NextAttemptAt - Now); Mail.FailingRecipients.Clear(); await DeliverAsync();
        Assert.Equal(1, Mail.Accepted[_architect.Email]); Assert.Equal(1, Mail.Accepted[_second.Email]);
        await using var db = postgres.CreateContext(); Assert.Equal(1, (await db.NotificationReceipts.SingleAsync(r => r.UserId == _architect.Id)).EmailAttempts);
        Assert.Equal(2, (await db.NotificationReceipts.SingleAsync(r => r.UserId == _second.Id)).EmailAttempts);
    }

    [Fact]
    public async Task ConcurrentWorkersClaimAnEventOnce()
    {
        await ReportAsync(); var counts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => DeliverAsync()));
        Assert.Equal(1, counts.Sum()); Assert.Equal(1, Mail.Accepted[_architect.Email]); Assert.Equal(1, Mail.Accepted[_second.Email]); Assert.Equal(1, (await NoticeAsync()).Attempts);
    }

    [Fact]
    public async Task CancelledDeliveryIsRecoveredAfterItsLeaseExpires()
    {
        await ReportAsync(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Mail.BeforeDelivery = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var cancellation = new CancellationTokenSource(); var delivery = DeliverAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        var pending = await NoticeAsync(); Assert.NotNull(pending.LeaseUntil); Assert.Empty(Mail.Accepted);
        Mail.BeforeDelivery = null; _factory.Clock.Advance(pending.LeaseUntil!.Value - Now); await DeliverAsync();
        Assert.NotNull((await NoticeAsync()).CompletedAt); Assert.Equal(1, Mail.Accepted[_architect.Email]); Assert.Equal(1, Mail.Accepted[_second.Email]);
    }

    [Fact]
    public async Task MissedEmailDeadlineMakesHealthFailAndRecoveryPreservesTheMiss()
    {
        await ReportAsync(); Mail.FailDelivery = true; _factory.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)); await DeliverAsync();
        using var client = _factory.Client(); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/healthz")).StatusCode);
        Assert.NotNull((await NoticeAsync()).DeadlineMissedAt);
        using var architect = await ClientAsync(_architect);
        var status = (await architect.GetFromJsonAsync<NotificationDeliveryStatus>("/api/notifications/delivery"))!; Assert.Equal(1, status.OverdueEmails);
        var pending = await NoticeAsync(); Mail.FailDelivery = false; _factory.Clock.Advance(pending.NextAttemptAt - Now); await DeliverAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        status = (await architect.GetFromJsonAsync<NotificationDeliveryStatus>("/api/notifications/delivery"))!; Assert.Equal(0, status.PendingEmails); Assert.Equal(1, status.RecoveredLate);
    }

    [Fact]
    public async Task MissingArchitectRetainsWorkUntilAnArchitectBecomesAvailable()
    {
        await ReportAsync();
        await using (var db = postgres.CreateContext())
            await db.Users.Where(u => u.CampusId == _campus && u.Role == UserRole.Architect).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Seeker));
        await DeliverAsync(); var pending = await NoticeAsync(); Assert.Equal("NO_ACTIVE_ARCHITECT", pending.LastErrorCode); Assert.Equal(0, Mail.Calls);
        await using (var db = postgres.CreateContext())
            await db.Users.Where(u => u.Id == _architect.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Architect));
        _factory.Clock.Advance(pending.NextAttemptAt - Now); await DeliverAsync(); Assert.Equal(1, Mail.Accepted[_architect.Email]); Assert.False(Mail.Accepted.ContainsKey(_second.Email));
    }

    [Fact]
    public async Task SourceCampusChangePreventsInboxExposureAndEmailDelivery()
    {
        await ReportAsync();
        await using (var db = postgres.CreateContext())
            await db.Users.Where(u => u.Id == _sender.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.CampusId, _foreign.CampusId));
        Assert.Empty((await InboxAsync(_architect)).Items); Assert.Empty((await InboxAsync(_foreign)).Items);
        await DeliverAsync(); Assert.NotNull((await NoticeAsync()).CancelledAt); Assert.Equal(0, Mail.Calls);
    }

    [Fact]
    public async Task OfflineInactivityNudgesAreAtomicDeduplicatedAndNewMessagesInvalidateTheOldCycle()
    {
        await UseAsync<IMessagingService>(s => s.ProcessInactivityChecksAsync());
        await UseAsync<IMessagingService>(s => s.ProcessInactivityChecksAsync());
        Assert.Single((await InboxAsync(_recipient)).Items); Assert.Single((await InboxAsync(_sender)).Items); Assert.Empty((await InboxAsync(_outsider)).Items);
        await using (var db = postgres.CreateContext()) { Assert.Equal(2, await db.Notifications.CountAsync()); Assert.NotNull((await db.Conversations.SingleAsync()).InactivityNudgeSentAt); }
        await UseAsync<IMessagingService, MessageDto>(s => s.SendMessageAsync(_sender.Id, _conversation.Id, new("A new thoughtful letter.")));
        Assert.Empty((await InboxAsync(_recipient)).Items); await DeliverAsync();
        _factory.Clock.Advance(TimeSpan.FromDays(22)); await UseAsync<IMessagingService>(s => s.ProcessInactivityChecksAsync());
        Assert.Single((await InboxAsync(_recipient)).Items);
        await using var check = postgres.CreateContext(); Assert.Equal(4, await check.Notifications.CountAsync()); Assert.Equal(2, await check.Notifications.CountAsync(n => n.CancelledAt != null));
    }

    [Fact]
    public async Task FailedInactivityOutboxInsertRollsBackItsMarker()
    {
        await InjectAsync("Notifications");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => UseAsync<IMessagingService>(s => s.ProcessInactivityChecksAsync())); }
        finally { await ClearInjectionAsync("Notifications"); }
        await using var db = postgres.CreateContext(); Assert.Null((await db.Conversations.SingleAsync()).InactivityNudgeSentAt); Assert.Empty(await db.Notifications.ToListAsync());
        await UseAsync<IMessagingService>(s => s.ProcessInactivityChecksAsync()); Assert.Equal(2, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task BlockedInactivityNoticesCannotBeReadOrPublished()
    {
        await UseAsync<IMessagingService>(s => s.ProcessInactivityChecksAsync()); var notice = (await InboxAsync(_recipient)).Items.Single();
        await UseAsync<IModerationService, bool>(s => s.BlockUserAsync(_sender.Id, new(_recipient.Id)));
        Assert.Empty((await InboxAsync(_recipient)).Items);
        await Assert.ThrowsAsync<EligibilityException>(() => UseAsync<INotificationService>(s => s.ReadAsync(_recipient.Id, notice.Id)));
        await DeliverAsync(); await using var db = postgres.CreateContext(); Assert.Equal(2, await db.Notifications.CountAsync(n => n.CancelledAt != null));
    }

    [Fact]
    public async Task RemovingAReportCascadesItsNotificationAndReceipts()
    {
        await ReportAsync(); await DeliverAsync();
        await using var db = postgres.CreateContext(); Assert.Equal(2, await db.NotificationReceipts.CountAsync());
        db.Reports.Remove(await db.Reports.SingleAsync()); await db.SaveChangesAsync();
        Assert.Empty(await db.Notifications.ToListAsync()); Assert.Empty(await db.NotificationReceipts.ToListAsync());
    }

    [Fact]
    public async Task PreviousSchemaUpgradePreservesReportsWithoutInventingDeliveryHistory()
    {
        await using var previous = new IsolatedPostgresDatabase(Environment.GetEnvironmentVariable("VOUCH_TEST_POSTGRES_ADMIN")!);
        await previous.CreateAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(previous.ConnectionString).Options;
        await using (var db = new ApplicationDbContext(options, PostgresFixture.Encryption, PostgresFixture.Lookup))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261011065511_AuditedModerationReview");
            await PreviousSchemaData.CopyAsync(postgres, db, "Campuses", "Users", "ProtectionStates");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Reports" ("Id", "ReporterId", "ReportedUserId", "Category", "Details", "SeverityScore", "Status", "CreatedAt")
                VALUES ({Guid.NewGuid()}, {_sender.Id}, {_recipient.Id}, 1, {PostgresFixture.Encryption.Encrypt("Legacy report details.", "Reports.Details")}, 5, 1, {Now})
                """);
        }
        await using var upgraded = new ApplicationDbContext(options, PostgresFixture.Encryption, PostgresFixture.Lookup);
        Assert.True((await new DatabaseUpgrade(upgraded, PostgresFixture.Encryption, PostgresFixture.Lookup).ApplyAsync(backupConfirmed: true)).CanApply);
        Assert.Single(await upgraded.Reports.ToListAsync()); Assert.Empty(await upgraded.Notifications.ToListAsync()); Assert.Empty(await upgraded.NotificationReceipts.ToListAsync());
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task FailedMessageSaveDoesNotPublishAClarityChange()
    {
        await using (var db = postgres.CreateContext())
        {
            var conversation = await db.Conversations.SingleAsync(); conversation.QualifyingMessageCount = 15; conversation.AdaptiveRevealThreshold = 40; await db.SaveChangesAsync();
        }
        using var recipient = await ClientAsync(_recipient); var socketClient = _factory.Server.CreateWebSocketClient();
        socketClient.ConfigureRequest = request => request.Headers.Authorization = recipient.DefaultRequestHeaders.Authorization!.ToString();
        using var socket = await socketClient.ConnectAsync(new Uri("ws://localhost/hubs/vouch"), default);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, default); await ReceiveAsync(socket);
        await InvokeAsync(socket, "JoinConversation"); await InjectAsync("Messages");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => UseAsync<IMessagingService, MessageDto>(s => s.SendMessageAsync(_sender.Id, _conversation.Id, new("A thoughtful new letter.")))); }
        finally { await ClearInjectionAsync("Messages"); }
        Assert.Equal(3, (await InvokeAsync(socket, "LeaveConversation")).GetProperty("type").GetInt32());
        await using var check = postgres.CreateContext(); Assert.Empty(await check.Messages.ToListAsync()); Assert.Equal(15, (await check.Conversations.SingleAsync()).QualifyingMessageCount);
    }
    private async Task<JsonElement> InvokeAsync(WebSocket socket, string method)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = 1, invocationId = "1", target = method, arguments = new[] { _conversation.Id.ToString() } }) + '\u001e'), WebSocketMessageType.Text, true, default);
        return await ReceiveAsync(socket);
    }
    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var buffer = new byte[8192];
        var frame = await socket.ReceiveAsync(buffer, timeout.Token); using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, frame.Count).TrimEnd('\u001e'));
        return document.RootElement.Clone();
    }
}
