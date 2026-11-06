namespace CoreBanking.Api.Entities;

public enum KycDocumentType
{
    NationalId,
    Passport,
    ProofOfAddress,
    Selfie
}

/// <summary>
/// Uploaded identity document. Bytes live in the database (bytea) for a
/// self-contained deployment; bytea never leaves the API except through the
/// owner/admin-only download endpoint. Size capped at 2 MB.
/// </summary>
public class KycDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }

    public KycDocumentType DocType { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public string Sha256 { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public Guid UploadedBy { get; set; }

    public CustomerProfile Profile { get; set; } = null!;
}
