using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ImageMagick;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.Services;

/// <summary>
/// Step 21 — REQ-17: Local-disk photo storage implementation.
///
/// Stores photos under PhotoStorage:BasePath (default: wwwroot/photos/).
/// Serves them at PhotoStorage:BaseUrl (set to your CDN/static host in production).
///
/// Abstract variant: Gaussian blur (sigma 18) applied via Magick.NET to evoke
/// the "oil-painting abstract" visual style (REQ-4 / REQ-17) without requiring
/// an external AI service. Swap the processing pipeline here for any ML API later.
///
/// For production: replace this class with a MinioPhotoStorageService or
/// AzureBlobPhotoStorageService while keeping the same IPhotoStorageService contract.
/// </summary>
public sealed class LocalPhotoStorageService : IPhotoStorageService
{
    private readonly string _basePath;
    private readonly string _baseUrl;
    private readonly ILogger<LocalPhotoStorageService> _logger;

    // Allowed MIME types for uploads
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };

    // Max file size: 5 MB
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    // Gaussian blur radius for abstract variant (higher = more abstract)
    private const float BlurRadius = 18f;

    // Dimensions to normalize to before storing (reduces storage/bandwidth)
    private const int MaxDimension = 1024;
    private const ulong MaxPixelCount = 25_000_000;

    public LocalPhotoStorageService(IConfiguration configuration, ILogger<LocalPhotoStorageService> logger)
    {
        var configuredPath = configuration["PhotoStorage:BasePath"];
        _basePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "photos")
            : configuredPath;
        var configuredUrl = configuration["PhotoStorage:BaseUrl"];
        _baseUrl = string.IsNullOrWhiteSpace(configuredUrl)
            ? "/photos"
            : configuredUrl.TrimEnd('/');
        _logger = logger;

        Directory.CreateDirectory(Path.Combine(_basePath, "original"));
        Directory.CreateDirectory(Path.Combine(_basePath, "abstract"));
    }

    public async Task<(string OriginalUrl, string AbstractUrl)> StorePhotoAsync(
        Guid userId,
        Stream stream,
        string contentType,
        CancellationToken ct = default)
    {
        if (!AllowedContentTypes.Contains(contentType))
            throw new ArgumentException($"Unsupported image format '{contentType}'. Allowed: JPEG, PNG, WebP.");

        // Bound reads even for streams that do not support seeking or Length.
        using var upload = new MemoryStream();
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (upload.Length + bytesRead > MaxFileSizeBytes)
                throw new ArgumentException("File size exceeds the 5 MB limit.");
            await upload.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
        }
        if (upload.Length == 0)
            throw new ArgumentException("Photo must not be empty.");

        var format = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => MagickFormat.Jpeg,
            "image/png" => MagickFormat.Png,
            _ => MagickFormat.WebP
        };
        var settings = new MagickReadSettings { Format = format };
        using var image = new MagickImage();
        try
        {
            upload.Position = 0;
            image.Ping(upload, settings);
            if ((ulong)image.Width * image.Height > MaxPixelCount)
                throw new ArgumentException("Photo must not exceed 25 million pixels.");
            upload.Position = 0;
            image.Read(upload, settings);
        }
        catch (MagickException ex)
        {
            throw new ArgumentException("Photo data is invalid or does not match the declared image format.", ex);
        }
        ct.ThrowIfCancellationRequested();
        image.AutoOrient();
        image.Strip();
        if (image.Width > MaxDimension || image.Height > MaxDimension)
            image.Resize(new MagickGeometry(MaxDimension, MaxDimension));
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
        image.Format = MagickFormat.Jpeg;

        var fileName = $"{userId:N}.jpg"; // always output as JPEG
        var originalPath = Path.Combine(_basePath, "original", fileName);
        var abstractPath = Path.Combine(_basePath, "abstract", fileName);

        // Save original
        await image.WriteAsync(originalPath, ct);
        _logger.LogInformation("Photo uploaded for user {UserId}: original saved.", userId);

        // Create abstract variant: strong Gaussian blur (REQ-17 oil-painting effect)
        image.GaussianBlur(0, BlurRadius);
        await image.WriteAsync(abstractPath, ct);
        _logger.LogInformation("Photo uploaded for user {UserId}: abstract variant saved.", userId);

        var originalUrl = $"{_baseUrl}/original/{fileName}";
        var abstractUrl = $"{_baseUrl}/abstract/{fileName}";

        return (originalUrl, abstractUrl);
    }
}
