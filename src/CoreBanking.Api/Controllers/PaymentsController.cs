using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Api.Controllers;

/// <summary>Beneficiary management and scheduled payments on the sandbox rail.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly PaymentService _payments;

    public PaymentsController(PaymentService payments) => _payments = payments;

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private Guid? CurrentUserId => Guid.TryParse(
        User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
        out var id) ? id : null;

    [HttpGet("beneficiaries")]
    public async Task<IActionResult> Beneficiaries()
    {
        var id = CurrentUserId;
        if (id is null) return Unauthorized();
        return Ok(await _payments.ListBeneficiariesAsync(id.Value));
    }

    [HttpPost("beneficiaries")]
    public async Task<IActionResult> AddBeneficiary([FromBody] AddBeneficiaryRequest req)
    {
        var id = CurrentUserId;
        if (id is null) return Unauthorized();
        return StatusCode(StatusCodes.Status201Created,
            await _payments.AddBeneficiaryAsync(id.Value, req, ClientIp));
    }

    [HttpDelete("beneficiaries/{id:guid}")]
    public async Task<IActionResult> RemoveBeneficiary(Guid id)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();
        await _payments.RemoveBeneficiaryAsync(userId.Value, id, ClientIp);
        return NoContent();
    }

    [HttpGet("scheduled")]
    public async Task<IActionResult> Scheduled()
    {
        var id = CurrentUserId;
        if (id is null) return Unauthorized();
        return Ok(await _payments.ListScheduledAsync(id.Value));
    }

    [HttpPost("scheduled")]
    public async Task<IActionResult> Schedule([FromBody] SchedulePaymentRequest req)
    {
        var id = CurrentUserId;
        if (id is null) return Unauthorized();
        return StatusCode(StatusCodes.Status201Created,
            await _payments.ScheduleAsync(id.Value, req, ClientIp));
    }

    [HttpPost("scheduled/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();
        await _payments.CancelScheduledAsync(userId.Value, id, ClientIp);
        return NoContent();
    }
}
