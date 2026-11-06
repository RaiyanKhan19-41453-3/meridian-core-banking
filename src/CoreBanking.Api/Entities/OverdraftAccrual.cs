namespace CoreBanking.Api.Entities;

/// <summary>
/// Memo of one day's overdraft interest owed by a facility's account —
/// computed at day-end for overdrawn accounts only (balance < 0). Charged
/// monthly; unique (facility, date) keeps day-end idempotent.
/// </summary>
public class OverdraftAccrual
{
    public long Id { get; set; }
    public Guid FacilityId { get; set; }
    public Guid AccountId { get; set; }
    public DateTime AccrualDate { get; set; }

    /// <summary>Interest for this day, in the account currency. Always positive.</summary>
    public decimal Amount { get; set; }

    public bool Charged { get; set; }
    public DateTime? ChargedAt { get; set; }
    public Guid? ChargeTransactionId { get; set; }

    public OverdraftFacility Facility { get; set; } = null!;
}
