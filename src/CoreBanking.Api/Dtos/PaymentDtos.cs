using System.ComponentModel.DataAnnotations;
using CoreBanking.Api.Entities;

namespace CoreBanking.Api.Dtos;

public record AddBeneficiaryRequest(
    [Required, MaxLength(80)] string Nickname,
    [Required, MaxLength(32)] string AccountNumber,
    [MaxLength(20)] string? BankCode);

public record BeneficiaryDto(Guid Id, string Nickname, string AccountNumber,
    string BankCode, string Currency, DateTime CreatedAt);

public record SchedulePaymentRequest(
    [Required] Guid FromAccountId,
    Guid? BeneficiaryId,
    string? ToAccountNumber,
    [Range(typeof(decimal), "0.0001", "100000000")] decimal Amount,
    [MaxLength(500)] string? Description,
    PaymentFrequency Frequency,
    [Required] DateTime ScheduledDate);

public record ScheduledPaymentDto(Guid Id, string ToAccountNumber, decimal Amount, string Currency,
    string Frequency, DateTime ScheduledDate, string Status, string Rail,
    Guid? LastTransactionId, string? LastError);
