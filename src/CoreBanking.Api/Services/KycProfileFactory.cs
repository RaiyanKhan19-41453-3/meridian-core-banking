using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Services;

/// <summary>Creates the empty (Pending) KYC dossier a fresh account starts with.</summary>
public static class KycProfileFactory
{
    public static async Task<CustomerProfile> CreatePendingAsync(AppDbContext db, User user)
    {
        var profile = new CustomerProfile
        {
            UserId = user.Id,
            FullLegalName = user.FullName,
            DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Nationality = "Unknown",
            Phone = "Unknown",
            AddressLine = "Unknown",
            City = "Unknown",
            Country = "Unknown",
            IdNumber = "Unknown",
            AnnualIncomeBand = "Unknown",
            EmploymentStatus = EmploymentStatus.Employed,
            KycStatus = KycStatus.Pending,
            RiskRating = RiskRating.Low,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.CustomerProfiles.Add(profile);
        return profile;
    }

    public static async Task<CustomerProfile?> FindByUserAsync(AppDbContext db, Guid userId) =>
        await db.CustomerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
}
