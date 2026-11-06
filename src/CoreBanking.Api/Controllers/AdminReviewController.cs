using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public class AdminReviewController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public AdminReviewController(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private Guid? AdminId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    // ---------- KYC review queue ----------

    /// <summary>Profiles awaiting review (Pending), oldest first.</summary>
    [HttpGet("kyc/pending")]
    public async Task<IActionResult> PendingKyc([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.CustomerProfiles.AsNoTracking()
            .Where(p => p.KycStatus == KycStatus.Pending)
            .OrderBy(p => p.CreatedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PendingProfileDto(
                p.UserId,
                p.User.Email,
                p.FullLegalName,
                p.IdType.ToString(),
                p.IdNumber,
                p.Nationality,
                p.RiskRating.ToString(),
                p.CreatedAt,
                p.Documents.Count))
            .ToListAsync();

        return Ok(new { items, page, pageSize, total });
    }

    /// <summary>Documents of a customer, for review (metadata only — download via /api/kyc/documents/{id}/content as admin).</summary>
    [HttpGet("kyc/{userId:guid}/documents")]
    public async Task<IActionResult> CustomerDocuments(Guid userId)
    {
        var docs = await _db.KycDocuments.AsNoTracking()
            .Where(d => d.Profile.UserId == userId)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new KycDocumentMeta(d.Id, d.DocType.ToString(), d.FileName, d.ContentType,
                d.SizeBytes, d.Sha256, d.UploadedAt))
            .ToListAsync();
        return Ok(docs);
    }

    [HttpPost("kyc/{userId:guid}/approve")]
    public async Task<IActionResult> ApproveKyc(Guid userId)
    {
        var adminId = AdminId();
        if (adminId is null) return Unauthorized();

        var profile = await _db.CustomerProfiles.SingleOrDefaultAsync(p => p.UserId == userId);
        if (profile is null) throw BankException.NotFound("profile_not_found", "Customer has no profile.");
        if (profile.KycStatus == KycStatus.Verified) return NoContent(); // idempotent

        profile.KycStatus = KycStatus.Verified;
        profile.VerifiedAt = DateTime.UtcNow;
        profile.VerifiedBy = adminId.Value;
        profile.RejectionReason = null;
        profile.UpdatedAt = DateTime.UtcNow;

        _audit.Write(adminId, "kyc.approved", nameof(CustomerProfile), profile.Id.ToString(),
            new { profile.UserId, profile.FullLegalName, profile.RiskRating }, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("kyc/{userId:guid}/reject")]
    public async Task<IActionResult> RejectKyc(Guid userId, [FromBody] RejectKycRequest req)
    {
        var adminId = AdminId();
        if (adminId is null) return Unauthorized();

        var profile = await _db.CustomerProfiles.SingleOrDefaultAsync(p => p.UserId == userId);
        if (profile is null) throw BankException.NotFound("profile_not_found", "Customer has no profile.");
        if (profile.KycStatus == KycStatus.Rejected) return NoContent(); // idempotent

        profile.KycStatus = KycStatus.Rejected;
        profile.RejectionReason = req.Reason.Trim();
        profile.VerifiedAt = null;
        profile.VerifiedBy = null;
        profile.UpdatedAt = DateTime.UtcNow;

        _audit.Write(adminId, "kyc.rejected", nameof(CustomerProfile), profile.Id.ToString(),
            new { profile.UserId, profile.FullLegalName, reason = req.Reason }, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- AML alerts ----------

    /// <summary>List alerts, newest first. Default filter: Open.</summary>
    [HttpGet("aml/alerts")]
    public async Task<IActionResult> ListAlerts(
        [FromQuery] string? status = "Open", [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        AmlAlertStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<AmlAlertStatus>(status, ignoreCase: true, out var s))
                throw BankException.BadRequest("invalid_status", "status must be Open, Reviewed, Dismissed or omitted for all.");
            parsed = s;
        }

        var query = _db.AmlAlerts.AsNoTracking();
        if (parsed is not null) query = query.Where(a => a.Status == parsed);

        var total = await query.CountAsync();
        var alerts = await query
            .OrderByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var userIds = alerts.Select(a => a.UserId).Distinct().ToList();
        var emails = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email);

        var items = alerts.Select(a => new AmlAlertDto(
            a.Id, a.AlertType.ToString(), a.Severity.ToString(), a.Status.ToString(),
            a.UserId, emails.TryGetValue(a.UserId, out var e) ? e : "?",
            a.AccountId, a.TransactionId, a.Details, a.CreatedAt)).ToList();

        return Ok(new { items, page, pageSize, total });
    }

    [HttpPost("aml/alerts/{id:guid}/review")]
    public Task<IActionResult> ReviewAlert(Guid id, [FromBody] AlertReviewRequest req) =>
        CloseAlertAsync(id, AmlAlertStatus.Reviewed, req.Note);

    [HttpPost("aml/alerts/{id:guid}/dismiss")]
    public Task<IActionResult> DismissAlert(Guid id, [FromBody] AlertReviewRequest req) =>
        CloseAlertAsync(id, AmlAlertStatus.Dismissed, req.Note);

    private async Task<IActionResult> CloseAlertAsync(Guid id, AmlAlertStatus target, string? note)
    {
        var adminId = AdminId();
        if (adminId is null) return Unauthorized();

        var alert = await _db.AmlAlerts.SingleOrDefaultAsync(a => a.Id == id);
        if (alert is null) throw BankException.NotFound("alert_not_found", "Alert does not exist.");
        if (alert.Status != AmlAlertStatus.Open) return NoContent(); // idempotent

        alert.Status = target;
        alert.ReviewedAt = DateTime.UtcNow;
        alert.ReviewedBy = adminId.Value;
        alert.ReviewNote = note?.Trim();

        _audit.Write(adminId, target == AmlAlertStatus.Reviewed ? "aml.alert_reviewed" : "aml.alert_dismissed",
            nameof(AmlAlert), alert.Id.ToString(),
            new { alert.AlertType, alert.UserId, note }, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
