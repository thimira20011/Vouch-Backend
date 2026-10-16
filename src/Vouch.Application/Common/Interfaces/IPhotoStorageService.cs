namespace Vouch.Application.Common.Interfaces;

/// <summary>
/// Private, encrypted photo generations. Returned URLs always require authorization.
/// </summary>
public interface IPhotoStorageService
{
    /// <summary>
    /// Atomically replaces the owner's normalized original and three reveal variants.
    /// </summary>
    /// <param name="userId">Verified, onboarded owner.</param>
    /// <param name="stream">Readable photo stream (JPEG, PNG or WebP).</param>
    /// <param name="contentType">Declared MIME type, checked against the image signature.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (originalUrl, abstractUrl).</returns>
    Task<(string OriginalUrl, string AbstractUrl)> StorePhotoAsync(
        Guid userId,
        Stream stream,
        string contentType,
        CancellationToken ct = default);

    Task<byte[]?> ReadPhotoAsync(Guid viewerId, Guid ownerId, Guid generationId,
        Vouch.Domain.Enums.RevealClarityStage stage, CancellationToken ct = default);
    Task DeletePhotoAsync(Guid userId, CancellationToken ct = default);
}
