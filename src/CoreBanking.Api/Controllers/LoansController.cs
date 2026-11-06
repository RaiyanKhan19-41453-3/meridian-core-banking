using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
[Authorize]
[EnableRateLimiting("money")]
[Route("api/loans")]
public class LoansController : ControllerBase
{
    private readonly LoanService _loans;
    private readonly AppDbContext _db;

    public LoansController(LoanService loans, AppDbContext db)
    {
        _loans = loans;
        _db = db;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    private Guid? CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>Loan products on offer.</summary>
    [HttpGet("products")]
    public async Task<IActionResult> Products()
    {
        var items = await _db.LoanProducts.AsNoTracking()
            .Where(p => p.Status == LoanProductStatus.Active)
            .OrderBy(p => p.Code)
            .Select(p => new LoanProductDto(
                p.Code, p.Name, p.Currency, p.MinPrincipal, p.MaxPrincipal,
                p.MinTermMonths, p.MaxTermMonths, p.AnnualInterestRate, p.OriginationFee, p.MinCreditScore))
            .ToListAsync();
        return Ok(items);
    }

    /// <summary>Apply for a loan. Decision is instant and deterministic.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyLoanRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var loan = await _loans.ApplyAsync(userId.Value, req, ClientIp);
        var detail = await LoadDetailAsync(loan.Id);
        return Ok(new ApplyLoanResponse(
            loan.Status.ToString(), loan.CreditScore, loan.DecisionReason, detail));
    }

    /// <summary>My loans, newest first.</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var loans = await _db.Loans.AsNoTracking()
            .Include(l => l.LoanProduct)
            .Include(l => l.Installments)
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.AppliedAt)
            .ToListAsync();
        return Ok(loans.Select(LoanMapper.ToSummary));
    }

    /// <summary>Detail incl. schedule — owner or admin.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        await AuthorizeLoanAccessAsync(id, userId.Value);
        return Ok(await LoadDetailAsync(id));
    }

    /// <summary>Accept the offer and disburse into the chosen account (idempotent).</summary>
    [HttpPost("{id:guid}/accept-and-disburse")]
    public async Task<IActionResult> AcceptAndDisburse(Guid id)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        await _loans.DisburseAsync(userId.Value, IsAdmin, id, ClientIp);
        return Ok(await LoadDetailAsync(id));
    }

    /// <summary>Cancel an approved (not yet disbursed) loan — owner only.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        await _loans.CancelAsync(userId.Value, IsAdmin, id, ClientIp);
        return NoContent();
    }

    // ---------- helpers ----------

    private async Task AuthorizeLoanAccessAsync(Guid loanId, Guid userId)
    {
        var owner = await _db.Loans.AsNoTracking()
            .Where(l => l.Id == loanId).Select(l => (Guid?)l.UserId).SingleOrDefaultAsync();
        if (owner is null) throw BankException.NotFound("loan_not_found", "Loan does not exist.");
        if (owner != userId && !IsAdmin)
            throw BankException.Forbidden("not_loan_owner", "This loan belongs to another customer.");
    }

    private async Task<LoanDetailDto> LoadDetailAsync(Guid loanId)
    {
        var loan = await _db.Loans.AsNoTracking()
            .Include(l => l.LoanProduct)
            .Include(l => l.Installments)
            .SingleAsync(l => l.Id == loanId);
        var number = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == loan.DisbursementAccountId)
            .Select(a => a.AccountNumber)
            .SingleAsync();
        return LoanMapper.ToDetail(loan, number);
    }
}
