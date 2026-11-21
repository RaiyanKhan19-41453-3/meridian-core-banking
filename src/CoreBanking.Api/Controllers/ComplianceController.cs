using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Api.Controllers;

/// <summary>Regulatory reporting: CTR/SAR, filings, tax statements, reconciliation,
/// sanctions-list refresh.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ComplianceController : ControllerBase
{
    private readonly ComplianceService _compliance;

    public ComplianceController(ComplianceService compliance) => _compliance = compliance;

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private Guid? CurrentUserId => Guid.TryParse(
        User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
        out var id) ? id : null;
    private bool IsAdmin => User.IsInRole("Admin");

    [HttpGet("ctr")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Ctr([FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
        => Ok(await _compliance.CtrAsync(fromDate ?? DateTime.UtcNow.AddYears(-1), toDate ?? DateTime.UtcNow));

    [HttpGet("sar")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Sar([FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate)
        => Ok(await _compliance.SarAsync(fromDate ?? DateTime.UtcNow.AddYears(-1), toDate ?? DateTime.UtcNow));

    [HttpPost("alerts/{id:guid}/file")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> File(Guid id, [FromBody] FilingRequest req)
    {
        var adminId = CurrentUserId;
        if (adminId is null) return Unauthorized();
        var filed = await _compliance.FileAsync(id, req.Reference, adminId.Value, ClientIp);
        return Ok(new { filed });
    }

    [HttpGet("tax-statement")]
    public async Task<IActionResult> TaxStatement([FromQuery] int year, [FromQuery] Guid? userId = null)
    {
        var caller = CurrentUserId;
        if (caller is null) return Unauthorized();
        if (!IsAdmin) userId = caller; // customers can only see their own
        if (year < 2000 || year > DateTime.UtcNow.Year)
            throw BankException.BadRequest("invalid_year", "Year is out of range.");
        return Ok(await _compliance.TaxStatementsAsync(userId, year));
    }

    [HttpGet("reconciliation")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reconciliation() => Ok(await _compliance.ReconciliationAsync());

    [HttpPost("watchlist/refresh")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RefreshWatchlist([FromBody] WatchlistRefreshRequest req)
    {
        var adminId = CurrentUserId;
        if (adminId is null) return Unauthorized();
        var (added, skipped) = await _compliance.RefreshWatchlistAsync(req.Entries, adminId.Value, ClientIp);
        return Ok(new { added, skipped });
    }
}
