using System.ComponentModel.DataAnnotations;

namespace CoreBanking.Api.Dtos;

public record DepositRequest(
    [Required] Guid AccountId,
    [Range(typeof(decimal), "0.0001", "250000", ErrorMessage = "Amount must be between 0.0001 and 250,000.")]
    decimal Amount,
    [MaxLength(500)] string? Description);

public record WithdrawRequest(
    [Required] Guid AccountId,
    [Range(typeof(decimal), "0.0001", "250000", ErrorMessage = "Amount must be between 0.0001 and 250,000.")]
    decimal Amount,
    [MaxLength(500)] string? Description);

public record TransferRequest(
    [Required] Guid FromAccountId,
    [Required, MaxLength(32)] string ToAccountNumber,
    [Range(typeof(decimal), "0.0001", "250000", ErrorMessage = "Amount must be between 0.0001 and 250,000.")]
    decimal Amount,
    [MaxLength(500)] string? Description);

public record MoneyResponse(
    Guid TransactionId,
    string Reference,
    string Type,
    string Status,
    decimal Amount,
    string Currency,
    string Description,
    DateTime PostedAtUtc,
    bool Replayed);

public record StatementLine(
    long EntryId,
    Guid TransactionId,
    string Reference,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    string Description,
    DateTime PostedAt);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total, int TotalPages);
