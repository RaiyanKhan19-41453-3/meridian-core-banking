using System.ComponentModel.DataAnnotations;

namespace CoreBanking.Api.Dtos;

public record LoanProductDto(
    string Code, string Name, string Currency,
    decimal MinPrincipal, decimal MaxPrincipal,
    int MinTermMonths, int MaxTermMonths,
    decimal AnnualInterestRate, decimal OriginationFee, int MinCreditScore);

public record ApplyLoanRequest(
    [Required, MaxLength(20)] string LoanProductCode,
    [Range(typeof(decimal), "100", "100000000")] decimal Principal,
    [Range(1, 360)] int TermMonths,
    [MaxLength(500)] string? Purpose,
    [Required] Guid DisbursementAccountId,
    [Range(300, 850, ErrorMessage = "CIB score must be between 300 and 850.")]
    int? CibScore = null);

public record LoanInstallmentDto(
    int InstallmentNo, DateTime DueDate,
    decimal Principal, decimal Interest, decimal Total,
    string Status, DateTime? PaidAt);

public record LoanSummaryDto(
    Guid Id, string Reference, string ProductCode, string ProductName,
    string Currency,
    decimal Principal, decimal AnnualInterestRate, int TermMonths,
    string Status, int CreditScore, int? CibScore, string ScoreSource,
    string Purpose, DateTime AppliedAt, DateTime? DisbursedAt, DateTime? CompletedAt,
    int InstallmentsPaid, int InstallmentsTotal,
    decimal TotalPaid, decimal TotalOutstanding,
    DateTime? NextDueDate, decimal? NextDueAmount);

public record LoanDetailDto(
    LoanSummaryDto Summary,
    decimal OriginationFee,
    string DecisionReason,
    Guid DisbursementAccountId,
    string DisbursementAccountNumber,
    IReadOnlyList<LoanInstallmentDto> Schedule);

public record ApplyLoanResponse(
    string Decision,          // "Approved" | "Rejected"
    int CreditScore,
    int? CibScore,
    string ScoreSource,
    string Reason,
    LoanDetailDto Loan);
