namespace CoreBanking.Api.Entities;

public enum LoanProductStatus
{
    Active,
    Retired
}

/// <summary>
/// Loan product terms. Currency is USD in v1. MinCreditScore is the instant
/// decision threshold for applications.
/// </summary>
public class LoanProduct
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty; // e.g. PLN-PER
    public string Name { get; set; } = string.Empty;
    public LoanProductStatus Status { get; set; } = LoanProductStatus.Active;

    public string Currency { get; set; } = "USD";
    public decimal MinPrincipal { get; set; }
    public decimal MaxPrincipal { get; set; }
    public int MinTermMonths { get; set; }
    public int MaxTermMonths { get; set; }

    /// <summary>Annual percentage rate, e.g. 18.00 means 18% p.a., reducing balance.</summary>
    public decimal AnnualInterestRate { get; set; }

    /// <summary>Flat origination fee charged at disbursement.</summary>
    public decimal OriginationFee { get; set; }

    public int MinCreditScore { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
