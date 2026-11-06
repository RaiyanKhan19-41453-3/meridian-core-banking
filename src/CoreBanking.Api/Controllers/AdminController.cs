using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LedgerService _ledger;
    private readonly AuditService _audit;
    private readonly BatchService _batch;

    public AdminController(AppDbContext db, LedgerService ledger, AuditService audit, BatchService batch)
    {
        _db = db;
        _ledger = ledger;
        _audit = audit;
        _batch = batch;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string IdempotencyKey => Request.Headers["Idempotency-Key"].ToString();

    private Guid? AdminId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    // ---------- account lifecycle ----------

    [HttpPost("accounts/{id:guid}/freeze")]
    public Task<IActionResult> Freeze(Guid id, [FromBody] FreezeAccountRequest req) =>
        SetStatusAsync(id, AccountStatus.Frozen, req.Reason, "account.freeze");

    [HttpPost("accounts/{id:guid}/unfreeze")]
    public Task<IActionResult> Unfreeze(Guid id, [FromBody] FreezeAccountRequest req) =>
        SetStatusAsync(id, AccountStatus.Active, req.Reason, "account.unfreeze");

    [HttpPost("accounts/{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, [FromBody] FreezeAccountRequest req)
    {
        var adminId = AdminId();
        if (adminId is null) return Unauthorized();

        var account = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == id);
        if (account is null) throw BankException.NotFound("account_not_found", "Account does not exist.");
        if (account.Status == AccountStatus.Closed) return NoContent(); // idempotent
        if (account.Balance != 0m)
            throw BankException.Unprocessable("balance_not_zero",
                $"Account cannot be closed with a balance of {account.Balance:F2}. Drain it first.");

        account.Status = AccountStatus.Closed;
        account.Version += 1;
        _audit.Write(adminId, "account.close", nameof(Account), account.Id.ToString(),
            new { account.AccountNumber, reason = req.Reason }, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<IActionResult> SetStatusAsync(Guid id, AccountStatus status, string? reason, string action)
    {
        var adminId = AdminId();
        if (adminId is null) return Unauthorized();

        var account = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == id);
        if (account is null) throw BankException.NotFound("account_not_found", "Account does not exist.");
        if (account.Status == AccountStatus.Closed && status != AccountStatus.Closed)
            throw BankException.Conflict("account_closed", "Closed accounts cannot be reopened.");
        if (account.Status == status) return NoContent(); // idempotent

        account.Status = status;
        account.Version += 1;
        _audit.Write(adminId, action, nameof(Account), account.Id.ToString(),
            new { account.AccountNumber, reason }, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- reversal ----------

    [HttpPost("transactions/{id:guid}/reverse")]
    public async Task<IActionResult> Reverse(Guid id, [FromBody] ReverseTransactionRequest req)
    {
        var adminId = AdminId();
        if (adminId is null) return Unauthorized();

        var hash = RequestFingerprint.Compute(new { id, req.Description });
        var result = await _ledger.ReverseAsync(adminId.Value, id, req.Description, IdempotencyKey, hash, ClientIp);

        var firstEntry = result.Entries.First();
        var currency = await _db.Accounts.Where(a => a.Id == firstEntry.AccountId)
            .Select(a => a.Currency).SingleAsync();
        return StatusCode(result.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created,
            new MoneyResponse(result.Transaction.Id, result.Transaction.Reference,
                result.Transaction.Type.ToString(), result.Transaction.Status.ToString(),
                Math.Abs(firstEntry.Amount), currency,
                result.Transaction.Description, result.Transaction.CreatedAt, result.Replayed));
    }

    // ---------- batch jobs (day-end / month-end) ----------

    private static BatchRunDto ToDto(CoreBanking.Api.Entities.BatchRun run, bool replayed)
    {
        JsonElement? Summary = null, Errors = null;
        try { if (run.Summary is not null) Summary = JsonSerializer.Deserialize<JsonElement>(run.Summary); } catch { }
        try { if (run.Errors is not null) Errors = JsonSerializer.Deserialize<JsonElement>(run.Errors); } catch { }
        return new BatchRunDto(run.Id, run.JobType.ToString(), run.RunDate, run.Status.ToString(),
            run.ItemsProcessed, run.ItemsFailed, Summary, Errors, run.StartedAt, run.FinishedAt, replayed);
    }

    [HttpPost("batch/day-end")]
    public async Task<IActionResult> RunDayEnd([FromBody] DayEndRequest req)
    {
        var result = await _batch.RunDayEndAsync(req.Date, AdminId(), ClientIp);
        return Ok(ToDto(result.Run, result.Replayed));
    }

    [HttpPost("batch/capitalize-interest")]
    public async Task<IActionResult> CapitalizeInterest([FromBody] CapitalizeInterestRequest req)
    {
        var asOf = req.AsOf.Date;
        if (asOf > DateTime.UtcNow.Date)
            throw BankException.BadRequest("future_date", "Cannot capitalize interest for a future date.");
        var result = await _batch.RunCapitalizationAsync(asOf, AdminId(), ClientIp);
        return Ok(ToDto(result.Run, result.Replayed));
    }

    [HttpPost("batch/charge-fees")]
    public async Task<IActionResult> ChargeFees([FromBody] ChargeFeesRequest req)
    {
        var result = await _batch.RunFeeChargeAsync(req.Year, req.Month, AdminId(), ClientIp);
        return Ok(ToDto(result.Run, result.Replayed));
    }

    [HttpGet("batch/runs")]
    public async Task<IActionResult> BatchRuns([FromQuery] int count = 20) =>
        Ok((await _batch.RecentRunsAsync(count)).Select(r => ToDto(r, false)));

    [HttpPost("batch/process-repayments")]
    public async Task<IActionResult> ProcessRepayments([FromBody] DayEndRequest req)
    {
        // No future-date restriction: unlike accrual/interest jobs this is a
        // pure cutoff — every currently-due installment is an eligible candidate.
        var date = req.Date?.Date ?? DateTime.UtcNow.Date;
        var result = await _batch.RunLoanRepaymentsAsync(date, AdminId(), ClientIp);
        return Ok(ToDto(result.Run, result.Replayed));
    }

    [HttpPost("batch/charge-overdraft-interest")]
    public async Task<IActionResult> ChargeOverdraftInterest([FromBody] ChargeFeesRequest req)
    {
        var result = await _batch.RunOverdraftChargeAsync(req.Year, req.Month, AdminId(), ClientIp);
        return Ok(ToDto(result.Run, result.Replayed));
    }

    [HttpPost("batch/accrue-overdraft-interest")]
    public async Task<IActionResult> AccrueOverdraftInterest([FromBody] DayEndRequest req)
    {
        var date = req.Date?.Date ?? DateTime.UtcNow.Date;
        var result = await _batch.RunOverdraftAccrualAsync(date, AdminId(), ClientIp);
        return Ok(ToDto(result.Run, result.Replayed));
    }

    // ---------- overdraft portfolio ----------

    /// <summary>All overdraft facilities with live utilisation, newest first.</summary>
    [HttpGet("overdraft")]
    public async Task<IActionResult> Overdrafts(
        [FromQuery] string? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        OverdraftStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<OverdraftStatus>(status, ignoreCase: true, out var s))
                throw BankException.BadRequest("invalid_status",
                    "status must be Pending, Active, Closed or Rejected.");
            parsed = s;
        }

        var query = _db.OverdraftFacilities.AsNoTracking().Include(f => f.Account).AsQueryable();
        if (parsed is not null) query = query.Where(f => f.Status == parsed);

        var total = await query.CountAsync();
        var facilities = await query
            .OrderByDescending(f => f.AppliedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var userIds = facilities.Select(f => f.UserId).Distinct().ToList();
        var emails = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email);

        var collateralAccountIds = facilities
            .Where(f => f.CollateralAccountId != null)
            .Select(f => f.CollateralAccountId!.Value)
            .Distinct()
            .ToList();
        var collateralNumbers = collateralAccountIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Accounts.AsNoTracking()
                .Where(a => collateralAccountIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.AccountNumber);

        var items = facilities.Select(f => new
        {
            f.Id,
            f.Reference,
            customer = emails.TryGetValue(f.UserId, out var e) ? e : "?",
            accountNumber = f.Account.AccountNumber,
            f.Limit,
            f.AnnualInterestRate,
            status = f.Status.ToString(),
            f.CreditScore,
            balance = f.Account.Balance,
            utilized = Math.Max(0m, -f.Account.Balance),
            available = f.Status == OverdraftStatus.Active
                ? Math.Max(0m, f.Limit - Math.Max(0m, -f.Account.Balance))
                : 0m,
            f.AppliedAt,
            f.ClosedAt,
            productCode = f.ProductCode,
            collateralAccountNumber = f.CollateralAccountId != null && collateralNumbers.TryGetValue(f.CollateralAccountId.Value, out var cn) ? cn : null
        }).ToList();

        return Ok(new { items, page, pageSize, total });
    }

    // ---------- loan portfolio oversight ----------

    /// <summary>All loans, newest application first. status filter optional: Approved|Active|Completed|Rejected|Cancelled.</summary>
    [HttpGet("loans")]
    public async Task<IActionResult> Loans(
        [FromQuery] string? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        LoanStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<LoanStatus>(status, ignoreCase: true, out var s))
                throw BankException.BadRequest("invalid_status", "status must be Approved, Active, Completed, Rejected or Cancelled.");
            parsed = s;
        }

        var query = _db.Loans.AsNoTracking()
            .Include(l => l.LoanProduct)
            .Include(l => l.Installments)
            .AsQueryable();
        if (parsed is not null) query = query.Where(l => l.Status == parsed);

        var total = await query.CountAsync();
        var loans = await query
            .OrderByDescending(l => l.AppliedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var userIds = loans.Select(l => l.UserId).Distinct().ToList();
        var emails = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email);

        var items = loans.Select(l => new
        {
            l.Id,
            l.Reference,
            customer = emails.TryGetValue(l.UserId, out var e) ? e : "?",
            product = l.LoanProduct.Code,
            l.Principal,
            l.AnnualInterestRate,
            l.TermMonths,
            status = l.Status.ToString(),
            l.CreditScore,
            l.AppliedAt,
            l.DisbursedAt,
            installmentsPaid = l.Installments.Count(i => i.Status == LoanInstallmentStatus.Paid),
            installmentsTotal = l.Installments.Count,
            outstanding = l.Installments
                .Where(i => i.Status != LoanInstallmentStatus.Paid).Sum(i => i.Total),
            overdueInstallments = l.Installments.Count(i => i.Status == LoanInstallmentStatus.Overdue)
        }).ToList();

        return Ok(new { items, page, pageSize, total });
    }

    // ---------- general ledger ----------

    [HttpGet("gl/trial-balance")]
    public async Task<IActionResult> TrialBalance()
    {
        var sums = await _db.GlEntries
            .GroupBy(e => e.GlAccountId)
            .Select(g => new { GlAccountId = g.Key, Sum = g.Sum(e => e.Amount) })
            .ToListAsync();

        var accountIds = sums.Select(s => s.GlAccountId).ToList();
        var accounts = await _db.GlAccounts.Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);

        var lines = sums
            .Select(s =>
            {
                var a = accounts[s.GlAccountId];
                // GL is stored debit-positive — no per-type flipping needed
                var debit = Math.Max(s.Sum, 0m);
                var credit = Math.Max(-s.Sum, 0m);
                return new TrialBalanceLine(a.Code, a.Name, a.Type.ToString(), s.Sum, debit, credit);
            })
            .OrderBy(l => l.Code)
            .ToList();

        var totalDebits = lines.Sum(l => l.Debit);
        var totalCredits = lines.Sum(l => l.Credit);
        return Ok(new TrialBalance(lines, totalDebits, totalCredits, totalDebits == totalCredits));
    }

    [HttpGet("gl/journals")]
    public async Task<IActionResult> Journals(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var toExclusive = toDate?.Date.AddDays(1);

        var query = _db.GlJournals.AsNoTracking()
            .Where(j => (fromDate == null || j.BusinessDate >= fromDate.Value.Date)
                     && (toExclusive == null || j.BusinessDate < toExclusive))
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id);

        var total = await query.CountAsync();
        var items = await query
            .Include(j => j.Entries).ThenInclude(e => e.GlAccount)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new GlJournalDto(j.Id, j.Number, j.TransactionId, j.BusinessDate, j.UsdRate,
                j.Description, j.CreatedAt,
                j.Entries.OrderBy(e => e.GlAccount.Code)
                    .Select(e => new GlLegDto(e.GlAccount.Code, e.GlAccount.Name, e.Amount))
                    .ToList()))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return Ok(new GlJournalPage(items, page, pageSize, total, totalPages));
    }

    // ---------- audit log ----------

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? action = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id, a.UserId, a.Action, a.EntityName, a.EntityId,
                a.Payload, a.IpAddress, a.CreatedAt
            })
            .ToListAsync();

        return Ok(new { items, page, pageSize, total });
    }
}
