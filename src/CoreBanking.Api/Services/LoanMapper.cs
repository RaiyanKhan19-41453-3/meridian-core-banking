using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;

namespace CoreBanking.Api.Services;

/// <summary>Builds loan DTOs from loaded entities — no N+1.</summary>
public static class LoanMapper
{
    public static LoanDetailDto ToDetail(Loan loan, string accountNumber)
    {
        var summary = ToSummary(loan);
        var schedule = loan.Installments
            .OrderBy(i => i.InstallmentNo)
            .Select(i => new LoanInstallmentDto(
                i.InstallmentNo, i.DueDate, i.Principal, i.Interest, i.Total,
                i.Status.ToString(), i.PaidAt))
            .ToList();

        return new LoanDetailDto(summary, loan.OriginationFee, loan.DecisionReason,
            loan.DisbursementAccountId, accountNumber, schedule);
    }

    public static LoanSummaryDto ToSummary(Loan loan)
    {
        var installments = loan.Installments.ToList();
        var paid = installments.Count(i => i.Status == LoanInstallmentStatus.Paid);
        var totalPaid = installments.Where(i => i.Status == LoanInstallmentStatus.Paid).Sum(i => i.Total);
        var outstanding = installments.Where(i => i.Status != LoanInstallmentStatus.Paid).Sum(i => i.Total);
        var next = installments
            .Where(i => i.Status != LoanInstallmentStatus.Paid)
            .OrderBy(i => i.DueDate)
            .FirstOrDefault();

        return new LoanSummaryDto(
            loan.Id, loan.Reference,
            loan.LoanProduct?.Code ?? string.Empty, loan.LoanProduct?.Name ?? string.Empty,
            loan.Principal, loan.AnnualInterestRate, loan.TermMonths,
            loan.Status.ToString(), loan.CreditScore,
            loan.Purpose, loan.AppliedAt, loan.DisbursedAt, loan.CompletedAt,
            paid, installments.Count,
            totalPaid, outstanding,
            next?.DueDate, next?.Total);
    }
}
