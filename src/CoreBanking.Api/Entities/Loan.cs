namespace CoreBanking.Api.Entities;

public enum LoanStatus
{
    Approved,   // credit decision passed — schedule generated, awaiting acceptance & disbursement
    Active,     // funds disbursed, installments accruing
    Completed,  // all installments paid
    Rejected,   // credit decision declined
    Cancelled   // customer cancelled before disbursement
}

/// <summary>
/// A loan contract. Schedule is generated at approval; disbursement posts the
/// principal to the customer's account and flips the loan to Active.
/// Repayments flow through the day-end batch (auto-debit, no overdraft).
/// </summary>
public class Loan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly reference, e.g. LN-2026-000001. Unique.</summary>
    public string Reference { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public Guid LoanProductId { get; set; }

    public decimal Principal { get; set; }
    public decimal AnnualInterestRate { get; set; }
    public int TermMonths { get; set; }
    public decimal OriginationFee { get; set; }

    public int CreditScore { get; set; }
    public string DecisionReason { get; set; } = string.Empty;
    public LoanStatus Status { get; set; }

    /// <summary>Account the loan was (or will be) disbursed into and repaid from. Must be Active, owned by the customer, USD.</summary>
    public Guid DisbursementAccountId { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
    public DateTime? DisbursedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Guid? DisbursementTransactionId { get; set; }

    public LoanProduct LoanProduct { get; set; } = null!;
    public ICollection<LoanInstallment> Installments { get; set; } = new List<LoanInstallment>();
}
