using System.ComponentModel.DataAnnotations;

namespace CoreBanking.Api.Dtos;

public record OverdraftTermsDto(
    string ProductCode,
    string Currency,
    decimal AnnualInterestRate,
    decimal MinLimit,
    decimal MaxLimit,
    int MinCreditScore,
    decimal? RequiredCollateralRatio,
    bool Secured);

public record ApplyOverdraftRequest(
    [Required] Guid AccountId,
    [StringLength(20)] string ProductCode = "ODL-VAR",
    [Range(typeof(decimal), "500", "3000000", ErrorMessage = "Limit must be between 500 and 3,000,000.")]
    decimal RequestedLimit = 1000m,
    Guid? CollateralAccountId = null,
    [Range(300, 850, ErrorMessage = "CIB score must be between 300 and 850.")]
    int? CibScore = null);

public record CloseOverdraftRequest([MaxLength(500)] string? Reason);

public record OverdraftFacilityDto(
    Guid Id,
    string Reference,
    Guid AccountId,
    string AccountNumber,
    decimal Limit,
    decimal AnnualInterestRate,
    string Status,
    int CreditScore,
    string DecisionReason,
    DateTime AppliedAt,
    DateTime? ApprovedAt,
    DateTime? ClosedAt,
    decimal CurrentBalance,
    decimal AvailableCredit,
    decimal AccruedInterestUncharged,
    string ProductCode,
    Guid? CollateralAccountId,
    decimal? CollateralAmount,
    string? CollateralAccountNumber,
    int? CibScore,
    string ScoreSource);

public record OverdraftAccrualDto(
    long Id,
    DateTime AccrualDate,
    decimal Amount,
    bool Charged,
    DateTime? ChargedAt);
