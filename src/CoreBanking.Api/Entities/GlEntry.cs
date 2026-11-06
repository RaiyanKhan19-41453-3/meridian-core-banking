namespace CoreBanking.Api.Entities;

/// <summary>
/// Immutable GL leg. Amount is signed in USD; legs of one journal sum to zero.
/// </summary>
public class GlEntry
{
    public long Id { get; set; }
    public Guid JournalId { get; set; }
    public Guid GlAccountId { get; set; }

    /// <summary>Signed amount in USD (converted at the journal's FX rate).</summary>
    public decimal Amount { get; set; }

    public GlJournal Journal { get; set; } = null!;
    public GlAccount GlAccount { get; set; } = null!;
}
