using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Services;

/// <summary>Shared KYC enforcement — outbound money and credit require a verified dossier.</summary>
public static class KycGate
{
    public static async Task RequireVerifiedAsync(AppDbContext db, Guid userId)
    {
        var status = await db.CustomerProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (KycStatus?)p.KycStatus)
            .SingleOrDefaultAsync();
        if (status != KycStatus.Verified)
            throw BankException.Forbidden("kyc_required",
                "Complete identity verification (KYC) before banking. Our compliance team must approve your dossier first.");
    }
}
