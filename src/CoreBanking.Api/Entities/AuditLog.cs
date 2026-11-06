namespace CoreBanking.Api.Entities;

/// <summary>
/// Append-only audit trail for compliance (who did what, when, from where).
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Payload { get; set; } // JSON
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
