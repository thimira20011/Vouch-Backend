using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Moderation;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Persistence;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class ModerationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private Guid _campus;
    private User _target = null!, _architect = null!, _second = null!, _foreign = null!;
    private readonly List<User> _peers = [];
    private int _clientNumber;
    private DateTimeOffset Now => _factory.Clock.GetUtcNow();

    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync();
        _factory = new(postgres.Database.ConnectionString);
        _factory.Clock.Advance(TimeSpan.FromTicks(-(Now.Ticks % 10)));
        await using var db = postgres.CreateContext();
        var campus = new Campus { Code = "TEST", Name = "Test", DomainPattern = "@test.ac.lk" };
        var other = new Campus { Code = "OTHER", Name = "Other", DomainPattern = "@other.ac.lk" };
        _campus = campus.Id;
        _target = Member("target"); _architect = Member("architect"); _second = Member("second"); _foreign = Member("foreign");
        _architect.Role = _second.Role = _foreign.Role = UserRole.Architect; _foreign.CampusId = other.Id;
        _peers.AddRange(Enumerable.Range(0, 30).Select(i => Member("peer" + i)));
        db.Campuses.AddRange(campus, other);
        db.Users.AddRange(_peers.Concat([_target, _architect, _second, _foreign]));
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() => await _factory.DisposeAsync();
    private User Member(string name) => new()
    {
        CampusId = _campus, Email = name + "@test.ac.lk", FullName = name, PasswordHash = "unused",
        Status = AccountStatus.Active, Faculty = "Science", Department = "Computing", AcademicYear = 2,
        EmailVerifiedAt = Now, OnboardingCompletedAt = Now, CreatedAt = Now.AddDays(-100),
        DeepValues = ["Sincerity"], IntellectualInterests = [IntellectualInterest.Science]
    };
    private async Task<T> ServiceAsync<T>(Func<IModerationService, Task<T>> work)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<IModerationService>());
    }
    private async Task ActionAsync(Func<IModerationService, Task> work)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<IModerationService>());
    }
    private Task<ReportDto> SubmitAsync(int peer = 0, Guid? messageId = null) => ServiceAsync(s =>
        s.SubmitReportAsync(_peers[peer].Id, new(_target.Id, messageId, ReportCategory.Spam, "Detailed report evidence.")));
    private Task<bool> ResolveAsync(Guid report, bool uphold = true, string? notes = "Reviewed evidence.", User? actor = null) =>
        ServiceAsync(s => s.ResolveReportAsync((actor ?? _architect).Id, report, uphold, notes));
    private Task ChangeAsync(bool reinstate = false, string reason = "Reviewed account evidence.") =>
        ActionAsync(s => s.ChangeSuspensionAsync(_architect.Id, _target.Id, reinstate, new(reason)));
    private async Task<User> StoredAsync()
    {
        await using var db = postgres.CreateContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == _target.Id);
    }
    private async Task<HttpClient> ClientAsync(User user)
    {
        await using var db = postgres.CreateContext();
        var current = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        var session = new AuthSession { UserId = user.Id, ExpiresAt = Now.AddDays(30) };
        db.AuthSessions.Add(session); await db.SaveChangesAsync();
        var client = _factory.Client($"198.51.100.{++_clientNumber}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<IJwtTokenService>().GenerateToken(current, session.Id));
        return client;
    }
    private Report Report(User reporter, ReportStatus status = ReportStatus.Pending, DateTimeOffset? resolved = null) => new()
    {
        ReporterId = reporter.Id, ReportedUserId = _target.Id, Category = ReportCategory.Spam,
        Details = "Detailed report evidence.", SeverityScore = 3, CreatedAt = Now.AddDays(-200), Status = status, ResolvedAt = resolved
    };

    [Fact]
    public async Task ThirdDecisionSuspendsImmediatelyAndRevokesSessions()
    {
        using var target = await ClientAsync(_target);
        for (var i = 0; i < 3; i++)
        {
            var report = await SubmitAsync(i); await ResolveAsync(report.Id);
            var stored = await StoredAsync(); Assert.Equal(i + 1, stored.UpheldReportsCount);
            Assert.Equal(i == 2 ? AccountStatus.Suspended : AccountStatus.Active, stored.Status);
            Assert.Equal(i == 2, stored.IsSoftHiddenFromMatchmaking);
        }
        var subject = await StoredAsync(); Assert.Equal(AccountStatus.Active, subject.StatusBeforeSuspension); Assert.Equal(Now, subject.SuspendedAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/identity")).StatusCode);
        await using var db = postgres.CreateContext();
        Assert.All(await db.AuthSessions.ToListAsync(), s => Assert.NotNull(s.RevokedAt));
        Assert.Equal(3, await db.ModerationAudits.CountAsync(a => a.Action == ModerationAction.ReportUpheld));
        Assert.Single(await db.ModerationAudits.Where(a => a.Action == ModerationAction.AutomaticallySuspended).ToListAsync());
        Assert.All(await db.Reports.ToListAsync(), r => { Assert.Equal(_architect.Id, r.ResolvedByArchitectId); Assert.Equal(Now, r.ResolvedAt); });
    }

    [Fact]
    public async Task ParallelDistinctDecisionsDoNotLoseTheThirdSuspension()
    {
        var reports = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => SubmitAsync(i)));
        await Task.WhenAll(reports.Select(r => ResolveAsync(r.Id)));
        var target = await StoredAsync(); Assert.Equal(AccountStatus.Suspended, target.Status); Assert.Equal(3, target.UpheldReportsCount);
        await using var db = postgres.CreateContext(); Assert.Equal(4, await db.ModerationAudits.CountAsync());
    }

    [Fact]
    public async Task ParallelRetriesPreserveOriginalActorTimeAndSingleAudit()
    {
        var report = await SubmitAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ResolveAsync(report.Id, actor: i % 2 == 0 ? _architect : _second)));
        await using var db = postgres.CreateContext(); var saved = await db.Reports.AsNoTracking().SingleAsync();
        var audit = await db.ModerationAudits.AsNoTracking().SingleAsync(); Assert.Equal(saved.ResolvedByArchitectId, audit.ArchitectId);
        _factory.Clock.Advance(TimeSpan.FromMinutes(1)); await ResolveAsync(report.Id, actor: _second);
        await db.Entry(saved).ReloadAsync(); Assert.Equal(audit.CreatedAt, saved.ResolvedAt); Assert.Equal(1, (await StoredAsync()).UpheldReportsCount);
        using var client = await ClientAsync(_architect);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/architect/reports/{report.Id}/resolve", new ResolveReportBody(false, "Reviewed evidence."))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/architect/reports/{report.Id}/resolve", new ResolveReportBody(true, "Changed notes."))).StatusCode);
        Assert.Single(await db.ModerationAudits.ToListAsync());
    }

    [Theory]
    [InlineData(-1, AccountStatus.Active)] [InlineData(0, AccountStatus.Suspended)] [InlineData(1, AccountStatus.Suspended)]
    public async Task ResolutionWindowLowerBoundaryIsInclusive(int offsetSeconds, AccountStatus expected)
    {
        await using (var db = postgres.CreateContext())
        {
            db.Reports.AddRange(Report(_peers[0], ReportStatus.Upheld, Now.AddDays(-90).AddSeconds(offsetSeconds)),
                Report(_peers[1], ReportStatus.Upheld, Now.AddDays(-1)));
            await db.SaveChangesAsync();
        }
        var current = await SubmitAsync(2); await ResolveAsync(current.Id);
        var target = await StoredAsync(); Assert.Equal(expected, target.Status); Assert.Equal(3, target.UpheldReportsCount);
    }

    [Fact]
    public async Task SubmissionTimeDoesNotCountAndFutureResolutionsAreExcluded()
    {
        await using (var db = postgres.CreateContext())
        {
            db.Reports.AddRange(Report(_peers[0], ReportStatus.Upheld, Now.AddSeconds(1)),
                Report(_peers[1], ReportStatus.Upheld, Now.AddDays(-1)), Report(_peers[2]));
            await db.SaveChangesAsync();
        }
        await using var check = postgres.CreateContext();
        var pending = await check.Reports.SingleAsync(r => r.Status == ReportStatus.Pending); await ResolveAsync(pending.Id);
        var review = await ServiceAsync(s => s.GetAccountReviewAsync(_architect.Id, _target.Id));
        Assert.Equal(2, review.UpheldReportsIn90Days); Assert.Equal(3, review.UpheldReportsCount); Assert.Equal(AccountStatus.Active, review.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LastPendingDecisionRestoresAvailableVisibility(bool uphold)
    {
        var first = await SubmitAsync(); var second = await SubmitAsync(1);
        Assert.True((await StoredAsync()).IsSoftHiddenFromMatchmaking);
        await ResolveAsync(first.Id, uphold); Assert.True((await StoredAsync()).IsSoftHiddenFromMatchmaking);
        await ResolveAsync(second.Id, uphold); Assert.False((await StoredAsync()).IsSoftHiddenFromMatchmaking);
    }

    [Theory]
    [InlineData(AccountStatus.Suspended)] [InlineData(AccountStatus.DeletionRequested)]
    public async Task DismissingDoesNotRestoreUnavailableAccounts(AccountStatus status)
    {
        var report = await SubmitAsync();
        await using (var db = postgres.CreateContext())
        {
            var target = await db.Users.SingleAsync(u => u.Id == _target.Id); target.Status = status; await db.SaveChangesAsync();
        }
        await ResolveAsync(report.Id, false);
        var saved = await StoredAsync(); Assert.Equal(status, saved.Status); Assert.True(saved.IsSoftHiddenFromMatchmaking);
    }

    [Fact]
    public async Task FullQueueAndDashboardTotalsRemainAccurateBeyondTwenty()
    {
        await using (var db = postgres.CreateContext())
        {
            var reports = _peers.Take(25).Select(p => Report(p)).ToList();
            for (var i = 0; i < 22; i++) reports[i].SeverityScore = 5;
            db.Reports.AddRange(reports); db.Reports.Add(Report(_foreign)); await db.SaveChangesAsync();
        }
        using var client = await ClientAsync(_architect);
        var first = (await client.GetFromJsonAsync<ReportPage>($"/api/architect/campuses/{_campus}/reports?pageSize=20"))!;
        var second = (await client.GetFromJsonAsync<ReportPage>($"/api/architect/campuses/{_campus}/reports?page=2&pageSize=20"))!;
        Assert.Equal(25, first.TotalCount); Assert.Equal(25, second.TotalCount); Assert.Equal(20, first.Items.Count); Assert.Equal(5, second.Items.Count);
        Assert.Equal(25, first.Items.Concat(second.Items).Select(r => r.Id).Distinct().Count());
        Assert.Equal(3, second.Items.Count(r => r.SeverityScore == 3));
        var dashboard = (await client.GetFromJsonAsync<ArchitectDashboardDto>($"/api/architect/dashboard/{_campus}"))!;
        Assert.Equal(25, dashboard.PendingReportsCount); Assert.Equal(20, dashboard.CriticalReports.Count);
        Assert.Empty((await client.GetFromJsonAsync<ReportPage>($"/api/architect/campuses/{_campus}/reports?page=3"))!.Items);
        using var response = await client.GetAsync($"/api/architect/users/{_target.Id}/moderation");
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("page=0")] [InlineData("pageSize=51")] [InlineData("status=99")] [InlineData("page=10001")]
    public async Task InvalidReportPaginationIsRejected(string query)
    {
        using var client = await ClientAsync(_architect);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/architect/campuses/{_campus}/reports?{query}")).StatusCode);
    }

    [Fact]
    public async Task ForeignAndOrdinaryUsersCannotReviewOrResolve()
    {
        var report = await SubmitAsync(); using var foreign = await ClientAsync(_foreign); using var member = await ClientAsync(_peers[0]);
        foreach (var route in new[] { $"/campuses/{_campus}/reports", $"/reports/{report.Id}", $"/users/{_target.Id}/moderation" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync("/api/architect" + route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/architect" + route)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsJsonAsync($"/api/architect/reports/{report.Id}/resolve", new ResolveReportBody(true, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsJsonAsync($"/api/architect/users/{_target.Id}/suspend", new AccountModerationBody("Reviewed evidence."))).StatusCode);
        await using var db = postgres.CreateContext(); Assert.Empty(await db.ModerationAudits.ToListAsync());
    }

    private async Task<Message> MessageAsync(bool validPair, bool system = false)
    {
        await using var db = postgres.CreateContext();
        var conversation = new Conversation { UserAId = _peers[0].Id, UserBId = validPair ? _target.Id : _peers[1].Id, Status = ConversationStatus.Archived };
        var message = new Message { ConversationId = conversation.Id, SenderId = _target.Id, Body = "Private reported message.", Type = system ? MessageType.System : MessageType.Text };
        db.Conversations.Add(conversation); db.Messages.Add(message); await db.SaveChangesAsync(); return message;
    }

    [Fact]
    public async Task ArchivedBlockedPairEvidenceIsAvailableOnlyToCampusArchitect()
    {
        var message = await MessageAsync(true);
        await ActionAsync(s => s.BlockUserAsync(_peers[0].Id, new(_target.Id)));
        var report = await SubmitAsync(messageId: message.Id);
        using var client = await ClientAsync(_architect);
        var evidence = (await client.GetFromJsonAsync<ReportEvidenceDto>($"/api/architect/reports/{report.Id}"))!;
        Assert.True(evidence.MessageEvidenceAvailable); Assert.Equal(message.Body, evidence.MessageBody);
        await ResolveAsync(report.Id);
        var completed = (await client.GetFromJsonAsync<ReportPage>($"/api/architect/campuses/{_campus}/reports?status=Upheld"))!.Items.Single();
        Assert.Equal(message.Id, completed.RelatedMessageId); Assert.Equal(_architect.Id, completed.ResolvedByArchitectId); Assert.Equal("Reviewed evidence.", completed.ArchitectNotes);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, true)]
    public async Task UnrelatedAndSystemEvidenceIsRejectedAndLegacyBodyIsWithheld(bool validPair, bool system)
    {
        var message = await MessageAsync(validPair, system);
        await Assert.ThrowsAsync<EligibilityException>(() => SubmitAsync(messageId: message.Id));
        await using var db = postgres.CreateContext(); Assert.Empty(await db.Reports.ToListAsync());
        var legacy = Report(_peers[0]); legacy.RelatedMessageId = message.Id; db.Reports.Add(legacy); await db.SaveChangesAsync();
        var evidence = await ServiceAsync(s => s.GetReportAsync(_architect.Id, legacy.Id));
        Assert.False(evidence.MessageEvidenceAvailable); Assert.Null(evidence.MessageBody);
        await Assert.ThrowsAsync<EligibilityException>(() => ResolveAsync(legacy.Id));
        await ResolveAsync(legacy.Id, false); Assert.False((await StoredAsync()).IsSoftHiddenFromMatchmaking);
    }

    [Theory]
    [InlineData(AccountStatus.Active)] [InlineData(AccountStatus.InIncubation)]
    public async Task ManualSuspensionAndReinstatementRestoreStateAndPreserveRevocation(AccountStatus prior)
    {
        await using (var db = postgres.CreateContext())
        {
            var target = await db.Users.SingleAsync(u => u.Id == _target.Id); target.Status = prior; await db.SaveChangesAsync();
        }
        using var old = await ClientAsync(_target);
        using var architect = await ClientAsync(_architect);
        var reason = new AccountModerationBody("Reviewed account evidence.");
        Assert.Equal(HttpStatusCode.NoContent, (await architect.PostAsJsonAsync($"/api/architect/users/{_target.Id}/suspend", reason)).StatusCode);
        await ChangeAsync(); // Identical retry creates no duplicate audit.
        Assert.Equal(AccountStatus.Suspended, (await StoredAsync()).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/auth/identity")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await architect.PostAsJsonAsync($"/api/architect/users/{_target.Id}/reinstate", reason)).StatusCode);
        await ChangeAsync(true); var saved = await StoredAsync(); Assert.Equal(prior, saved.Status); Assert.Null(saved.SuspendedAt); Assert.Null(saved.StatusBeforeSuspension);
        Assert.False(saved.IsSoftHiddenFromMatchmaking);
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/auth/identity")).StatusCode);
        using var fresh = await ClientAsync(_target); Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/identity")).StatusCode);
        var first = await ServiceAsync(s => s.GetAccountReviewAsync(_architect.Id, _target.Id, pageSize: 1));
        var second = await ServiceAsync(s => s.GetAccountReviewAsync(_architect.Id, _target.Id, page: 2, pageSize: 1));
        Assert.Equal(2, first.TotalAuditCount); Assert.Equal(ModerationAction.Reinstated, first.Audit.Single().Action);
        Assert.Equal(ModerationAction.ManuallySuspended, second.Audit.Single().Action);
    }

    [Fact]
    public async Task ManualOverrideDoesNotReplayOldDecisionsButNewDecisionCanSuspendAgain()
    {
        var reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => SubmitAsync(i)));
        foreach (var report in reports.Take(3)) await ResolveAsync(report.Id);
        await ChangeAsync(true); Assert.Equal(AccountStatus.Active, (await StoredAsync()).Status);
        Assert.True((await StoredAsync()).IsSoftHiddenFromMatchmaking); // Remaining pending report.
        await ResolveAsync(reports[0].Id); Assert.Equal(AccountStatus.Active, (await StoredAsync()).Status);
        await ResolveAsync(reports[3].Id); Assert.Equal(AccountStatus.Suspended, (await StoredAsync()).Status);
        await using var db = postgres.CreateContext(); Assert.Equal(2, await db.ModerationAudits.CountAsync(a => a.Action == ModerationAction.AutomaticallySuspended));
    }

    [Fact]
    public async Task InvalidReasonsDeletionAndArchitectChangesAreRejected()
    {
        using var client = await ClientAsync(_architect);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/architect/users/{_target.Id}/suspend", new AccountModerationBody("short"))).StatusCode);
        foreach (var subject in new[] { _architect, _second })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/architect/users/{subject.Id}/suspend", new AccountModerationBody("Reviewed evidence."))).StatusCode);
        var report = await SubmitAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/architect/reports/{report.Id}/resolve", new ResolveReportBody(true, new string('x', 1001)))).StatusCode);
        await using var db = postgres.CreateContext(); var target = await db.Users.SingleAsync(u => u.Id == _target.Id);
        target.Status = AccountStatus.DeletionRequested; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/architect/users/{_target.Id}/reinstate", new AccountModerationBody("Reviewed evidence."))).StatusCode);
        Assert.Empty(await db.ModerationAudits.ToListAsync());
    }

    [Fact]
    public async Task AuditReasonsAreEncryptedAndTrackedEditsAreRejected()
    {
        var report = await SubmitAsync(); await ResolveAsync(report.Id);
        await using var db = postgres.CreateContext();
        var raw = await db.Database.SqlQueryRaw<string>("SELECT \"Reason\" AS \"Value\" FROM \"ModerationAudits\"").SingleAsync();
        Assert.StartsWith("vouch:v1:", raw); Assert.DoesNotContain("Reviewed evidence", raw);
        var audit = await db.ModerationAudits.SingleAsync(); Assert.Equal("Reviewed evidence.", audit.Reason);
        audit.Reason = "Changed evidence."; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditFailureRollsBackDecisionSuspensionCountersAndSessionRevocation()
    {
        for (var i = 0; i < 2; i++) { var prior = await SubmitAsync(i); await ResolveAsync(prior.Id); }
        var report = await SubmitAsync(2); using var target = await ClientAsync(_target);
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION step11_fail_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected audit failure'; END $$;
            CREATE TRIGGER step11_fail_audit BEFORE INSERT ON "ModerationAudits" FOR EACH ROW EXECUTE FUNCTION step11_fail_audit();
            """);
        try { await Assert.ThrowsAsync<DbUpdateException>(() => ResolveAsync(report.Id)); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER step11_fail_audit ON \"ModerationAudits\"; DROP FUNCTION step11_fail_audit()"); }
        var saved = await StoredAsync(); Assert.Equal(AccountStatus.Active, saved.Status); Assert.Equal(2, saved.UpheldReportsCount); Assert.True(saved.IsSoftHiddenFromMatchmaking);
        var pending = await db.Reports.AsNoTracking().SingleAsync(r => r.Id == report.Id);
        Assert.Equal(ReportStatus.Pending, pending.Status); Assert.Null(pending.ResolvedAt); Assert.Null(pending.ResolvedByArchitectId);
        Assert.Equal(2, await db.ModerationAudits.CountAsync()); Assert.Null((await db.AuthSessions.SingleAsync()).RevokedAt);
        Assert.Equal(HttpStatusCode.OK, (await target.GetAsync("/api/auth/identity")).StatusCode);
    }

    [Fact]
    public async Task CampusChangeRemovesReportAndRelatedAuditFromCurrentReview()
    {
        var report = await SubmitAsync(); await ResolveAsync(report.Id);
        await using (var db = postgres.CreateContext())
        {
            var reporter = await db.Users.SingleAsync(u => u.Id == _peers[0].Id); reporter.CampusId = _foreign.CampusId;
            await db.SaveChangesAsync();
        }
        var page = await ServiceAsync(s => s.GetReportsAsync(_architect.Id, _campus, ReportStatus.Upheld));
        var review = await ServiceAsync(s => s.GetAccountReviewAsync(_architect.Id, _target.Id));
        Assert.Empty(page.Items); Assert.Equal(0, page.TotalCount); Assert.Empty(review.Audit); Assert.Equal(0, review.TotalAuditCount);
        Assert.Equal(0, review.UpheldReportsCount); Assert.Equal(0, review.UpheldReportsIn90Days); Assert.Equal(0, (await StoredAsync()).UpheldReportsCount);
        await Assert.ThrowsAsync<EligibilityException>(() => ServiceAsync(s => s.GetReportAsync(_architect.Id, report.Id)));
        await Assert.ThrowsAsync<EligibilityException>(() => ResolveAsync(report.Id));
        await using var check = postgres.CreateContext(); Assert.Single(await check.ModerationAudits.ToListAsync()); // Historical audit is retained privately.
    }

    [Fact]
    public async Task ReportDeletionRetainsAuditWithClearedReferenceAndRecomputesTotals()
    {
        var report = await SubmitAsync(); await ResolveAsync(report.Id);
        await using var db = postgres.CreateContext(); db.Reports.Remove(await db.Reports.SingleAsync()); await db.SaveChangesAsync();
        var audit = await db.ModerationAudits.SingleAsync(); Assert.Null(audit.ReportId); Assert.Equal(0, (await StoredAsync()).UpheldReportsCount);
        db.ChangeTracker.Clear(); db.Users.Remove(await db.Users.SingleAsync(u => u.Id == _target.Id)); await db.SaveChangesAsync();
        Assert.Empty(await db.ModerationAudits.ToListAsync());
    }

    [Fact]
    public async Task PreviousSchemaUpgradeBackfillsOnlyMissingDecisionTimesAndPreservesLegacySuspension()
    {
        // Isolated fixture database only: downgrade empty audit schema, then exercise the real explicit upgrade.
        var created = Now.AddDays(-20); var preserved = Now.AddDays(-10);
        await using (var db = postgres.CreateContext())
        {
            var first = Report(_peers[0], ReportStatus.Upheld); first.CreatedAt = created;
            db.Reports.AddRange(first, Report(_peers[1], ReportStatus.Dismissed, preserved), Report(_peers[2]));
            var target = await db.Users.SingleAsync(u => u.Id == _target.Id); target.Status = AccountStatus.Suspended;
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261009213532_ConsistentVouchesAndPeerRequests");
        }
        await using var upgraded = postgres.CreateContext();
        var result = await new DatabaseUpgrade(upgraded, PostgresFixture.Encryption, PostgresFixture.Lookup).ApplyAsync(backupConfirmed: true);
        Assert.True(result.CanApply); Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
        var reports = await upgraded.Reports.ToListAsync();
        Assert.Equal(created, reports.Single(r => r.Status == ReportStatus.Upheld).ResolvedAt);
        Assert.Equal(preserved, reports.Single(r => r.Status == ReportStatus.Dismissed).ResolvedAt);
        Assert.Null(reports.Single(r => r.Status == ReportStatus.Pending).ResolvedAt);
        Assert.All(reports, r => Assert.Null(r.ResolvedByArchitectId)); Assert.Empty(await upgraded.ModerationAudits.ToListAsync());
        var saved = await StoredAsync(); Assert.Equal(AccountStatus.Suspended, saved.Status); Assert.Null(saved.StatusBeforeSuspension); Assert.Null(saved.SuspendedAt);
        await ChangeAsync(true); Assert.Equal(AccountStatus.InIncubation, (await StoredAsync()).Status);
    }
}
