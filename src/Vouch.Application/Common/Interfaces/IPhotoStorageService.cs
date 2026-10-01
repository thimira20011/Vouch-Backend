namespace Vouch.Application.Common.Interfaces;

/// <summary>
/// Step 21 — REQ-17: Stores user photos and returns their public URLs.
/// The implementation handles both the original and the oil-painting abstract versions.
/// </summary>
public interface IPhotoStorageService
{
    /// <summary>
    /// Stores the raw uploaded photo and produces a blurred abstract variant.
    /// </summary>
    /// <param name="userId">Owner — used to key the file path.</param>
    /// <param name="stream">Readable photo stream (JPEG or PNG).</param>
    /// <param name="contentType">MIME type of the upload (image/jpeg, image/png).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (originalUrl, abstractUrl).</returns>
    Task<(string OriginalUrl, string AbstractUrl)> StorePhotoAsync(
        Guid userId,
        Stream stream,
        string contentType,
        CancellationToken ct = default);
}
