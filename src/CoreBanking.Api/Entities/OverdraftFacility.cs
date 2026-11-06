namespace CoreBanking.Api.Entities;

public enum OverdraftStatus
{
    Pending,
    Active,
    Closed,
    Rejected
}

/// <summary>
/// Revolving credit limit attached to a Current account. No money moves at
/// approval — the facility simply raises the withdrawable headroom of the
/// account to Limit above its balance. USD v1. Interest accrues daily only
/// on overdrawn balances and is charged monthly (see BatchService).
/// </summary>
public class OverdraftFacility
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly reference, e.g. OD-2026-000001. Unique.</summary>
    public string Reference { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>ODL-VAR (unsecured, scored) or ODL-SEC (collateral-backed).</summary>
    public string ProductCode { get; set; } = "ODL-VAR";

    public decimal Limit { get; set; }
    public decimal AnnualInterestRate { get; set; }

    public int CreditScore { get; set; }
    public string DecisionReason { get; set; } = string.Empty;
    public OverdraftStatus Status { get; set; } = OverdraftStatus.Active;

    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public string? CloseReason { get; set; }

    // ---------- collateral (ODL-SEC only) ----------
    /// <summary>Savings account pledged as security — funds below CollateralAmount cannot be withdrawn.</summary>
    public Guid? CollateralAccountId { get; set; }
    public decimal? CollateralAmount { get; set; }

    public Account Account { get; set; } = null!;
    public Account? CollateralAccount { get; set; }
    public ICollection<OverdraftAccrual> Accruals { get; set; } = new List<OverdraftAccrual>();
}
