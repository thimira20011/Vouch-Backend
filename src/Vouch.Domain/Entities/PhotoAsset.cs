namespace Vouch.Domain.Entities;

// One current generation per owner. Ciphertext lives in the database, never in wwwroot.
public class PhotoAsset : BaseEntity
{
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public uint Width { get; set; }
    public uint Height { get; set; }
    public string ProcessingVersion { get; set; } = "paint-v1";
    public required string OriginalJpeg { get; set; }
    public required string AbstractJpeg { get; set; }
    public required string QuarterJpeg { get; set; }
    public required string SixtyJpeg { get; set; }
}
