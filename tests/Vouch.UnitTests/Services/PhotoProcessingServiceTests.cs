using ImageMagick;
using ImageMagick.Drawing;
using Vouch.Infrastructure.Services;

namespace Vouch.UnitTests.Services;

public class PhotoProcessingServiceTests
{
    public static MagickImage Fixture(uint width = 320, uint height = 480)
    {
        var image = new MagickImage(new MagickColor("#91a9b7"), width, height);
        var sx = width / 320d; var sy = height / 480d;
        new Drawables().FillColor(new MagickColor("#34576a")).Rectangle(25*sx, 320*sy, 295*sx, 479*sy)
            .FillColor(new MagickColor("#d9ad87")).Ellipse(160*sx, 180*sy, 82*sx, 110*sy, 0, 360)
            .FillColor(new MagickColor("#3b2a26")).Rectangle(80*sx, 65*sy, 240*sx, 115*sy)
            .FillColor(MagickColors.White).Ellipse(125*sx, 165*sy, 16*sx, 10*sy, 0, 360).Ellipse(195*sx, 165*sy, 16*sx, 10*sy, 0, 360)
            .FillColor(MagickColors.Black).Circle(128*sx, 165*sy, 133*sx, 165*sy).Circle(192*sx, 165*sy, 197*sx, 165*sy)
            .StrokeColor(new MagickColor("#704839")).StrokeWidth(3).Line(160*sx, 180*sy, 150*sx, 215*sy).Line(135*sx, 240*sy, 185*sx, 240*sy)
            .Draw(image);
        return image;
    }
    [Theory]
    [InlineData(MagickFormat.Jpeg, "image/jpeg")] [InlineData(MagickFormat.Png, "image/png")] [InlineData(MagickFormat.WebP, "image/webp")]
    public async Task SupportedFormatsCreateFourDistinctNormalizedJpegs(MagickFormat format, string mime)
    {
        using var source = Fixture(1600, 800); using var stream = new MemoryStream(source.ToByteArray(format));
        var result = await new PhotoProcessingService().ProcessAsync(stream, mime);
        Assert.Equal(1024u, result.Width); Assert.Equal(512u, result.Height);
        var variants = new[] { result.Abstract, result.Quarter, result.Sixty, result.Original };
        foreach (var bytes in variants)
        {
            using var image = new MagickImage(bytes); Assert.Equal(MagickFormat.Jpeg, image.Format);
            Assert.Equal(result.Width, image.Width); Assert.Equal(result.Height, image.Height); Assert.Null(image.GetExifProfile());
        }
        Assert.False(result.Abstract.SequenceEqual(result.Quarter)); Assert.False(result.Quarter.SequenceEqual(result.Sixty)); Assert.False(result.Sixty.SequenceEqual(result.Original));
    }
    [Theory]
    [InlineData("image/svg+xml")] [InlineData("image/gif")] [InlineData("image/jpeg")] [InlineData("image/png")] [InlineData("image/webp")]
    public async Task UnsupportedOrMalformedImagesAreRejected(string mime)
    {
        using var stream = new MemoryStream("not an image"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentException>(() => new PhotoProcessingService().ProcessAsync(stream, mime));
    }
    [Fact]
    public async Task DeclaredFormatMustMatchActualData()
    {
        using var image = Fixture(); using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        await Assert.ThrowsAsync<ArgumentException>(() => new PhotoProcessingService().ProcessAsync(stream, "image/jpeg"));
    }
    [Fact]
    public async Task NonSeekableUploadsAreBoundedAndSupported()
    {
        using var source = Fixture(); using var valid = new NonSeekable(source.ToByteArray(MagickFormat.Png));
        Assert.Equal(320u, (await new PhotoProcessingService().ProcessAsync(valid, "image/png")).Width);
        using var huge = new NonSeekable(new byte[PhotoProcessingService.MaxUploadBytes + 1]);
        await Assert.ThrowsAsync<ArgumentException>(() => new PhotoProcessingService().ProcessAsync(huge, "image/png"));
    }
    [Theory]
    [InlineData(5001, 5000)] [InlineData(8193, 1)]
    public async Task ExcessiveDimensionsAreRejectedBeforePixelDecode(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.White, 1, 1);
        var png = image.ToByteArray(MagickFormat.Png);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), height);
        uint crc = uint.MaxValue;
        foreach (var value in png.AsSpan(12, 17))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), ~crc);
        using var stream = new MemoryStream(png);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new PhotoProcessingService().ProcessAsync(stream, "image/png"));
        Assert.Contains("dimensions", error.Message);
    }
    [Fact]
    public async Task OrientationIsAppliedAndPrivateMetadataIsStripped()
    {
        using var image = Fixture(80, 40); var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6); exif.SetValue(ExifTag.Artist, "Private photographer"); image.SetProfile(exif);
        image.Orientation = OrientationType.RightTop;
        image.Comment = "Private comment"; using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Jpeg));
        var result = await new PhotoProcessingService().ProcessAsync(stream, "image/jpeg");
        Assert.Equal(40u, result.Width); Assert.Equal(80u, result.Height);
        foreach (var bytes in new[] { result.Original, result.Abstract, result.Quarter, result.Sixty })
        {
            using var clean = new MagickImage(bytes); Assert.Null(clean.GetExifProfile()); Assert.True(string.IsNullOrEmpty(clean.Comment));
            Assert.Null(clean.GetProfile("xmp")); Assert.Null(clean.GetProfile("iptc"));
        }
    }
    [Fact]
    public async Task CancelledProcessingDoesNotReturnAResult()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var image = Fixture(); using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PhotoProcessingService().ProcessAsync(stream, "image/png", cancel.Token));
    }
    [Fact]
    public async Task SyntheticPreviewCanBeExportedForVisualReview()
    {
        using var image = Fixture(); using var stream = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        var result = await new PhotoProcessingService().ProcessAsync(stream, "image/png");
        var path = Environment.GetEnvironmentVariable("VOUCH_PHOTO_PREVIEW_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var collection = new MagickImageCollection();
        foreach (var bytes in new[] { result.Abstract, result.Quarter, result.Sixty, result.Original }) collection.Add(new MagickImage(bytes));
        using var sheet = collection.AppendHorizontally(); sheet.Write(path, MagickFormat.Png);
    }
    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
