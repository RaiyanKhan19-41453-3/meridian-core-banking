namespace CoreBanking.Api.Entities;

/// <summary>
/// One balanced journal in the general ledger, in USD. Every commercial event
/// (a ledger posting, an accrual, a fee) produces exactly one journal whose
/// entries sum to zero.
/// </summary>
public class GlJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly number, e.g. GLJ-20261104-483920.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Subledger transaction this journal belongs to (1:1 here).</summary>
    public Guid? TransactionId { get; set; }

    /// <summary>Business date the entry is effective (day-end batches use the batch date).</summary>
    public DateTime BusinessDate { get; set; }

    /// <summary>USD per 1 unit of the transaction currency, at posting time.</summary>
    public decimal UsdRate { get; set; }

    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GlEntry> Entries { get; set; } = new List<GlEntry>();
}
