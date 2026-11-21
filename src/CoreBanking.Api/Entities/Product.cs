namespace CoreBanking.Api.Entities;

public enum ProductType
{
    Savings,
    Current,
    TermDeposit
}

/// <summary>
/// Bank product definition — interest terms and fee policy. Accounts reference
/// exactly one product; pricing changes apply to future accruals/fees.
/// </summary>
public class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable code used by seeding/tests, e.g. SAV-STD, CUR-PER.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public ProductType Type { get; set; }

    /// <summary>Currency the pricing is denominated in (default USD; BDT products are denominated in BDT).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Annual interest rate, e.g. 2.5 means 2.5% p.a. 0 = non-interest-bearing.</summary>
    public decimal AnnualInterestRate { get; set; }

    /// <summary>Flat monthly account fee charged when collected.</summary>
    public decimal MonthlyFee { get; set; }

    /// <summary>Fee waived entirely when balance is at or above this threshold at collection time.</summary>
    public decimal FeeWaiverMinBalance { get; set; }

    /// <summary>
    /// Second-tier waiver: half the monthly fee is charged when the balance is
    /// at or above this (lower) threshold. 0 disables the reduced-fee tier.
    /// </summary>
    public decimal FeeWaiverReducedBalance { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
