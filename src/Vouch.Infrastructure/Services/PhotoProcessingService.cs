using ImageMagick;
using Vouch.Infrastructure.Persistence;

namespace Vouch.Infrastructure.Services;

public sealed record ProcessedPhoto(byte[] Original, byte[] Abstract, byte[] Quarter, byte[] Sixty, uint Width, uint Height);

public sealed class PhotoProcessingService
{
    public const int MaxUploadBytes = 5 * 1024 * 1024;
    private static readonly SemaphoreSlim ProcessingSlots = new(2);
    static PhotoProcessingService()
    {
        // Fail rather than spill a private image's pixel cache to a plaintext temp file.
        ResourceLimits.Disk = 0;
        ResourceLimits.Memory = 256 * 1024 * 1024;
        ResourceLimits.Thread = 2;
    }
    public async Task<ProcessedPhoto> ProcessAsync(Stream stream, string contentType, CancellationToken ct = default)
    {
        var format = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => MagickFormat.Jpeg, "image/png" => MagickFormat.Png,
            "image/webp" => MagickFormat.WebP, _ => throw new ArgumentException("Allowed photo formats: JPEG, PNG, WebP.")
        };
        await ProcessingSlots.WaitAsync(ct);
        try
        {
            using var upload = new MemoryStream();
            var buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (upload.Length + read > MaxUploadBytes) throw new ArgumentException("Photo exceeds the 5 MB limit.");
                await upload.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var data = upload.GetBuffer().AsSpan(0, (int)upload.Length);
            var signatureMatches = format switch
            {
                MagickFormat.Jpeg => data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff,
                MagickFormat.Png => data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                _ => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)
            };
            if (!signatureMatches) throw new ArgumentException("Photo data does not match the declared image format.");
            ct.ThrowIfCancellationRequested();
            // Multi-frame inputs are deliberately reduced to their first frame.
            var settings = new MagickReadSettings { Format = format, FrameIndex = 0, FrameCount = 1 };
            using var image = new MagickImage();
            upload.Position = 0; image.Ping(upload, settings);
            if (image.Width == 0 || image.Height == 0 || image.Width > 8192 || image.Height > 8192 || (ulong)image.Width * image.Height > 25_000_000)
                throw new ArgumentException("Photo dimensions must be at most 8192 per side and 25 million pixels.");
            upload.Position = 0; image.Read(upload, settings); image.AutoOrient();
            image.ColorSpace = ColorSpace.sRGB; image.Strip();
            if (image.Width > 1024 || image.Height > 1024) image.Resize(new MagickGeometry(1024, 1024));
            image.BackgroundColor = MagickColors.White; image.Alpha(AlphaOption.Remove);
            image.Format = MagickFormat.Jpeg; image.Quality = 85;
            var original = Encode(image);
            var abstractImage = Render(image, 20, 3, 3, 2.5, ct);
            var quarter = Render(image, 56, 6, 2, 1.4, ct);
            var sixty = Render(image, 192, 16, 1, 0.6, ct);
            ct.ThrowIfCancellationRequested();
            return new(original, abstractImage, quarter, sixty, image.Width, image.Height);
        }
        catch (MagickException ex) { throw new ArgumentException("Photo could not be decoded within the supported image limits.", ex); }
        finally { ProcessingSlots.Release(); }
    }
    private static byte[] Render(MagickImage source, uint detail, int levels, double paint, double blur, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var image = source.Clone();
        image.Resize(new MagickGeometry(detail, detail)); image.Posterize(levels);
        image.OilPaint(paint, 1); image.GaussianBlur(0, blur);
        image.Resize(new MagickGeometry(source.Width, source.Height) { IgnoreAspectRatio = true });
        image.Strip(); image.Format = MagickFormat.Jpeg; image.Quality = 85;
        return Encode(image);
    }
    private static byte[] Encode(IMagickImage<byte> image)
    {
        var bytes = image.ToByteArray(MagickFormat.Jpeg);
        if (bytes.Length > PhotoPayload.MaxJpegBytes) throw new ArgumentException("Normalized photo exceeds the storage limit.");
        return bytes;
    }
}
