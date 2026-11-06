using System.ComponentModel.DataAnnotations;
using CoreBanking.Api.Entities;

namespace CoreBanking.Api.Dtos;

public record OpenAccountRequest(
    [Required] AccountType Type,
    [Required, StringLength(3, MinimumLength = 3)] string Currency);

public record AccountResponse(
    Guid Id,
    string AccountNumber,
    string Type,
    string Currency,
    string Status,
    decimal Balance,
    DateTime CreatedAt);
