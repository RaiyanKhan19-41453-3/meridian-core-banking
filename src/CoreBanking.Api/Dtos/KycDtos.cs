using System.ComponentModel.DataAnnotations;
using CoreBanking.Api.Entities;

namespace CoreBanking.Api.Dtos;

// ---------- customer-facing ----------

public record KycStatusResponse(
    string KycStatus,
    string RiskRating,
    string FullLegalName,
    bool ProfileComplete,
    int DocumentCount,
    string? RejectionReason,
    DateTime? VerifiedAt);

public record KycProfileDto(
    string FullLegalName,
    DateTime DateOfBirth,
    string Nationality,
    string Phone,
    string AddressLine,
    string City,
    string Country,
    string IdType,
    string IdNumber,
    string EmploymentStatus,
    string AnnualIncomeBand,
    string KycStatus,
    string RiskRating,
    string? RejectionReason,
    DateTime? VerifiedAt,
    DateTime UpdatedAt);

public record UpdateKycProfileRequest(
    [Required, MaxLength(200)] string FullLegalName,
    [Required] DateTime DateOfBirth,
    [Required, MaxLength(100)] string Nationality,
    [Required, MaxLength(40), Phone] string Phone,
    [Required, MaxLength(300)] string AddressLine,
    [Required, MaxLength(100)] string City,
    [Required, MaxLength(100)] string Country,
    [Required] IdType IdType,
    [Required, MaxLength(64)] string IdNumber,
    [Required] EmploymentStatus EmploymentStatus,
    [Required, MaxLength(30)] string AnnualIncomeBand);

public record KycDocumentMeta(
    Guid Id,
    string DocType,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTime UploadedAt);

// ---------- admin ----------

public record PendingProfileDto(
    Guid UserId,
    string Email,
    string FullLegalName,
    string IdType,
    string IdNumber,
    string Nationality,
    string RiskRating,
    DateTime CreatedAt,
    int DocumentCount);

public record RejectKycRequest([Required, MaxLength(500)] string Reason);

public record AmlAlertDto(
    Guid Id,
    string AlertType,
    string Severity,
    string Status,
    Guid UserId,
    string UserEmail,
    Guid? AccountId,
    Guid? TransactionId,
    string Details,
    DateTime CreatedAt);

public record AlertReviewRequest([MaxLength(1000)] string? Note);
