namespace CoreBanking.Api.Entities;

/// <summary>
/// Immutable ledger line. Amount is signed relative to the account:
/// positive = credit, negative = debit. Sum of all entries per transaction == 0.
/// </summary>
public class LedgerEntry
{
    public long Id { get; set; }
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Balance of the account immediately after this entry posted.</summary>
    public decimal BalanceAfter { get; set; }

    public DateTime PostedAt { get; set; } = DateTime.UtcNow;

    public Transaction Transaction { get; set; } = null!;
    public Account Account { get; set; } = null!;
}
