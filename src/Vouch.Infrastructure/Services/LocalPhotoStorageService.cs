using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.Services;

/// <summary>
/// Step 21 — REQ-17: Local-disk photo storage implementation.
///
/// Stores photos under PhotoStorage:BasePath (default: wwwroot/photos/).
/// Serves them at PhotoStorage:BaseUrl (set to your CDN/static host in production).
///
/// Abstract variant: Gaussian blur (radius 18) applied via ImageSharp to evoke
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

    public LocalPhotoStorageService(IConfiguration configuration, ILogger<LocalPhotoStorageService> logger)
    {
        _basePath = configuration["PhotoStorage:BasePath"]
            ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "photos");
        _baseUrl = configuration["PhotoStorage:BaseUrl"]?.TrimEnd('/')
            ?? "/photos";
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

        if (stream.Length > MaxFileSizeBytes)
            throw new ArgumentException($"File size exceeds the 5 MB limit.");

        var fileName = $"{userId:N}.jpg"; // always output as JPEG
        var originalPath = Path.Combine(_basePath, "original", fileName);
        var abstractPath = Path.Combine(_basePath, "abstract", fileName);

        // Load and normalize the image using ImageSharp
        using var image = await Image.LoadAsync(stream, ct);

        // Resize if either dimension exceeds MaxDimension (maintains aspect ratio)
        if (image.Width > MaxDimension || image.Height > MaxDimension)
        {
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(MaxDimension, MaxDimension),
                Mode = ResizeMode.Max
            }));
        }

        // Save original
        await image.SaveAsJpegAsync(originalPath, ct);
        _logger.LogInformation("Photo uploaded for user {UserId}: original saved.", userId);

        // Create abstract variant: strong Gaussian blur (REQ-17 oil-painting effect)
        image.Mutate(ctx => ctx.GaussianBlur(BlurRadius));
        await image.SaveAsJpegAsync(abstractPath, ct);
        _logger.LogInformation("Photo uploaded for user {UserId}: abstract variant saved.", userId);

        var originalUrl = $"{_baseUrl}/original/{fileName}";
        var abstractUrl = $"{_baseUrl}/abstract/{fileName}";

        return (originalUrl, abstractUrl);
    }
}
