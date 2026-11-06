using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
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
[Route("api/kyc")]
public class KycController : ControllerBase
{
    private const long MaxDocumentBytes = 2 * 1024 * 1024; // 2 MB
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png"
    };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png"
    };

    private readonly AppDbContext _db;
    private readonly AmlService _aml;
    private readonly AuditService _audit;

    public KycController(AppDbContext db, AmlService aml, AuditService audit)
    {
        _db = db;
        _aml = aml;
        _audit = audit;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private Guid? CurrentUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    /// <summary>Current user's KYC status + profile completeness.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var profile = await GetOrCreateProfileAsync(userId.Value);
        return Ok(new KycStatusResponse(
            profile.KycStatus.ToString(), profile.RiskRating.ToString(), profile.FullLegalName,
            IsProfileComplete(profile),
            await _db.KycDocuments.CountAsync(d => d.ProfileId == profile.Id),
            profile.RejectionReason, profile.VerifiedAt));
    }

    /// <summary>Full profile for editing.</summary>
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var profile = await GetOrCreateProfileAsync(userId.Value);
        return Ok(ToDto(profile));
    }

    /// <summary>Create/replace the KYC dossier. Verified dossiers are read-only; edits to a rejected dossier resubmit it (Pending).</summary>
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateKycProfileRequest req)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        if (req.DateOfBirth.Date > DateTime.UtcNow.Date.AddYears(-18))
            throw BankException.BadRequest("underage", "Customer must be at least 18 years old.");
        if (req.DateOfBirth.Date < DateTime.UtcNow.Date.AddYears(-120))
            throw BankException.BadRequest("invalid_dob", "Date of birth is out of range.");

        var profile = await GetOrCreateProfileAsync(userId.Value);
        if (profile.KycStatus == KycStatus.Verified)
            throw BankException.Conflict("profile_locked", "Verified profile is locked. Contact support to change details.");

        var wasRejected = profile.KycStatus == KycStatus.Rejected;
        profile.FullLegalName = req.FullLegalName.Trim();
        profile.DateOfBirth = DateTime.SpecifyKind(req.DateOfBirth.Date, DateTimeKind.Utc);
        profile.Nationality = req.Nationality.Trim();
        profile.Phone = req.Phone.Trim();
        profile.AddressLine = req.AddressLine.Trim();
        profile.City = req.City.Trim();
        profile.Country = req.Country.Trim();
        profile.IdType = req.IdType;
        profile.IdNumber = req.IdNumber.Trim();
        profile.EmploymentStatus = req.EmploymentStatus;
        profile.AnnualIncomeBand = req.AnnualIncomeBand.Trim();

        profile.KycStatus = KycStatus.Pending; // any edit resubmits for review
        profile.RejectionReason = null;
        profile.UpdatedAt = DateTime.UtcNow;

        // Watchlist screening on every submission
        var match = await _aml.ScreenWatchlistAsync(profile.FullLegalName);
        if (match is not null)
        {
            profile.RiskRating = RiskRating.High;
            await _aml.FlagWatchlistMatchAsync(userId.Value, profile.FullLegalName, match, ClientIp);
            _audit.Write(userId, "kyc.watchlist_match", nameof(CustomerProfile), profile.Id.ToString(),
                new { match.FullName, match.Source }, ClientIp);
        }
        else if (profile.RiskRating == RiskRating.High)
        {
            profile.RiskRating = RiskRating.Medium; // previously flagged; needs admin review anyway
        }

        _audit.Write(userId, wasRejected ? "kyc.resubmitted" : "kyc.profile_updated",
            nameof(CustomerProfile), profile.Id.ToString(),
            new { profile.FullLegalName, profile.KycStatus, profile.RiskRating }, ClientIp);

        await _db.SaveChangesAsync();
        return Ok(ToDto(profile));
    }

    /// <summary>Upload an identity document (PDF/JPEG/PNG, max 2 MB).</summary>
    [HttpPost("documents")]
    [RequestSizeLimit(3_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 3_000_000)]
    public async Task<IActionResult> UploadDocument([FromForm] KycDocumentType docType, [FromForm] IFormFile file)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        if (file is null || file.Length == 0)
            throw BankException.BadRequest("empty_file", "A document file is required.");
        if (file.Length > MaxDocumentBytes)
            throw BankException.BadRequest("file_too_large", "Document must be 2 MB or smaller.");
        if (!AllowedContentTypes.Contains(file.ContentType))
            throw BankException.BadRequest("unsupported_type", "Allowed types: PDF, JPEG, PNG.");
        var extension = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(extension))
            throw BankException.BadRequest("unsupported_extension", "Allowed extensions: .pdf, .jpg, .jpeg, .png.");

        var profile = await GetOrCreateProfileAsync(userId.Value);
        if (profile.KycStatus == KycStatus.Verified)
            throw BankException.Conflict("profile_locked", "Verified profile is locked.");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var doc = new KycDocument
        {
            ProfileId = profile.Id,
            DocType = docType,
            FileName = Path.GetFileName(file.FileName),
            ContentType = file.ContentType,
            SizeBytes = bytes.LongLength,
            Content = bytes,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            UploadedAt = DateTime.UtcNow,
            UploadedBy = userId.Value
        };
        _db.KycDocuments.Add(doc);
        _audit.Write(userId, "kyc.document_uploaded", nameof(KycDocument), doc.Id.ToString(),
            new { docType = docType.ToString(), doc.FileName, doc.SizeBytes, doc.Sha256 }, ClientIp);
        await _db.SaveChangesAsync();

        return StatusCode(StatusCodes.Status201Created, ToMeta(doc));
    }

    /// <summary>List uploaded document metadata (no bytes).</summary>
    [HttpGet("documents")]
    public async Task<IActionResult> ListDocuments()
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var profile = await GetOrCreateProfileAsync(userId.Value);
        var docs = await _db.KycDocuments.AsNoTracking()
            .Where(d => d.ProfileId == profile.Id)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => ToMeta(d))
            .ToListAsync();
        return Ok(docs);
    }

    /// <summary>Download a document. Owner or Admin only; every download is audited.</summary>
    [HttpGet("documents/{id:guid}/content")]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var doc = await _db.KycDocuments
            .Include(d => d.Profile)
            .SingleOrDefaultAsync(d => d.Id == id);
        if (doc is null) throw BankException.NotFound("document_not_found", "Document does not exist.");
        if (doc.Profile.UserId != userId && !IsAdmin)
            throw BankException.Forbidden("not_document_owner", "You do not have access to this document.");

        _audit.Write(userId, "kyc.document_downloaded", nameof(KycDocument), doc.Id.ToString(),
            new { doc.DocType, doc.FileName, byAdmin = IsAdmin }, ClientIp);
        await _db.SaveChangesAsync();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(doc.Content, doc.ContentType, doc.FileName);
    }

    /// <summary>Delete a document you own (only while your profile is not Verified).</summary>
    [HttpDelete("documents/{id:guid}")]
    public async Task<IActionResult> DeleteDocument(Guid id)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();

        var doc = await _db.KycDocuments.Include(d => d.Profile).SingleOrDefaultAsync(d => d.Id == id);
        if (doc is null) throw BankException.NotFound("document_not_found", "Document does not exist.");
        if (doc.Profile.UserId != userId) throw BankException.Forbidden("not_document_owner", "Not your document.");
        if (doc.Profile.KycStatus == KycStatus.Verified)
            throw BankException.Conflict("profile_locked", "Verified profile is locked.");

        _db.KycDocuments.Remove(doc);
        _audit.Write(userId, "kyc.document_deleted", nameof(KycDocument), doc.Id.ToString(),
            new { doc.DocType, doc.FileName }, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- helpers ----------

    internal async Task<CustomerProfile> GetOrCreateProfileAsync(Guid userId)
    {
        var profile = await _db.CustomerProfiles
            .Include(p => p.Documents)
            .FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile is not null) return profile;

        var user = await _db.Users.SingleAsync(u => u.Id == userId);
        profile = await KycProfileFactory.CreatePendingAsync(_db, user);
        await _db.SaveChangesAsync();
        return profile;
    }

    private static bool IsProfileComplete(CustomerProfile p) =>
        !string.IsNullOrWhiteSpace(p.Phone) && p.Phone != "Unknown"
        && !string.IsNullOrWhiteSpace(p.AddressLine) && p.AddressLine != "Unknown"
        && !string.IsNullOrWhiteSpace(p.IdNumber) && p.IdNumber != "Unknown";

    private static KycProfileDto ToDto(CustomerProfile p) => new(
        p.FullLegalName, p.DateOfBirth, p.Nationality, p.Phone, p.AddressLine, p.City, p.Country,
        p.IdType.ToString(), p.IdNumber, p.EmploymentStatus.ToString(), p.AnnualIncomeBand,
        p.KycStatus.ToString(), p.RiskRating.ToString(), p.RejectionReason, p.VerifiedAt, p.UpdatedAt);

    private static KycDocumentMeta ToMeta(KycDocument d) => new(
        d.Id, d.DocType.ToString(), d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.UploadedAt);
}
