namespace CoreBanking.Api.Entities;

public enum AccountType
{
    Savings,
    Current
}

public enum AccountStatus
{
    Active,
    Frozen,
    Closed
}

public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AccountNumber { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public AccountType Type { get; set; }
    public string Currency { get; set; } = "USD";
    public AccountStatus Status { get; set; } = AccountStatus.Active;

    /// <summary>
    /// Cached balance. Source of truth is the ledger (LedgerEntry rows).
    /// Always updated inside the same DB transaction as the ledger insert.
    /// </summary>
    public decimal Balance { get; set; }

    /// <summary>Optimistic concurrency token — prevents lost updates on Balance.</summary>
    public int Version { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Product (pricing/terms). Null only for system/omnibus accounts.</summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    public User User { get; set; } = null!;
    public ICollection<LedgerEntry> LedgerEntries { get; set; } = new List<LedgerEntry>();
}
