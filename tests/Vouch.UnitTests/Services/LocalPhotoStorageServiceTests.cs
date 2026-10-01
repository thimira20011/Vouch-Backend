using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Vouch.Infrastructure.Services;

namespace Vouch.UnitTests.Services;

public sealed class LocalPhotoStorageServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vouch-photo-tests", Guid.NewGuid().ToString("N"));

    private LocalPhotoStorageService CreateService() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PhotoStorage:BasePath"] = _directory,
            ["PhotoStorage:BaseUrl"] = ""
        }).Build(), NullLogger<LocalPhotoStorageService>.Instance);

    [Theory]
    [InlineData(MagickFormat.Jpeg, "image/jpeg")]
    [InlineData(MagickFormat.Png, "image/png")]
    [InlineData(MagickFormat.WebP, "image/webp")]
    public async Task Upload_NormalizesSupportedFormatsAndProducesDistinctBlur(MagickFormat format, string contentType)
    {
        using var source = new MagickImage(MagickColors.Black, 1600, 800);
        new Drawables().FillColor(MagickColors.White).Rectangle(800, 0, 1599, 799).Draw(source);
        using var stream = new MemoryStream(source.ToByteArray(format));
        var userId = Guid.NewGuid();
        var urls = await CreateService().StorePhotoAsync(userId, stream, contentType);
        Assert.Equal($"/photos/original/{userId:N}.jpg", urls.OriginalUrl);
        Assert.Equal($"/photos/abstract/{userId:N}.jpg", urls.AbstractUrl);
        var originalPath = Path.Combine(_directory, "original", $"{userId:N}.jpg");
        var abstractPath = Path.Combine(_directory, "abstract", $"{userId:N}.jpg");
        using var original = new MagickImage(originalPath);
        using var abstractImage = new MagickImage(abstractPath);
        Assert.Equal(MagickFormat.Jpeg, original.Format);
        Assert.Equal(1024u, original.Width);
        Assert.Equal(512u, original.Height);
        Assert.Equal(original.Width, abstractImage.Width);
        Assert.Equal(original.Height, abstractImage.Height);
        Assert.False(File.ReadAllBytes(originalPath).SequenceEqual(File.ReadAllBytes(abstractPath)));
    }

    [Fact]
    public async Task Upload_InvalidImageIsRejectedWithoutWritingFiles()
    {
        using var stream = new MemoryStream("not an image"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().StorePhotoAsync(Guid.NewGuid(), stream, "image/png"));
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_UnsupportedTypeIsRejected()
    {
        using var stream = new MemoryStream("<svg/>"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().StorePhotoAsync(Guid.NewGuid(), stream, "image/svg+xml"));
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_OversizedNonSeekableStreamIsRejected()
    {
        using var stream = new NonSeekableStream(new byte[5 * 1024 * 1024 + 1]);
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().StorePhotoAsync(Guid.NewGuid(), stream, "image/png"));
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_ValidNonSeekableStreamIsAccepted()
    {
        using var image = new MagickImage(MagickColors.White, 32, 16);
        using var stream = new NonSeekableStream(image.ToByteArray(MagickFormat.Png));
        var userId = Guid.NewGuid();
        await CreateService().StorePhotoAsync(userId, stream, "image/png");
        using var original = new MagickImage(Path.Combine(_directory, "original", $"{userId:N}.jpg"));
        Assert.Equal(32u, original.Width);
        Assert.Equal(16u, original.Height);
    }

    [Fact]
    public async Task Upload_ExcessiveDimensionsAreRejectedBeforeWriting()
    {
        using var image = new MagickImage(MagickColors.White, 5001, 5000);
        using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().StorePhotoAsync(Guid.NewGuid(), stream, "image/png"));
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
