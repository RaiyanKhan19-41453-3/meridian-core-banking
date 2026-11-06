namespace CoreBanking.Api.Entities;

public enum TransactionType
{
    Deposit,
    Withdrawal,
    Transfer,
    Fee,
    Interest,
    Reversal,
    LoanDisbursement,
    LoanRepayment
}

public enum TransactionStatus
{
    Posted,
    Reversed
}

/// <summary>
/// A business-level money movement. Every transaction posts 2+ ledger entries
/// whose signed amounts sum to zero (double-entry bookkeeping).
/// </summary>
public class Transaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Client-supplied idempotency key (unique). Replays with the same key
    /// return the original transaction instead of double-posting.
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>SHA-256 of the request payload — detects idempotency-key reuse with a different body.</summary>
    public string? RequestHash { get; set; }

    /// <summary>Set when this transaction reverses a previous one.</summary>
    public Guid? OriginalTransactionId { get; set; }

    public TransactionType Type { get; set; }
    public TransactionStatus Status { get; set; } = TransactionStatus.Posted;
    public string Description { get; set; } = string.Empty;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<LedgerEntry> Entries { get; set; } = new List<LedgerEntry>();
}
