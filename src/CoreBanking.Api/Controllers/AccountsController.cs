using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/accounts")]
public class AccountsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AccountsController(AppDbContext db) => _db = db;

    private Guid? CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var accounts = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.UserId == userId && !a.AccountNumber.StartsWith("SYS-"))
            .OrderBy(a => a.CreatedAt)
            .Select(a => new AccountResponse(
                a.Id, a.AccountNumber, a.Type.ToString(), a.Currency,
                a.Status.ToString(), a.Balance, a.CreatedAt))
            .ToListAsync();

        return Ok(accounts);
    }

    [HttpPost]
    public async Task<IActionResult> Open([FromBody] OpenAccountRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        await KycGate.RequireVerifiedAsync(_db, userId.Value);

        var currency = req.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3)
            return BadRequest(new { error = "Currency must be a 3-letter ISO code." });

        var account = new Account
        {
            AccountNumber = GenerateAccountNumber(),
            UserId = userId.Value,
            Type = req.Type,
            Currency = currency,
            Status = AccountStatus.Active,
            Balance = 0m,
            Version = 0
        };

        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), new AccountResponse(
            account.Id, account.AccountNumber, account.Type.ToString(), account.Currency,
            account.Status.ToString(), account.Balance, account.CreatedAt));
    }

    /// <summary>
    /// Paginated ledger for one account (owner or admin). format=json (default) or format=csv.
    /// </summary>
    [HttpGet("{id:guid}/statement")]
    public async Task<IActionResult> Statement(
        Guid id,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string format = "json")
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var account = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
        if (account is null) return NotFound(new { error = "Account does not exist." });
        if (account.UserId != userId && !IsAdmin)
            return Forbid();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var toExclusive = toDate?.Date.AddDays(1);

        var baseQuery =
            from e in _db.LedgerEntries
            join t in _db.Transactions on e.TransactionId equals t.Id
            where e.AccountId == id
               && (fromDate == null || e.PostedAt >= fromDate.Value.Date)
               && (toExclusive == null || e.PostedAt < toExclusive)
            orderby e.PostedAt descending, e.Id descending
            select new StatementLine(e.Id, t.Id, t.Reference, t.Type.ToString(),
                e.Amount, e.BalanceAfter, t.Description, e.PostedAt);

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await baseQuery.Take(10_000).ToListAsync();
            var csv = ToCsv(rows);
            return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"statement-{account.AccountNumber}.csv");
        }

        var total = await baseQuery.CountAsync();
        var items = await baseQuery.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return Ok(new PagedResult<StatementLine>(items, page, pageSize, total, totalPages));
    }

    private static string ToCsv(IReadOnlyList<StatementLine> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("posted_at,reference,type,amount,balance_after,description");
        foreach (var r in rows)
        {
            sb.Append(CsvCell(r.PostedAt.ToString("O"))).Append(',')
              .Append(CsvCell(r.Reference)).Append(',')
              .Append(CsvCell(r.Type)).Append(',')
              .Append(r.Amount.ToString("F4")).Append(',')
              .Append(r.BalanceAfter.ToString("F4")).Append(',')
              .Append(CsvCell(r.Description)).AppendLine();
        }
        return sb.ToString();
    }

    private static string CsvCell(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]))
            value = "'" + value; // neutralize spreadsheet formula injection
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string GenerateAccountNumber() =>
        "AC" + Random.Shared.NextInt64(100000000000, 999999999999);
}
