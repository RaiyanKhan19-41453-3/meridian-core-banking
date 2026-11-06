namespace CoreBanking.Api.Entities;

public enum KycStatus
{
    Pending,
    Verified,
    Rejected
}

public enum IdType
{
    NationalId,
    Passport
}

public enum EmploymentStatus
{
    Employed,
    SelfEmployed,
    BusinessOwner,
    Student,
    Retired,
    Unemployed
}

public enum RiskRating
{
    Low,
    Medium,
    High
}

/// <summary>
/// KYC dossier for a registered customer. Created with Status=Pending at
/// registration; admin reviews documents and approves/rejects. Rejected
/// dossiers return to Pending on any edit.
/// </summary>
public class CustomerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    public string FullLegalName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    public string Nationality { get; set; } = string.Empty; // ISO-ish free text, e.g. "Kenya"
    public string Phone { get; set; } = string.Empty;
    public string AddressLine { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;

    public IdType IdType { get; set; }
    public string IdNumber { get; set; } = string.Empty;
    public EmploymentStatus EmploymentStatus { get; set; }
    public string AnnualIncomeBand { get; set; } = string.Empty; // e.g. "25k-50k"

    public KycStatus KycStatus { get; set; } = KycStatus.Pending;
    public RiskRating RiskRating { get; set; } = RiskRating.Low;
    public DateTime? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<KycDocument> Documents { get; set; } = new List<KycDocument>();
}
