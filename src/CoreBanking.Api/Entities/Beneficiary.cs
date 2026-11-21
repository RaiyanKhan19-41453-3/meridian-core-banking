namespace CoreBanking.Api.Entities;

/// <summary>A saved payee for the sandbox payment rail. Account numbers must
/// resolve to an account in this deployment — no external rail is wired.</summary>
public class Beneficiary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string BankCode { get; set; } = "MERIDIAN";
    public string Currency { get; set; } = "USD";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum PaymentFrequency
{
    Once,
    Weekly,
    Monthly
}

public enum ScheduledPaymentStatus
{
    Pending,
    Processing,
    Executed,
    Failed,
    Cancelled
}

/// <summary>A one-off or recurring payment queued for the sandbox rail.</summary>
public class ScheduledPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? BeneficiaryId { get; set; }
    public Guid FromAccountId { get; set; }
    public string ToAccountNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Description { get; set; }
    public PaymentFrequency Frequency { get; set; } = PaymentFrequency.Once;
    public DateTime ScheduledDate { get; set; }
    public ScheduledPaymentStatus Status { get; set; } = ScheduledPaymentStatus.Pending;
    public string Rail { get; set; } = "sandbox";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastExecutedAt { get; set; }
    public Guid? LastTransactionId { get; set; }
    public string? LastError { get; set; }
}
