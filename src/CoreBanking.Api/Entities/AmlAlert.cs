namespace CoreBanking.Api.Entities;

public enum AmlAlertType
{
    HighValue,      // single money movement >= 10,000 (CTR-threshold style)
    Velocity24h,    // outbound volume of one account >= 25,000 in 24h
    Burst,          // >= 5 outbound transactions in 1h
    WatchlistMatch  // legal name matches seeded watchlist at profile submission
}

public enum AmlAlertStatus
{
    Open,
    Reviewed,
    Dismissed
}

public enum AmlSeverity
{
    Low,
    Medium,
    High
}

/// <summary>
/// AML monitoring alert. Created by the rule engine after a posting (or at
/// KYC submission for watchlist matches). Money movement is never rolled back
/// because of an alert — alerts are for human review.
/// </summary>
public class AmlAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AmlAlertType AlertType { get; set; }
    public AmlSeverity Severity { get; set; }
    public AmlAlertStatus Status { get; set; } = AmlAlertStatus.Open;

    public Guid UserId { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? TransactionId { get; set; }

    public string Details { get; set; } = string.Empty; // JSON: amount, currency, rule inputs, matched entry

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
}
