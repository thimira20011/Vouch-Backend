using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vouch.Api.Configuration;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Auth;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Services;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class AccountDeletionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private User _victim = null!, _peer = null!, _architect = null!, _foreign = null!;
    private Campus _campus = null!;
    private int _clientNumber;
    private DateTimeOffset Now => _factory.Clock.GetUtcNow();
    private User Member(string name, Guid campus, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), CampusId = campus, Email = name + "@erase.ac.lk", FullName = name,
        PasswordHash = "unused", Faculty = "Science", Department = "Computing", AcademicYear = 2,
        Status = AccountStatus.Active, EmailVerifiedAt = Now, OnboardingCompletedAt = Now,
        DeepValues = ["Sincerity"], IntellectualInterests = [IntellectualInterest.Science], CreatedAt = Now.AddDays(-60)
    };
    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync();
        _factory = new(postgres.Database.ConnectionString);
        _factory.Clock.Advance(TimeSpan.FromTicks(-(Now.Ticks % 10)));
        await using var db = postgres.CreateContext();
        _campus = new Campus { Name = "Erasure", Code = "ERASE", DomainPattern = "@erase.ac.lk", RequiredAmbassadorsForLaunch = 1, RequiredVouchesPerAmbassador = 0 };
        var other = new Campus { Name = "Other", Code = "OTHER", DomainPattern = "@other.ac.lk" };
        db.Campuses.AddRange(_campus, other);
        _victim = Member("victim", _campus.Id); _peer = Member("peer", _campus.Id);
        _architect = Member("architect", _campus.Id); _architect.Role = UserRole.Architect;
        _foreign = Member("foreign", other.Id); _foreign.Role = UserRole.Architect;
        db.Users.AddRange(_victim, _peer, _architect, _foreign); await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() => await _factory.DisposeAsync();
    private async Task<AccountDeletionResponse> Request(Guid? id = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAuthService>().RequestAccountDeletionAsync(id ?? _victim.Id);
    }
    private async Task<int> Process(CancellationToken ct = default)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AccountDeletionProcessor>().ProcessDueAsync(ct: ct);
    }
    private async Task<HttpClient> Client(User user)
    {
        var client = _factory.Client($"198.51.100.{Interlocked.Increment(ref _clientNumber)}");
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var session = new AuthSession { UserId = user.Id, ExpiresAt = Now.AddDays(30) };
        db.AuthSessions.Add(session); await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", scope.ServiceProvider.GetRequiredService<IJwtTokenService>().GenerateToken(user, session.Id));
        return client;
    }
    private async Task<HealthStatus> Health() => (await ActivatorUtilities.CreateInstance<AccountDeletionHealthCheck>(_factory.Services)
        .CheckHealthAsync(new HealthCheckContext())).Status;
    private AccountDeletionProcessor Processor(ApplicationDbContext db) => new(db, _factory.Clock, NullLogger<AccountDeletionProcessor>.Instance);

    [Fact]
    public async Task RequestReturnsOriginalDeadlineRevokesSessionAndCannotBeExtended()
    {
        using var client = await Client(_victim);
        var response = await client.PostAsync("/api/auth/delete-account", null); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var first = (await response.Content.ReadFromJsonAsync<AccountDeletionResponse>())!;
        Assert.Equal(Now, first.RequestedAt); Assert.Equal(Now.AddDays(30), first.DeadlineAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/identity")).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromDays(2));
        var repeated = await Request(); Assert.Equal(first, repeated);
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _victim.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletionRequestedAt, (DateTimeOffset?)Now).SetProperty(u => u.Status, AccountStatus.Active));
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == _victim.Id);
        Assert.Equal(first.RequestedAt, user.DeletionRequestedAt); Assert.Equal(AccountStatus.DeletionRequested, user.Status);
        await db.Users.Where(u => u.Id == _victim.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletionRequestedAt, (DateTimeOffset?)null));
        Assert.Equal(first.RequestedAt, await db.Users.Where(u => u.Id == _victim.Id).Select(u => u.DeletionRequestedAt).SingleAsync());
        Assert.Single(await db.AccountDeletions.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRequestsAndWorkersProduceOneCompletion()
    {
        var requests = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Request()));
        Assert.Single(requests.Select(r => r.RequestId).Distinct());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Process())); Assert.Equal(1, attempts.Sum());
        await using var db = postgres.CreateContext(); var job = await db.AccountDeletions.SingleAsync();
        Assert.Equal(1, job.Attempts); Assert.NotNull(job.CompletedAt); Assert.Null(job.UserId); Assert.Null(job.LeaseId);
        Assert.False(await db.Users.AnyAsync(u => u.Id == _victim.Id)); Assert.Equal(0, await Process());
    }

    [Fact]
    public async Task EnabledWorkerProcessesRequestsOnStartupWithoutWaitingForTheDeadline()
    {
        await Request();
        await using var running = new VouchApiFactory(postgres.Database.ConnectionString,
            new Dictionary<string, string?> { ["BackgroundJobs:Enabled"] = "true" });
        using var client = running.Client(); await using var db = postgres.CreateContext();
        for (var i = 0; i < 250 && await db.Users.AnyAsync(u => u.Id == _victim.Id); i++) await Task.Delay(20);
        Assert.False(await db.Users.AnyAsync(u => u.Id == _victim.Id));
        Assert.NotNull((await db.AccountDeletions.SingleAsync()).CompletedAt);
        Assert.Equal(0, running.Services.GetRequiredService<RecordingEmailService>().Calls);
    }

    private async Task SeedGraph()
    {
        await using var db = postgres.CreateContext();
        var conversation = new Conversation { UserAId = _victim.Id, UserBId = _peer.Id };
        var untouched = new Conversation { UserAId = _peer.Id, UserBId = _architect.Id };
        db.Conversations.AddRange(conversation, untouched);
        var sent = new Message { ConversationId = conversation.Id, SenderId = _victim.Id, Body = "private victim message" };
        var received = new Message { ConversationId = conversation.Id, SenderId = _peer.Id, Body = "private partner message" };
        db.Messages.AddRange(sent, received, new Message { ConversationId = untouched.Id, SenderId = _peer.Id, Body = "keep this unrelated letter" });
        var report = new Report { ReporterId = _victim.Id, ReportedUserId = _peer.Id, RelatedMessageId = received.Id,
            Category = ReportCategory.Other, Details = "private report", Status = ReportStatus.Upheld, ResolvedAt = Now };
        db.Reports.Add(report);
        db.ModerationAudits.Add(new ModerationAudit { CampusId = _campus.Id, ArchitectId = _victim.Id, SubjectUserId = _peer.Id,
            Action = ModerationAction.ReportUpheld, PreviousStatus = AccountStatus.Active, ResultingStatus = AccountStatus.Active, Reason = "private actor reason" });
        db.Vouches.Add(new VouchRecord { TargetUserId = _peer.Id, VoucherUserId = _victim.Id, Traits = CharacterTrait.Sincere, FinalCalculatedWeight = 1 });
        db.VouchRequests.Add(new PeerVouchRequest { RequesterId = _peer.Id, RequestedVoucherId = _victim.Id, ExpiresAt = Now.AddDays(7) });
        db.Matches.Add(new DailyMatch { CampusId = _campus.Id, UserAId = _victim.Id, UserBId = _peer.Id, CycleDate = DateOnly.FromDateTime(Now.UtcDateTime) });
        db.Blocks.Add(new Block { BlockerId = _victim.Id, BlockedUserId = _architect.Id });
        foreach (var kind in new[] { 0, 1, 2 }) db.AmbassadorInvites.Add(new AmbassadorInvite { CampusId = _campus.Id,
            IssuedByArchitectId = kind == 0 ? _victim.Id : _architect.Id, RedeemedByUserId = kind == 1 ? _victim.Id : null,
            IntendedEmail = kind == 2 ? _victim.Email : $"invite{kind}@erase.ac.lk", TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), ExpiresAt = Now.AddDays(7) });
        var peer = await db.Users.SingleAsync(u => u.Id == _peer.Id); peer.Role = UserRole.Ambassador;
        peer.AmbassadorApprovedByArchitectId = _victim.Id; peer.AmbassadorApprovedAt = Now;
        var session = new AuthSession { UserId = _victim.Id, ExpiresAt = Now.AddDays(30) }; db.AuthSessions.Add(session);
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = new string('a', 64) });
        db.EmailVerifications.Add(new EmailVerification { UserId = _victim.Id, CampusId = _campus.Id, EmailLookupHash = PostgresFixture.Lookup.Hash(_victim.Email),
            TokenHash = new string('b', 64), ExpiresAt = Now.AddMinutes(30) });
        var photo = new PhotoAsset { OwnerId = _victim.Id, Width = 1, Height = 1, OriginalJpeg = "", AbstractJpeg = "", QuarterJpeg = "", SixtyJpeg = "" };
        photo.OriginalJpeg = PhotoPayload.Encode([255, 216, 255, 217], _victim.Id, photo.Id, RevealClarityStage.Full100);
        photo.AbstractJpeg = PhotoPayload.Encode([255, 216, 255, 217], _victim.Id, photo.Id, RevealClarityStage.Abstract0);
        photo.QuarterJpeg = PhotoPayload.Encode([255, 216, 255, 217], _victim.Id, photo.Id, RevealClarityStage.Quarter25);
        photo.SixtyJpeg = PhotoPayload.Encode([255, 216, 255, 217], _victim.Id, photo.Id, RevealClarityStage.Sixty60); db.PhotoAssets.Add(photo);
        var notice = new Notification { CampusId = _campus.Id, SubjectUserId = _victim.Id, RecipientUserId = _peer.Id,
            ConversationId = conversation.Id, Kind = NotificationKind.InactivityNudge, Payload = "{}", DedupeKey = "erase-notice", NextAttemptAt = Now };
        db.Notifications.Add(notice); db.NotificationReceipts.Add(new NotificationReceipt { NotificationId = notice.Id, UserId = _peer.Id });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PromptDeletionErasesTheWholeOwnedGraphAndRepairsSurvivors()
    {
        await SeedGraph(); await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.Users.Where(u => u.Id == _peer.Id).Select(u => u.UpheldReportsCount).SingleAsync());
        Assert.True(await db.Campuses.Where(c => c.Id == _campus.Id).Select(c => c.IsSoftLaunchUnlocked).SingleAsync());
        var request = await Request(); Assert.Equal(1, await Process());
        Assert.False(await db.Users.AnyAsync(u => u.Id == _victim.Id));
        Assert.Empty(await db.PhotoAssets.ToListAsync()); Assert.Empty(await db.RefreshTokens.ToListAsync()); Assert.Empty(await db.AuthSessions.ToListAsync());
        Assert.Empty(await db.EmailVerifications.ToListAsync()); Assert.Empty(await db.Vouches.ToListAsync()); Assert.Empty(await db.VouchRequests.ToListAsync());
        Assert.Empty(await db.Reports.ToListAsync()); Assert.Empty(await db.ModerationAudits.ToListAsync()); Assert.Empty(await db.AmbassadorInvites.ToListAsync());
        Assert.Empty(await db.Matches.ToListAsync()); Assert.Empty(await db.Blocks.ToListAsync()); Assert.Empty(await db.Notifications.ToListAsync()); Assert.Empty(await db.NotificationReceipts.ToListAsync());
        Assert.Single(await db.Conversations.ToListAsync()); Assert.Equal("keep this unrelated letter", (await db.Messages.SingleAsync()).Body);
        var peer = await db.Users.AsNoTracking().SingleAsync(u => u.Id == _peer.Id);
        Assert.Equal(0, peer.TrustScore); Assert.Equal(0, peer.ActiveVouchesReceivedCount); Assert.Equal(0, peer.UpheldReportsCount);
        Assert.Null(peer.AmbassadorApprovedByArchitectId); Assert.Null(peer.AmbassadorApprovedAt);
        Assert.False(await db.Campuses.Where(c => c.Id == _campus.Id).Select(c => c.IsSoftLaunchUnlocked).SingleAsync());
        var job = await db.AccountDeletions.SingleAsync(); Assert.Equal(request.RequestId, job.Id);
        Assert.True(job.CompletedAt < job.DeadlineAt); Assert.Null(job.UserId); Assert.Null(job.DeadlineMissedAt);
    }

    [Theory]
    [InlineData("Messages", "DELETE")]
    [InlineData("Users", "DELETE")]
    [InlineData("AccountDeletions", "UPDATE")]
    public async Task FailureRollsBackEveryDeletionAndRetriesWithoutExtendingDeadline(string table, string operation)
    {
        await SeedGraph(); var request = await Request(); await using var db = postgres.CreateContext();
        Assert.Contains(table, new[] { "Messages", "Users", "AccountDeletions" }); Assert.Contains(operation, new[] { "DELETE", "UPDATE" });
        var guard = table == "AccountDeletions" ? "WHEN (NEW.\"CompletedAt\" IS NOT NULL)" : "";
        // DDL identifiers are fixed theory values, never request/user input.
        var setupSql = $"CREATE FUNCTION erase_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected-private-provider-error'; END; $$; CREATE TRIGGER erase_test_fail BEFORE {operation} ON \"{table}\" FOR EACH ROW {guard} EXECUTE FUNCTION erase_test_fail();";
        var cleanupSql = $"DROP TRIGGER erase_test_fail ON \"{table}\"; DROP FUNCTION erase_test_fail();";
        await db.Database.ExecuteSqlRawAsync(setupSql);
        try
        {
            Assert.Equal(1, await Process());
            Assert.True(await db.Users.AnyAsync(u => u.Id == _victim.Id)); Assert.Equal(3, await db.Messages.CountAsync()); Assert.Single(await db.PhotoAssets.ToListAsync());
            Assert.Single(await db.Reports.ToListAsync()); Assert.Single(await db.Notifications.ToListAsync());
            var failed = await db.AccountDeletions.AsNoTracking().SingleAsync(); Assert.Null(failed.CompletedAt); Assert.Null(failed.LeaseId);
            Assert.Equal("DELETION_FAILED", failed.LastErrorCode); Assert.Equal(1, failed.Attempts); Assert.Equal(request.DeadlineAt, failed.DeadlineAt);
            Assert.Equal(0, await Process());
        }
        finally { await db.Database.ExecuteSqlRawAsync(cleanupSql); }
        _factory.Clock.Advance(TimeSpan.FromMinutes(1)); Assert.Equal(1, await Process());
        var completed = await db.AccountDeletions.AsNoTracking().SingleAsync(); Assert.NotNull(completed.CompletedAt); Assert.Equal(2, completed.Attempts);
        Assert.Equal(request.DeadlineAt, completed.DeadlineAt); Assert.False(await db.Users.AnyAsync(u => u.Id == _victim.Id));
    }

    [Theory]
    [InlineData(0, HealthStatus.Healthy, false)]
    [InlineData(1, HealthStatus.Degraded, false)]
    [InlineData(29.999, HealthStatus.Degraded, false)]
    [InlineData(30, HealthStatus.Unhealthy, true)]
    [InlineData(31, HealthStatus.Unhealthy, true)]
    public async Task DeadlineBoundariesAndRecoveryRetainAccurateEvidence(double days, HealthStatus health, bool missed)
    {
        await using var db = postgres.CreateContext(); var requested = Now.AddDays(-days);
        requested = requested.AddTicks(-(requested.Ticks % 10));
        await db.Users.Where(u => u.Id == _victim.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.DeletionRequested).SetProperty(u => u.DeletionRequestedAt, requested));
        var request = await Request(); Assert.Equal(requested, request.RequestedAt); Assert.Equal(health, await Health());
        Assert.Equal(1, await Process()); var job = await db.AccountDeletions.SingleAsync();
        Assert.Equal(missed, job.DeadlineMissedAt != null); Assert.Equal(HealthStatus.Healthy, await Health());
        if (!missed) Assert.True(job.CompletedAt < job.DeadlineAt);
    }

    [Fact]
    public async Task CancellationRollsBackAndAnExpiredLeaseCanRecoverAfterRestart()
    {
        await SeedGraph(); await Request(); await using var held = postgres.CreateContext();
        await using var tx = await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Campuses\" WHERE \"Id\"={_campus.Id} FOR UPDATE");
        using var cancellation = new CancellationTokenSource(); var processing = Process(cancellation.Token);
        await using var observe = postgres.CreateContext();
        for (var i = 0; i < 100 && !await observe.AccountDeletions.AnyAsync(d => d.Attempts == 1); i++) await Task.Delay(20);
        Assert.True(await observe.AccountDeletions.AnyAsync(d => d.Attempts == 1)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.True(await observe.Users.AnyAsync(u => u.Id == _victim.Id)); Assert.Equal(3, await observe.Messages.CountAsync());
        Assert.NotNull((await observe.AccountDeletions.AsNoTracking().SingleAsync()).LeaseId);
        await tx.RollbackAsync(); _factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await using var restarted = new VouchApiFactory(postgres.Database.ConnectionString);
        // Its clock can differ from the controlled original host, so use the recovered scope with the original test clock.
        using var scope = restarted.Services.CreateScope();
        await Processor(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()).ProcessDueAsync();
        Assert.False(await observe.Users.AnyAsync(u => u.Id == _victim.Id));
    }

    [Fact]
    public async Task DirectStatusChangesAreReconciledAndReceiptIdentityExpires()
    {
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _victim.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.DeletionRequested));
        Assert.Equal(HealthStatus.Degraded, await Health()); Assert.Equal(1, await Process());
        await using var raw = new NpgsqlConnection(postgres.Database.ConnectionString); await raw.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"BackupSubjectId\" FROM \"AccountDeletions\"", raw);
        Assert.StartsWith("vouch:v1:", (string)(await command.ExecuteScalarAsync())!);
        var receipt = await db.AccountDeletions.AsNoTracking().SingleAsync(); Assert.Null(receipt.UserId); Assert.Equal(_victim.Id, DeletionTombstone.Decode(receipt.BackupSubjectId!, receipt.Id));
        _factory.Clock.Advance(TimeSpan.FromDays(30)); await Process(); Assert.Null((await db.AccountDeletions.AsNoTracking().SingleAsync()).BackupSubjectId);
        _factory.Clock.Advance(TimeSpan.FromDays(60)); await Process(); Assert.Empty(await db.AccountDeletions.ToListAsync());
    }

    [Fact]
    public async Task ArchitectStatusIsCampusScopedAndOrdinaryUsersCannotReadIt()
    {
        await Request(); using var ordinary = await Client(_peer); using var local = await Client(_architect); using var foreign = await Client(_foreign);
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/architect/deletions/status")).StatusCode);
        Assert.Equal(1, (await local.GetFromJsonAsync<AccountDeletionStatus>("/api/architect/deletions/status"))!.Pending);
        Assert.Equal(0, (await foreign.GetFromJsonAsync<AccountDeletionStatus>("/api/architect/deletions/status"))!.Pending);
    }

    [Fact]
    public async Task DeletedAccountsAreErasedAgainWhenAnOlderDatabaseIsRestored()
    {
        await Request(); await Process(); await using var source = postgres.CreateContext(); var ledger = await Processor(source).ExportRestoreLedgerAsync();
        Assert.DoesNotContain(_victim.Id.ToString("N"), ledger);
        await using var restored = new IsolatedPostgresDatabase(Environment.GetEnvironmentVariable("VOUCH_TEST_POSTGRES_ADMIN")!); await restored.CreateAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(restored.ConnectionString).Options, PostgresFixture.Encryption, PostgresFixture.Lookup);
        await new DatabaseUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).ApplyAsync();
        db.Campuses.Add(new Campus { Id = _campus.Id, Name = "Restored", Code = "RESTORED", DomainPattern = "@erase.ac.lk" });
        db.Users.AddRange(Member("victim", _campus.Id, _victim.Id), Member("unrelated", _campus.Id)); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(1, await Processor(db).ReplayRestoreLedgerAsync(ledger)); Assert.Equal(0, await Processor(db).ReplayRestoreLedgerAsync(ledger));
        Assert.Single(await db.Users.ToListAsync()); Assert.False(await db.Users.AnyAsync(u => u.Id == _victim.Id));
        await Assert.ThrowsAsync<Vouch.Infrastructure.Security.ProtectedDataException>(() => Processor(db).ReplayRestoreLedgerAsync(ledger[..^4] + "AAAA"));
    }

    [Fact]
    public async Task LedgerCommandsRefuseOverwriteAndRequireBackupAcknowledgement()
    {
        await Request();
        var path = Path.Combine(Path.GetTempPath(), "vouch-erasure-test-" + Guid.NewGuid().ToString("N") + ".ledger");
        var settings = VouchApiFactory.Settings(postgres.Database.ConnectionString); settings["database-task"] = "export-deletions"; settings["deletion-ledger-path"] = path;
        try
        {
            Assert.Equal(0, await DatabaseOperations.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
            var exported = await File.ReadAllTextAsync(path); Assert.StartsWith("vouch:v1:", exported);
            Assert.Equal(1, await DatabaseOperations.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
            Assert.Equal(exported, await File.ReadAllTextAsync(path));
            settings["database-task"] = "replay-deletions";
            Assert.Equal(1, await DatabaseOperations.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
            settings["backup-confirmed"] = "true";
            Assert.Equal(0, await DatabaseOperations.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingDeletionTriggerFailsReadiness()
    {
        await using var db = postgres.CreateContext(); await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" DISABLE TRIGGER vouch_preserve_deletion_request");
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadinessCheck.VerifyAsync(db, PostgresFixture.Lookup, PostgresFixture.Encryption)); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Users\" ENABLE TRIGGER vouch_preserve_deletion_request"); }
    }

    [Fact]
    public async Task PreviousSchemaBackfillsLegacyRequestsAndPreservesTheirDeadline()
    {
        await using var legacy = new IsolatedPostgresDatabase(Environment.GetEnvironmentVariable("VOUCH_TEST_POSTGRES_ADMIN")!); await legacy.CreateAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(legacy.ConnectionString).Options, PostgresFixture.Encryption, PostgresFixture.Lookup);
        await db.GetService<IMigrator>().MigrateAsync("20261016152507_PrivateDatabasePhotos");
        db.ProtectionStates.Add(new ProtectionState { Id = 1, CompletedAt = Now,
            LookupKeyCheck = PostgresFixture.Lookup.Hash(DatabaseReadinessCheck.LookupCheckEmail),
            EncryptionCheck = PostgresFixture.Encryption.Encrypt(DatabaseReadinessCheck.EncryptionCheckValue, "ProtectionStates.EncryptionCheck") });
        await db.SaveChangesAsync();
        db.Campuses.Add(new Campus { Id = _campus.Id, Name = "Legacy", Code = "LEGACY", DomainPattern = "@erase.ac.lk" });
        var user = Member("legacy", _campus.Id); user.Status = AccountStatus.DeletionRequested; user.DeletionRequestedAt = null;
        db.Users.Add(user); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await new DatabaseUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).ApplyAsync(backupConfirmed: true);
        _factory.Clock.Advance(TimeSpan.FromMinutes(1)); // Migration due time comes from the database's real clock.
        var job = await db.AccountDeletions.AsNoTracking().SingleAsync(); Assert.True(job.RequestTimeInferred);
        Assert.Equal(user.CreatedAt, job.RequestedAt); Assert.Equal(user.CreatedAt.AddDays(30), job.DeadlineAt);
        Assert.True((await new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync()).CanApply);
        await Processor(db).ProcessDueAsync(); Assert.False(await db.Users.AnyAsync()); Assert.NotNull((await db.AccountDeletions.AsNoTracking().SingleAsync()).DeadlineMissedAt);
        await Assert.ThrowsAsync<NotSupportedException>(() => db.GetService<IMigrator>().MigrateAsync("20261016152507_PrivateDatabasePhotos"));
    }

    [Fact]
    public async Task CompletedRestoreIdentifiersCannotBeCopiedBetweenRequests()
    {
        await Request(); await Request(_peer.Id); await Process(); await using var db = postgres.CreateContext();
        var jobs = await db.AccountDeletions.AsNoTracking().OrderBy(d => d.Id).ToArrayAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AccountDeletions\" SET \"BackupSubjectId\"=(SELECT \"BackupSubjectId\" FROM \"AccountDeletions\" WHERE \"Id\"={jobs[0].Id}) WHERE \"Id\"={jobs[1].Id}");
        await Assert.ThrowsAsync<Vouch.Infrastructure.Security.ProtectedDataException>(() => Processor(db).ExportRestoreLedgerAsync());
        var report = await new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync();
        Assert.Contains(report.Issues, i => i.Table == "AccountDeletions" && i.RowId == jobs[1].Id && i.Column == "BackupSubjectId");
    }

    [Fact]
    public async Task RestoreLedgerExpiresAtTheOriginalDeadlineAndCannotExtendIdentityRetention()
    {
        await using var db = postgres.CreateContext(); var requested = Now.AddDays(-30).AddMinutes(1);
        await db.Users.Where(u => u.Id == _victim.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.DeletionRequested)
            .SetProperty(u => u.DeletionRequestedAt, requested));
        await Request(); await Process(); var ledger = await Processor(db).ExportRestoreLedgerAsync();
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<ArgumentException>(() => Processor(db).ReplayRestoreLedgerAsync(ledger));
        Assert.Equal(HealthStatus.Unhealthy, await Health()); // Expired tombstone still needs its scheduled purge.
        await Process(); Assert.Null((await db.AccountDeletions.AsNoTracking().SingleAsync()).BackupSubjectId);
        Assert.Equal(HealthStatus.Healthy, await Health());
    }

    [Fact]
    public async Task RestoreReplayCannotSucceedWhileSnapshotDeletionIntentsStillFail()
    {
        await using var db = postgres.CreateContext(); var ledger = await Processor(db).ExportRestoreLedgerAsync();
        await Request(); // A restored snapshot can contain intent not represented by this ledger.
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION restore_test_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END; $$; CREATE TRIGGER restore_test_fail BEFORE DELETE ON \"Users\" FOR EACH ROW EXECUTE FUNCTION restore_test_fail();");
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => Processor(db).ReplayRestoreLedgerAsync(ledger)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER restore_test_fail ON \"Users\"; DROP FUNCTION restore_test_fail();"); }
        Assert.True(await db.Users.AnyAsync(u => u.Id == _victim.Id)); _factory.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await Processor(db).ReplayRestoreLedgerAsync(ledger));
        Assert.False(await db.Users.AnyAsync(u => u.Id == _victim.Id));
    }
}
