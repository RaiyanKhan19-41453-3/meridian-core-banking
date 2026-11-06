namespace CoreBanking.Api.Entities;

/// <summary>
/// One day's interest earned by an account — accrued daily, capitalized monthly.
/// Unique (account, accrual_date) makes the day-end batch idempotent.
/// </summary>
public class InterestAccrual
{
    public long Id { get; set; }
    public Guid AccountId { get; set; }
    public DateTime AccrualDate { get; set; }

    /// <summary>Interest for this day, in the account's currency.</summary>
    public decimal Amount { get; set; }

    public bool Capitalized { get; set; }
    public DateTime? CapitalizedAt { get; set; }

    public Guid? CapitalizationTransactionId { get; set; }
}
