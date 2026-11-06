using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace CoreBanking.Api.Dtos;

public record FreezeAccountRequest([MaxLength(500)] string? Reason);
public record ReverseTransactionRequest([MaxLength(500)] string? Description);

public record DayEndRequest(DateTime? Date);
public record CapitalizeInterestRequest([Required] DateTime AsOf);
public record ChargeFeesRequest([Range(2000, 9999)] int Year, [Range(1, 12)] int Month);

public record BatchRunDto(
    long Id,
    string JobType,
    DateTime RunDate,
    string Status,
    int ItemsProcessed,
    int ItemsFailed,
    JsonElement? Summary,
    JsonElement? Errors,
    DateTime StartedAt,
    DateTime? FinishedAt,
    bool Replayed);

public record TrialBalanceLine(string Code, string Name, string Type, decimal Balance, decimal Debit, decimal Credit);
public record TrialBalance(IReadOnlyList<TrialBalanceLine> Lines, decimal TotalDebits, decimal TotalCredits, bool Balanced);

public record GlLegDto(string Code, string Name, decimal Amount);
public record GlJournalDto(Guid Id, string Number, Guid? TransactionId, DateTime BusinessDate,
    decimal UsdRate, string Description, DateTime CreatedAt, IReadOnlyList<GlLegDto> Legs);
public record GlJournalPage(IReadOnlyList<GlJournalDto> Items, int Page, int PageSize, int Total, int TotalPages);
