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
[Route("api/overdraft")]
public class OverdraftController : ControllerBase
{
    private readonly OverdraftService _overdrafts;
    private readonly AppDbContext _db;

    public OverdraftController(OverdraftService overdrafts, AppDbContext db)
    {
        _overdrafts = overdrafts;
        _db = db;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    private Guid? CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>Term sheet for both overdraft products (ODL-VAR unsecured, ODL-SEC collateral-backed).</summary>
    [HttpGet("terms")]
    public IActionResult Terms() => Ok(OverdraftService.Terms());

    /// <summary>Apply for an overdraft. ODL-VAR: scored decision. ODL-SEC: requires a pledged Savings account as collateral (limit ≤ 90% of pledge).</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyOverdraftRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var facility = await _overdrafts.ApplyAsync(userId.Value, req, ClientIp);
        return facility.Status == "Active"
            ? StatusCode(StatusCodes.Status201Created, facility)
            : Ok(facility); // rejected applications return 200 with the decision
    }

    /// <summary>My facilities (all statuses), newest first, with live utilisation.</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        return Ok(await _overdrafts.ListMineAsync(userId.Value));
    }

    /// <summary>Close a facility — balance must be ≥ 0 (owner or admin).</summary>
    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, [FromBody] CloseOverdraftRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        return Ok(await _overdrafts.CloseAsync(userId.Value, IsAdmin, id, req.Reason, ClientIp));
    }

    /// <summary>Accrued interest memo rows for one of your facilities.</summary>
    [HttpGet("{id:guid}/accruals")]
    public async Task<IActionResult> Accruals(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 60)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var facility = await _db.OverdraftFacilities.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id);
        if (facility is null) throw BankException.NotFound("facility_not_found", "Facility does not exist.");
        if (facility.UserId != userId && !IsAdmin)
            throw BankException.Forbidden("not_facility_owner", "This facility belongs to another customer.");

        var rows = await _db.OverdraftAccruals.AsNoTracking()
            .Where(a => a.FacilityId == id)
            .OrderByDescending(a => a.AccrualDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new OverdraftAccrualDto(a.Id, a.AccrualDate, a.Amount, a.Charged, a.ChargedAt))
            .ToListAsync();

        return Ok(rows);
    }
}
