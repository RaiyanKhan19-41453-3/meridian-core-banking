using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Controllers;

[ApiController]
[Authorize]
[EnableRateLimiting("money")]
[Route("api/transactions")]
public class TransactionsController : ControllerBase
{
    private readonly LedgerService _ledger;
    private readonly AppDbContext _db;

    public TransactionsController(LedgerService ledger, AppDbContext db)
    {
        _ledger = ledger;
        _db = db;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string IdempotencyKey => Request.Headers["Idempotency-Key"].ToString();

    private Guid? CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private static MoneyResponse ToResponse(LedgerResult result, decimal amount, string currency) =>
        new(result.Transaction.Id, result.Transaction.Reference,
            result.Transaction.Type.ToString(), result.Transaction.Status.ToString(),
            amount, currency, result.Transaction.Description,
            result.Transaction.CreatedAt, result.Replayed);

    [HttpPost("deposit")]
    public async Task<IActionResult> Deposit([FromBody] DepositRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var hash = RequestFingerprint.Compute(req);
        var result = await _ledger.DepositAsync(
            userId.Value, req.AccountId, req.Amount, req.Description, IdempotencyKey, hash, ClientIp);

        var currency = await _db.Accounts.Where(a => a.Id == req.AccountId)
            .Select(a => a.Currency).SingleAsync();
        return StatusCode(result.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created,
            ToResponse(result, req.Amount, currency));
    }

    [HttpPost("withdraw")]
    public async Task<IActionResult> Withdraw([FromBody] WithdrawRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var hash = RequestFingerprint.Compute(req);
        var result = await _ledger.WithdrawAsync(
            userId.Value, req.AccountId, req.Amount, req.Description, IdempotencyKey, hash, ClientIp);

        var currency = await _db.Accounts.Where(a => a.Id == req.AccountId)
            .Select(a => a.Currency).SingleAsync();
        return StatusCode(result.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created,
            ToResponse(result, req.Amount, currency));
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var hash = RequestFingerprint.Compute(req);
        var result = await _ledger.TransferAsync(
            userId.Value, req.FromAccountId, req.ToAccountNumber, req.Amount,
            req.Description, IdempotencyKey, hash, ClientIp);

        var currency = await _db.Accounts.Where(a => a.Id == req.FromAccountId)
            .Select(a => a.Currency).SingleAsync();
        return StatusCode(result.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created,
            ToResponse(result, req.Amount, currency));
    }
}
