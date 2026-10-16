using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.Matching;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;
using Vouch.Domain.Services;
using Vouch.Infrastructure.Persistence;
using Vouch.Infrastructure.Security;
using Vouch.Infrastructure.Services;
using Vouch.IntegrationTests.Infrastructure;

namespace Vouch.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class PhotoStorageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private VouchApiFactory _factory = null!;
    private User _owner = null!, _viewer = null!, _stranger = null!, _foreign = null!;
    private Conversation _conversation = null!;
    private int _clientNumber;
    public async Task InitializeAsync()
    {
        await postgres.Database.ResetDataAsync();
        await using var db = postgres.CreateContext();
        var campus = new Campus { Name = "Photo", Code = "PHOTO", DomainPattern = "@photo.ac.lk" };
        var foreign = new Campus { Name = "Foreign", Code = "FOREIGN", DomainPattern = "@foreign.ac.lk" };
        db.Campuses.AddRange(campus, foreign);
        User Member(string name, Guid campusId) => new() { CampusId = campusId, Email = $"{name}@photo.ac.lk",
            FullName = name, PasswordHash = "unused", Faculty = "Science", Department = "Computing", AcademicYear = 2,
            Status = AccountStatus.Active, EmailVerifiedAt = DateTimeOffset.UtcNow, OnboardingCompletedAt = DateTimeOffset.UtcNow };
        _owner = Member("owner", campus.Id); _viewer = Member("viewer", campus.Id);
        _stranger = Member("stranger", campus.Id); _foreign = Member("foreign", foreign.Id);
        _stranger.Role = UserRole.Architect;
        db.Users.AddRange(_owner, _viewer, _stranger, _foreign);
        _conversation = new Conversation { UserAId = _owner.Id, UserBId = _viewer.Id };
        db.Conversations.Add(_conversation); await db.SaveChangesAsync();
        _factory = new(postgres.Database.ConnectionString, enablePhotos: true);
    }
    public async Task DisposeAsync() => await _factory.DisposeAsync();
    private async Task<HttpClient> Client(User? user)
    {
        var client = _factory.Client($"198.51.100.{Interlocked.Increment(ref _clientNumber)}");
        if (user is null) return client;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var current = await db.Users.SingleAsync(u => u.Id == user.Id);
        var session = new AuthSession { UserId = user.Id, ExpiresAt = _factory.Clock.GetUtcNow().AddDays(1) };
        db.AuthSessions.Add(session); await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", scope.ServiceProvider.GetRequiredService<IJwtTokenService>().GenerateToken(current, session.Id));
        return client;
    }
    private static byte[] Image()
    {
        using var image = new MagickImage(MagickColors.CadetBlue, 96, 128);
        new Drawables().FillColor(MagickColors.PeachPuff).Circle(48, 48, 70, 48)
            .FillColor(MagickColors.DarkBlue).Rectangle(10, 85, 86, 128)
            .FillColor(MagickColors.Black).Circle(40, 45, 43, 45).Circle(58, 45, 61, 45).Draw(image);
        return image.ToByteArray(MagickFormat.Png);
    }
    private async Task<(string OriginalUrl, string AbstractUrl)> Store(Guid? owner = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPhotoStorageService>().StorePhotoAsync(owner ?? _owner.Id, new MemoryStream(Image()), "image/png");
    }
    private static string At(string original, RevealClarityStage stage) => PhotoUrls.ForStage(original,
        Guid.ParseExact(original.Split('/')[3][..32], "N"), stage)!;

    [Theory]
    [InlineData(RevealClarityStage.Abstract0)]
    [InlineData(RevealClarityStage.Quarter25)]
    [InlineData(RevealClarityStage.Sixty60)]
    [InlineData(RevealClarityStage.Full100)]
    public async Task RetrievalEnforcesEveryStageAndReturnsOnlyTheAuthorizedBytes(RevealClarityStage earned)
    {
        var photo = await Store();
        await using var db = postgres.CreateContext();
        await db.Conversations.ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentClarityStage, earned));
        using var viewer = await Client(_viewer); using var owner = await Client(_owner); using var anonymous = await Client(null);
        foreach (var stage in new[] { RevealClarityStage.Abstract0, RevealClarityStage.Quarter25, RevealClarityStage.Sixty60, RevealClarityStage.Full100 })
        {
            var url = At(photo.OriginalUrl, stage);
            var response = await viewer.GetAsync(url);
            Assert.Equal((int)stage <= (int)earned ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            var own = await owner.GetAsync(url); Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            if (response.IsSuccessStatusCode)
            {
                Assert.Equal(await own.Content.ReadAsByteArrayAsync(), await response.Content.ReadAsByteArrayAsync());
                Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
                Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
                Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
                Assert.Null(response.Headers.ETag);
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, photo.AbstractUrl);
        request.Headers.Range = new RangeHeaderValue(0, 1);
        Assert.Equal(HttpStatusCode.OK, (await viewer.SendAsync(request)).StatusCode);
        var conversations = await viewer.GetStringAsync("/api/conversations");
        Assert.Contains(At(photo.OriginalUrl, earned), conversations);
    }

    [Fact]
    public async Task StrangersArchitectsForeignCampusesAndEitherBlockCannotReadAnyStage()
    {
        var photo = await Store();
        foreach (var user in new[] { _stranger, _foreign })
        {
            using var client = await Client(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(photo.AbstractUrl)).StatusCode);
        }
        using var viewer = await Client(_viewer);
        await using var db = postgres.CreateContext();
        foreach (var reverse in new[] { false, true })
        {
            var block = new Block { BlockerId = reverse ? _viewer.Id : _owner.Id, BlockedUserId = reverse ? _owner.Id : _viewer.Id };
            db.Blocks.Add(block); await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(photo.AbstractUrl)).StatusCode);
            await db.Blocks.ExecuteDeleteAsync(); db.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task MatchPreviewExpiresAndNeverGrantsHigherStages()
    {
        var photo = await Store();
        await using var db = postgres.CreateContext(); await db.Conversations.ExecuteDeleteAsync();
        var match = new DailyMatch { CampusId = _owner.CampusId, UserAId = _owner.Id, UserBId = _viewer.Id,
            CycleDate = DateOnly.FromDateTime(_factory.Clock.GetUtcNow().UtcDateTime) };
        db.Matches.Add(match); await db.SaveChangesAsync();
        using var viewer = await Client(_viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(photo.AbstractUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(At(photo.OriginalUrl, RevealClarityStage.Quarter25))).StatusCode);
        await db.Matches.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MatchStatus.Accepted));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(photo.AbstractUrl)).StatusCode);
        await db.Matches.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MatchStatus.Rejected));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(photo.AbstractUrl)).StatusCode);
        await db.Matches.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MatchStatus.Pending));
        _factory.Clock.Advance(TimeSpan.FromDays(1));
        // Use the storage service directly because the original access token has expired as well.
        using var scope = _factory.Services.CreateScope();
        await Assert.ThrowsAsync<Vouch.Application.Common.EligibilityException>(() => scope.ServiceProvider.GetRequiredService<IPhotoStorageService>()
            .ReadPhotoAsync(_viewer.Id, _owner.Id, Guid.ParseExact(photo.OriginalUrl.Split('/')[3].Substring(33, 32), "N"), RevealClarityStage.Abstract0));
    }

    [Fact]
    public async Task ReplacementIsAtomicConcurrentAndDeletionIsIdempotent()
    {
        var old = await Store(); using var owner = await Client(_owner);
        var replacements = await Task.WhenAll(Store(), Store());
        await using var db = postgres.CreateContext();
        var asset = await db.PhotoAssets.SingleAsync();
        var current = await db.Users.AsNoTracking().SingleAsync(u => u.Id == _owner.Id);
        Assert.Equal(PhotoUrls.Create(_owner.Id, asset.Id, RevealClarityStage.Full100), current.OriginalPhotoUrl);
        Assert.Contains(current.OriginalPhotoUrl, replacements.Select(x => x.OriginalUrl));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(old.OriginalUrl)).StatusCode);
        using (var scope = _factory.Services.CreateScope())
            await Assert.ThrowsAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IPhotoStorageService>()
                .StorePhotoAsync(_owner.Id, new MemoryStream([1, 2, 3]), "image/png"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(current.OriginalPhotoUrl!)).StatusCode);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION photo_test_fail() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''Injected photo write failure''; END'; CREATE TRIGGER photo_test_fail BEFORE INSERT ON \"PhotoAssets\" FOR EACH ROW EXECUTE FUNCTION photo_test_fail();");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Store()); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER photo_test_fail ON \"PhotoAssets\"; DROP FUNCTION photo_test_fail();"); }
        Assert.Equal(asset.Id, await db.PhotoAssets.AsNoTracking().Select(p => p.Id).SingleAsync());
        Assert.Equal(current.OriginalPhotoUrl, await db.Users.AsNoTracking().Where(u => u.Id == _owner.Id).Select(u => u.OriginalPhotoUrl).SingleAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/profile/photo")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/profile/photo")).StatusCode);
        Assert.Empty(await db.PhotoAssets.AsNoTracking().ToListAsync());
        Assert.Null(await db.Users.AsNoTracking().Where(u => u.Id == _owner.Id).Select(u => u.OriginalPhotoUrl).SingleAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(current.OriginalPhotoUrl!)).StatusCode);
    }

    [Fact]
    public async Task StoredVariantsAreEncryptedAndMaskedReadsDoNotDecryptOriginal()
    {
        var photo = await Store();
        await using var connection = new NpgsqlConnection(postgres.Database.ConnectionString); await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT \"OriginalJpeg\", \"AbstractJpeg\", \"QuarterJpeg\", \"SixtyJpeg\" FROM \"PhotoAssets\"", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            for (var i = 0; i < 4; i++) Assert.StartsWith("vouch:v1:", reader.GetString(i));
        }
        await using (var corrupt = new NpgsqlCommand("UPDATE \"PhotoAssets\" SET \"OriginalJpeg\"='vouch:v1:broken'", connection)) await corrupt.ExecuteNonQueryAsync();
        using var viewer = await Client(_viewer); using var owner = await Client(_owner);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(photo.AbstractUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await owner.GetAsync(photo.OriginalUrl)).StatusCode);
    }

    [Fact]
    public async Task CiphertextCopiedAcrossOwnersFailsBindingAndUpgradeInspection()
    {
        var first = await Store(); await Store(_viewer.Id);
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"PhotoAssets\" SET \"AbstractJpeg\"=(SELECT \"AbstractJpeg\" FROM \"PhotoAssets\" WHERE \"OwnerId\"={_owner.Id}) WHERE \"OwnerId\"={_viewer.Id}");
        using var viewer = await Client(_viewer);
        var own = await db.Users.AsNoTracking().SingleAsync(u => u.Id == _viewer.Id);
        Assert.Equal(HttpStatusCode.InternalServerError, (await viewer.GetAsync(At(own.OriginalPhotoUrl!, RevealClarityStage.Abstract0))).StatusCode);
        var inspection = await new ProtectedDataUpgrade(db, PostgresFixture.Encryption, PostgresFixture.Lookup).RunAsync(apply: false);
        Assert.Contains(inspection.Issues, i => i.Table == "PhotoAssets" && i.Column == "AbstractJpeg");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(first.AbstractUrl)).StatusCode);
    }

    [Fact]
    public async Task UploadMetadataRestartAndCascadeWorkWithoutFilesystemStorage()
    {
        using var owner = await Client(_owner);
        using var form = new MultipartFormDataContent(); var file = new ByteArrayContent(Image()); file.Headers.ContentType = new("image/png");
        form.Add(file, "photo", "private.png");
        var response = await owner.PutAsync("/api/profile/photo", form); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var urls = (await response.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(JsonValueKind.String, urls.GetProperty("quarterUrl").ValueKind);
        Assert.Equal(JsonValueKind.String, urls.GetProperty("sixtyUrl").ValueKind);
        Assert.Equal(urls.ToString(), (await owner.GetFromJsonAsync<JsonElement>("/api/profile/photo")).ToString());
        await using (var restarted = new VouchApiFactory(postgres.Database.ConnectionString, enablePhotos: true))
        {
            using var client = restarted.Client(); client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(urls.GetProperty("originalUrl").GetString())).StatusCode);
        }
        await using var db = postgres.CreateContext(); await db.Conversations.ExecuteDeleteAsync();
        await db.Users.Where(u => u.Id == _owner.Id).ExecuteDeleteAsync(); Assert.Empty(await db.PhotoAssets.ToListAsync());
    }

    [Fact]
    public async Task MissingAndLegacyPhotosStayOptionalAndInvalidFormsDoNotCreateAssets()
    {
        using var owner = await Client(_owner);
        var empty = await owner.GetFromJsonAsync<JsonElement>("/api/profile/photo");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("originalUrl").ValueKind);
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _owner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.OriginalPhotoUrl, $"/photos/original/{_owner.Id:N}.jpg"));
        Assert.Equal(JsonValueKind.Null, (await owner.GetFromJsonAsync<JsonElement>("/api/profile/photo")).GetProperty("originalUrl").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/photos/original/{_owner.Id:N}.jpg")).StatusCode);
        using var form = new MultipartFormDataContent();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync("/api/profile/photo", form)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await owner.PutAsJsonAsync("/api/profile/photo", new { photo = "invalid" })).StatusCode);
        Assert.Empty(await db.PhotoAssets.ToListAsync());
    }

    [Theory]
    [InlineData(2, 100, "image/png", HttpStatusCode.BadRequest)]
    [InlineData(1, 100, "text/plain", HttpStatusCode.BadRequest)]
    [InlineData(1, 5 * 1024 * 1024 + 1, "image/png", HttpStatusCode.BadRequest)]
    [InlineData(1, 6 * 1024 * 1024 + 1, "image/png", HttpStatusCode.RequestEntityTooLarge)]
    public async Task InvalidUploadsPreserveTheExistingGeneration(int files, int size, string mime, HttpStatusCode expected)
    {
        var photo = await Store(); using var owner = await Client(_owner);
        using var form = new MultipartFormDataContent();
        for (var i = 0; i < files; i++)
        {
            var content = new ByteArrayContent(new byte[size]); content.Headers.ContentType = new(mime);
            form.Add(content, "photo", "invalid.png");
        }
        Assert.Equal(expected, (await owner.PutAsync("/api/profile/photo", form)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(photo.OriginalUrl)).StatusCode);
        await using var db = postgres.CreateContext(); Assert.Single(await db.PhotoAssets.ToListAsync());
    }

    [Theory]
    [InlineData(AccountStatus.InIncubation)]
    [InlineData(AccountStatus.Suspended)]
    [InlineData(AccountStatus.DeletionRequested)]
    public async Task OwnerStateImmediatelyRestrictsPeerAccess(AccountStatus status)
    {
        var photo = await Store(); using var viewer = await Client(_viewer);
        await using var db = postgres.CreateContext();
        await db.Users.Where(u => u.Id == _owner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(photo.AbstractUrl)).StatusCode);
        if (status == AccountStatus.InIncubation)
        {
            using var owner = await Client(_owner);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(photo.OriginalUrl)).StatusCode);
        }
    }

    [Fact]
    public async Task UnverifiedOwnersAndUnknownRevealStagesFailClosed()
    {
        var photo = await Store(); using var viewer = await Client(_viewer);
        await using var db = postgres.CreateContext();
        await db.Conversations.ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentClarityStage, (RevealClarityStage)999));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(photo.OriginalUrl)).StatusCode);
        await db.Conversations.ExecuteUpdateAsync(s => s.SetProperty(c => c.CurrentClarityStage, RevealClarityStage.Full100).SetProperty(c => c.Status, ConversationStatus.Archived));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(photo.OriginalUrl)).StatusCode);
        await db.Users.Where(u => u.Id == _owner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, (DateTimeOffset?)null));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(photo.AbstractUrl)).StatusCode);
        using var owner = await Client(_owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(photo.OriginalUrl)).StatusCode);
    }

    [Fact]
    public async Task CachedMatchPhotoPointerTracksReplacementAndRemoval()
    {
        var first = await Store(); await using var db = postgres.CreateContext();
        db.Matches.Add(new DailyMatch { CampusId = _owner.CampusId, UserAId = _owner.Id, UserBId = _viewer.Id, CycleDate = DateOnly.FromDateTime(DateTime.UtcNow) });
        await db.SaveChangesAsync();
        async Task<string?> Pointer()
        {
            using var scope = _factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IMatchService>().GetTodayConnectionAsync(_viewer.Id)).Match!.AbstractPhotoUrl;
        }
        Assert.Equal(first.AbstractUrl, await Pointer());
        var next = await Store(); Assert.Equal(next.AbstractUrl, await Pointer());
        using (var scope = _factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<IPhotoStorageService>().DeletePhotoAsync(_owner.Id);
        Assert.Null(await Pointer());
    }

    [Fact]
    public async Task KeyRotationPreservesAllFourPhotoPayloads()
    {
        var photo = await Store();
        var settings = new ConfigurationBuilder().AddConfiguration(PostgresFixture.TestSettings).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:ActiveEncryptionKeyId"] = "v2", ["Security:EncryptionKeys:v2"] = "photo-rotation-test-secret-at-least-48-characters-long"
        }).Build();
        var encryption = new AesEncryptionService(settings);
        await using var db = new ApplicationDbContext(postgres.Options, encryption, PostgresFixture.Lookup);
        var before = await db.PhotoAssets.AsNoTracking().SingleAsync();
        Assert.True((await new ProtectedDataUpgrade(db, encryption, PostgresFixture.Lookup).RunAsync(apply: true)).CanApply);
        var after = await db.PhotoAssets.AsNoTracking().SingleAsync();
        Assert.Equal(before.OriginalJpeg, after.OriginalJpeg); Assert.Equal(before.AbstractJpeg, after.AbstractJpeg);
        Assert.Equal(before.QuarterJpeg, after.QuarterJpeg); Assert.Equal(before.SixtyJpeg, after.SixtyJpeg);
        var service = new DatabasePhotoStorageService(db, new PhotoProcessingService(), _factory.Clock);
        foreach (var stage in Enum.GetValues<RevealClarityStage>()) Assert.NotEmpty((await service.ReadPhotoAsync(_owner.Id, _owner.Id, after.Id, stage))!);
        await using var connection = new NpgsqlConnection(postgres.Database.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"OriginalJpeg\", \"AbstractJpeg\", \"QuarterJpeg\", \"SixtyJpeg\" FROM \"PhotoAssets\"", connection);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        for (var i = 0; i < 4; i++) Assert.StartsWith("vouch:v1:v2:", reader.GetString(i));
        // Restore the fixture key before the next test reuses its protected-data readiness marker.
        await reader.DisposeAsync(); await connection.DisposeAsync();
        var restoreSettings = new ConfigurationBuilder().AddConfiguration(settings).AddInMemoryCollection(new Dictionary<string, string?> { ["Security:ActiveEncryptionKeyId"] = "v1" }).Build();
        var restoreEncryption = new AesEncryptionService(restoreSettings);
        await using var restoreDb = new ApplicationDbContext(postgres.Options, restoreEncryption, PostgresFixture.Lookup);
        await new ProtectedDataUpgrade(restoreDb, restoreEncryption, PostgresFixture.Lookup).RunAsync(apply: true);
    }
}
