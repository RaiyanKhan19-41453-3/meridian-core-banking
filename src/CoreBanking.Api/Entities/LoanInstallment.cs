namespace CoreBanking.Api.Entities;

public enum LoanInstallmentStatus
{
    Pending,
    Paid,
    Overdue // auto-debit attempted but insufficient funds — retried at next day-end
}

/// <summary>One scheduled EMI payment. Amounts in product currency (USD v1).</summary>
public class LoanInstallment
{
    public long Id { get; set; }
    public Guid LoanId { get; set; }

    public int InstallmentNo { get; set; }
    public DateTime DueDate { get; set; }

    public decimal Principal { get; set; }
    public decimal Interest { get; set; }
    public decimal Total { get; set; }

    public LoanInstallmentStatus Status { get; set; } = LoanInstallmentStatus.Pending;
    public DateTime? PaidAt { get; set; }
    public Guid? PaymentTransactionId { get; set; }

    public Loan Loan { get; set; } = null!;
}
