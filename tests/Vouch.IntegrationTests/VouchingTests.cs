using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Vouching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.BackgroundJobs;
using Vouch.Infrastructure.Persistence;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class VouchingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private Guid _campus;
    private User _target = null!;
    private User _foreign = null!;
    private readonly List<User> _peers = [];
    private int _clientNumber;
    private DateTimeOffset Now => _factory.Clock.GetUtcNow();

    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync();
        _factory = new(postgres.Database.ConnectionString);
        _factory.Clock.Advance(TimeSpan.FromTicks(-(Now.Ticks % 10))); // PostgreSQL microsecond precision.
        await using var db = postgres.CreateContext();
        var campus = new Campus { Code = "TEST", Name = "Test", DomainPattern = "@test.ac.lk" };
        var other = new Campus { Code = "OTHER", Name = "Other", DomainPattern = "@other.ac.lk" };
        _campus = campus.Id;
        _target = User("target"); _target.Status = AccountStatus.InIncubation;
        _foreign = User("foreign"); _foreign.CampusId = other.Id;
        _peers.AddRange(Enumerable.Range(0, 8).Select(i => User("peer" + i)));
        db.Campuses.AddRange(campus, other);
        db.Users.AddRange(_peers.Append(_target).Append(_foreign));
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() => await _factory.DisposeAsync();
    private User User(string name) => new()
    {
        CampusId = _campus, Email = name + "@test.ac.lk", FullName = name, PasswordHash = "unused",
        Faculty = "Science", Department = "Computing", AcademicYear = 2,
        Status = AccountStatus.Active, EmailVerifiedAt = Now, OnboardingCompletedAt = Now,
        DeepValues = ["Sincerity"], IntellectualInterests = [IntellectualInterest.Science], CreatedAt = Now.AddDays(-16)
    };
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
    private async Task<VouchDto> SubmitAsync(User voucher, User? target = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITrustService>().SubmitVouchAsync(voucher.Id,
            new((target ?? _target).Id, CharacterTrait.Sincere | CharacterTrait.Reliable, "A thoughtful peer"));
    }
    private VouchRecord Edge(User voucher, User target, double weight = 1) => new()
    { VoucherUserId = voucher.Id, TargetUserId = target.Id, Traits = CharacterTrait.Sincere, FinalCalculatedWeight = weight, CreatedAt = Now };
    private async Task<User> StoredAsync(User user)
    {
        await using var db = postgres.CreateContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
    }

    [Fact]
    public async Task ParallelDistinctVouchesPreserveTotalsAndActivateOnce()
    {
        var results = await Task.WhenAll(_peers.Select(p => SubmitAsync(p)));
        Assert.All(results, v => Assert.Equal(1, v.FinalWeight));
        var target = await StoredAsync(_target);
        Assert.Equal(8, target.ActiveVouchesReceivedCount); Assert.Equal(8, target.TrustScore);
        Assert.Equal(AccountStatus.Active, target.Status); Assert.Equal(Now, target.IncubationCompletedAt);
        await using var db = postgres.CreateContext(); Assert.Equal(8, await db.Vouches.CountAsync());
    }

    [Fact]
    public async Task ParallelDuplicatePairReturnsControlledConflict()
    {
        using var first = await ClientAsync(_peers[0]); using var second = await ClientAsync(_peers[0]);
        var request = new SubmitVouchRequest(_target.Id, CharacterTrait.Sincere, null);
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/vouches/", request), second.PostAsJsonAsync("/api/vouches/", request));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(1, (await StoredAsync(_target)).ActiveVouchesReceivedCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(64)] [InlineData(-1)]
    public async Task EmptyAndUnknownTraitsAreRejected(int traits)
    {
        using var client = await ClientAsync(_peers[0]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/vouches/",
            new SubmitVouchRequest(_target.Id, (CharacterTrait)traits, null))).StatusCode);
        Assert.Equal(0, (await StoredAsync(_target)).ActiveVouchesReceivedCount);
    }

    [Theory]
    [InlineData(-1, 0)] [InlineData(0, 1)] [InlineData(1, 1)]
    public async Task AccountAgeBoundaryIsExactAndWeightIsFrozen(int seconds, double expected)
    {
        await using (var db = postgres.CreateContext())
        {
            var voucher = await db.Users.SingleAsync(u => u.Id == _peers[0].Id);
            voucher.CreatedAt = Now.AddDays(-14).AddSeconds(-seconds); await db.SaveChangesAsync();
        }
        var result = await SubmitAsync(_peers[0]); Assert.Equal(expected, result.FinalWeight);
        _factory.Clock.Advance(TimeSpan.FromDays(20));
        await SubmitAsync(_peers[1]);
        await using var check = postgres.CreateContext();
        var stored = await check.Vouches.SingleAsync(v => v.Id == result.Id);
        Assert.Equal(expected, stored.FinalCalculatedWeight);
        Assert.Equal(expected == 0, stored.IsZeroWeightDueToAccountAge);
    }

    [Fact]
    public async Task ThreeYoungEligiblePeersActivateWithoutAddingScore()
    {
        await using (var db = postgres.CreateContext())
        {
            foreach (var user in await db.Users.Where(u => _peers.Take(3).Select(p => p.Id).Contains(u.Id)).ToListAsync()) user.CreatedAt = Now;
            await db.SaveChangesAsync();
        }
        await Task.WhenAll(_peers.Take(3).Select(p => SubmitAsync(p)));
        var target = await StoredAsync(_target);
        Assert.Equal(AccountStatus.Active, target.Status); Assert.Equal(3, target.ActiveVouchesReceivedCount); Assert.Equal(0, target.TrustScore);
    }

    [Theory]
    [InlineData(4, 2, 1)] [InlineData(5, 2, 1.2)] [InlineData(5, 3, .6)]
    public async Task BonusAndCliqueBoundariesUseQualifyingPeersAndPersistEvidence(int received, int shared, double weight)
    {
        await using (var db = postgres.CreateContext())
        {
            for (var i = 1; i <= received; i++) db.Vouches.Add(Edge(_peers[i], _peers[0]));
            for (var i = 1; i <= shared; i++) db.Vouches.Add(Edge(_peers[i], _target));
            db.Vouches.Add(Edge(_foreign, _peers[0])); db.Vouches.Add(Edge(_foreign, _target));
            await db.SaveChangesAsync();
        }
        var result = await SubmitAsync(_peers[0]); Assert.Equal(weight, result.FinalWeight, 8);
        await using var check = postgres.CreateContext(); var record = await check.Vouches.SingleAsync(v => v.Id == result.Id);
        Assert.Equal(shared, record.MutualVoucherCountAtSubmission); Assert.Equal(shared >= 3, record.IsCliqueFlagged);
        Assert.Equal(received >= 5 ? .2 : 0, record.VoucherBonus);
    }

    [Fact]
    public async Task CappedScoreRecomputesFromRemainingRecordsRatherThanSubtractingFromCap()
    {
        await using var db = postgres.CreateContext();
        var peers = Enumerable.Range(0, 21).Select(i => User("cap" + i)).ToArray();
        db.Users.AddRange(peers); await db.SaveChangesAsync();
        db.Vouches.AddRange(peers.Select(p => Edge(p, _target))); await db.SaveChangesAsync();
        Assert.Equal(20, (await StoredAsync(_target)).TrustScore);
        db.Vouches.Remove(await db.Vouches.FirstAsync()); await db.SaveChangesAsync();
        Assert.Equal(20, (await StoredAsync(_target)).TrustScore);
        db.Vouches.Remove(await db.Vouches.FirstAsync()); await db.SaveChangesAsync();
        var target = await StoredAsync(_target); Assert.Equal(19, target.TrustScore); Assert.Equal(19, target.ActiveVouchesReceivedCount);
    }

    [Theory]
    [InlineData("suspended")] [InlineData("deletion")] [InlineData("unverified")]
    [InlineData("onboarding")] [InlineData("campus")] [InlineData("block")]
    public async Task LosingVoucherEligibilityRecalculatesScoreAndDoesNotRelockActivatedMember(string change)
    {
        foreach (var peer in _peers.Take(3)) await SubmitAsync(peer);
        await using var db = postgres.CreateContext(); var voucher = await db.Users.SingleAsync(u => u.Id == _peers[0].Id);
        switch (change)
        {
            case "suspended": voucher.Status = AccountStatus.Suspended; break;
            case "deletion": voucher.Status = AccountStatus.DeletionRequested; break;
            case "unverified": voucher.EmailVerifiedAt = null; break;
            case "onboarding": voucher.OnboardingCompletedAt = null; break;
            case "campus": voucher.CampusId = _foreign.CampusId; break;
            case "block": db.Blocks.Add(new Block { BlockerId = _target.Id, BlockedUserId = voucher.Id }); break;
        }
        await db.SaveChangesAsync();
        var target = await StoredAsync(_target);
        Assert.Equal(2, target.ActiveVouchesReceivedCount); Assert.Equal(2, target.TrustScore); Assert.Equal(AccountStatus.Active, target.Status);
        if (change == "suspended")
        { voucher.Status = AccountStatus.Active; await db.SaveChangesAsync(); Assert.Equal(3, (await StoredAsync(_target)).TrustScore); }
    }

    [Fact]
    public async Task SelfCrossCampusUnverifiedAndBlockedVouchesCannotActivate()
    {
        using var client = await ClientAsync(_peers[0]);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/vouches/", new SubmitVouchRequest(_peers[0].Id, CharacterTrait.Sincere, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/vouches/", new SubmitVouchRequest(_foreign.Id, CharacterTrait.Sincere, null))).StatusCode);
        await using var db = postgres.CreateContext(); var voucher = await db.Users.SingleAsync(u => u.Id == _peers[0].Id);
        voucher.EmailVerifiedAt = null; await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/vouches/", new SubmitVouchRequest(_target.Id, CharacterTrait.Sincere, null))).StatusCode);
        voucher.EmailVerifiedAt = Now; db.Blocks.Add(new Block { BlockerId = _peers[0].Id, BlockedUserId = _target.Id }); await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/vouches/", new SubmitVouchRequest(_target.Id, CharacterTrait.Sincere, null))).StatusCode);
        Assert.Equal(AccountStatus.InIncubation, (await StoredAsync(_target)).Status);
    }

    [Fact]
    public async Task RequestsArePrivateAndVouchFulfillmentIsAtomic()
    {
        using var target = await ClientAsync(_target); using var peer = await ClientAsync(_peers[0]); using var outsider = await ClientAsync(_peers[1]);
        var response = await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = (await response.Content.ReadFromJsonAsync<PeerVouchRequestDto>())!;
        Assert.Equal("target", request.RequesterName); Assert.Equal(Now.AddDays(7), request.ExpiresAt);
        Assert.Single((await peer.GetFromJsonAsync<PeerVouchRequestPage>("/api/vouches/requests/incoming"))!.Items);
        Assert.Empty((await outsider.GetFromJsonAsync<PeerVouchRequestPage>("/api/vouches/requests/incoming"))!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync($"/api/vouches/requests/{request.Id}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await peer.PostAsync($"/api/vouches/requests/{request.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await target.PostAsync($"/api/vouches/requests/{request.Id}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await peer.PostAsJsonAsync("/api/vouches/", new SubmitVouchRequest(_target.Id, CharacterTrait.Sincere, null))).StatusCode);
        var fulfilled = Assert.Single((await target.GetFromJsonAsync<PeerVouchRequestPage>("/api/vouches/requests/sent"))!.Items);
        Assert.Equal(PeerVouchRequestStatus.Fulfilled, fulfilled.Status); Assert.Equal(Now, fulfilled.ResolvedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id))).StatusCode);
    }

    [Fact]
    public async Task RequestDismissalCooldownExpiryAndConcurrentCreationAreEnforced()
    {
        using var target = await ClientAsync(_target); using var other = await ClientAsync(_target); using var peer = await ClientAsync(_peers[0]);
        var responses = await Task.WhenAll(target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id)),
            other.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id)));
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order().ToArray());
        var request = (await responses.Single(r => r.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<PeerVouchRequestDto>())!;
        Assert.Equal(HttpStatusCode.NoContent, (await peer.PostAsync($"/api/vouches/requests/{request.Id}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await peer.PostAsync($"/api/vouches/requests/{request.Id}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id))).StatusCode);
        var pending = (await (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[1].Id))).Content.ReadFromJsonAsync<PeerVouchRequestDto>())!;
        _factory.Clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(PeerVouchRequestStatus.Expired, (await target.GetFromJsonAsync<PeerVouchRequestPage>("/api/vouches/requests/sent"))!.Items.Single(r => r.Id == pending.Id).Status);
        Assert.Equal(HttpStatusCode.Created, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[1].Id))).StatusCode);
        await using var db = postgres.CreateContext(); Assert.Equal(PeerVouchRequestStatus.Expired, (await db.VouchRequests.SingleAsync(r => r.Id == pending.Id)).Status);
    }

    [Fact]
    public async Task RequestLimitsArePersistentAndAtomic()
    {
        using var target = await ClientAsync(_target);
        var responses = await Task.WhenAll(_peers.Select(p => target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(p.Id))));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Created)); Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await using var db = postgres.CreateContext(); Assert.Equal(5, await db.VouchRequests.CountAsync());
        var current = await db.VouchRequests.AsNoTracking().ToListAsync();
        var unused = _peers.First(p => current.All(r => r.RequestedVoucherId != p.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await target.PostAsync($"/api/vouches/requests/{current[0].Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(unused.Id))).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(HttpStatusCode.Created, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(unused.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await target.GetAsync("/api/vouches/requests/sent?pageSize=2147483647")).StatusCode);
        Assert.Equal(6, (await target.GetFromJsonAsync<PeerVouchRequestPage>("/api/vouches/requests/sent?pageSize=2"))!.TotalCount);
    }

    [Fact]
    public async Task PendingRequestLimitAndCancellationFreeOneSlot()
    {
        await using var db = postgres.CreateContext();
        var peers = Enumerable.Range(0, 12).Select(i => User("requestlimit" + i)).ToArray();
        db.Users.AddRange(peers); await db.SaveChangesAsync();
        using var target = await ClientAsync(_target);
        for (var day = 0; day < 2; day++)
        {
            for (var i = day * 5; i < day * 5 + 5; i++)
                Assert.Equal(HttpStatusCode.Created, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(peers[i].Id))).StatusCode);
            _factory.Clock.Advance(TimeSpan.FromDays(1));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(peers[10].Id))).StatusCode);
        var request = await db.VouchRequests.AsNoTracking().FirstAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await target.PostAsync($"/api/vouches/requests/{request.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await target.PostAsync($"/api/vouches/requests/{request.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(peers[10].Id))).StatusCode);
        Assert.Equal(10, await db.VouchRequests.CountAsync(r => r.Status == PeerVouchRequestStatus.Pending));
    }

    [Fact]
    public async Task RequestsRequireCurrentSameCampusEligibilityAndDisappearAfterBlock()
    {
        using var target = await ClientAsync(_target); using var peer = await ClientAsync(_peers[0]);
        Assert.Equal(HttpStatusCode.BadRequest, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_target.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_foreign.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await peer.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_target.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id))).StatusCode);
        await using var db = postgres.CreateContext(); db.Blocks.Add(new Block { BlockerId = _peers[0].Id, BlockedUserId = _target.Id }); await db.SaveChangesAsync();
        Assert.Empty((await target.GetFromJsonAsync<PeerVouchRequestPage>("/api/vouches/requests/sent"))!.Items);
        Assert.Empty((await peer.GetFromJsonAsync<PeerVouchRequestPage>("/api/vouches/requests/incoming"))!.Items);
        Assert.Equal(PeerVouchRequestStatus.Cancelled, (await db.VouchRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id))).StatusCode);
    }

    [Fact]
    public async Task FailedVouchRollsBackFulfillmentAndMetrics()
    {
        using var target = await ClientAsync(_target); using var peer = await ClientAsync(_peers[0]);
        await target.PostAsJsonAsync("/api/vouches/requests", new RequestPeerVouchRequest(_peers[0].Id));
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION test_reject_vouch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END; $$; CREATE TRIGGER test_reject_vouch BEFORE INSERT ON \"Vouches\" FOR EACH ROW EXECUTE FUNCTION test_reject_vouch();");
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await peer.PostAsJsonAsync("/api/vouches/", new SubmitVouchRequest(_target.Id, CharacterTrait.Sincere, null))).StatusCode);
            Assert.Equal(PeerVouchRequestStatus.Pending, (await db.VouchRequests.AsNoTracking().SingleAsync()).Status);
            Assert.Empty(await db.Vouches.ToListAsync()); Assert.Equal(0, (await StoredAsync(_target)).TrustScore);
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER test_reject_vouch ON \"Vouches\"; DROP FUNCTION test_reject_vouch();"); }
        Assert.Equal(HttpStatusCode.Created, (await peer.PostAsJsonAsync("/api/vouches/", new SubmitVouchRequest(_target.Id, CharacterTrait.Sincere, null))).StatusCode);
    }

    [Fact]
    public async Task BulkAccountDeletionRecalculatesSurvivingTotalsAndCascadesRequests()
    {
        foreach (var peer in _peers.Take(3)) await SubmitAsync(peer);
        await using var db = postgres.CreateContext();
        db.VouchRequests.Add(new PeerVouchRequest { RequesterId = _target.Id, RequestedVoucherId = _peers[0].Id, ExpiresAt = Now.AddDays(7) });
        await db.SaveChangesAsync();
        var worker = ActivatorUtilities.CreateInstance<AccountDeletionWorker>(_factory.Services);
        var deletion = typeof(AccountDeletionWorker).GetMethod("DeleteUserDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)deletion.Invoke(worker, [db, _peers[0].Id, CancellationToken.None])!;
        Assert.False(await db.Users.AnyAsync(u => u.Id == _peers[0].Id)); Assert.Empty(await db.VouchRequests.ToListAsync());
        var target = await StoredAsync(_target); Assert.Equal(2, target.TrustScore); Assert.Equal(2, target.ActiveVouchesReceivedCount);
    }
}
