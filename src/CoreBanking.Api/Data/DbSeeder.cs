using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Data;

/// <summary>
/// Idempotent seed for local development. Never run in production —
/// the compose stack sets ASPNETCORE_ENVIRONMENT=Development for this reason.
/// </summary>
public static class DbSeeder
{
    public const string DemoEmail = "demo@bank.com";
    public const string DemoPassword = "Demo123!";
    public const string AdminEmail = "admin@bank.com";
    public const string AdminPassword = "Admin123!";
    public const string SystemUserEmail = "system@meridian.internal";

    private static readonly string[] Currencies = { "USD", "EUR", "GBP", "KES", "NGN" };

    public static async Task SeedAsync(AppDbContext db)
    {
        var system = await UpsertUserAsync(db, SystemUserEmail, "System Omnibus",
            BCrypt.Net.BCrypt.HashPassword(Convert.ToHexString(Guid.NewGuid().ToByteArray())), // unusable password
            UserRole.Admin);

        await UpsertUserAsync(db, AdminEmail, "Bank Administrator",
            BCrypt.Net.BCrypt.HashPassword(AdminPassword), UserRole.Admin);

        var demo = await UpsertUserAsync(db, DemoEmail, "Demo Customer",
            BCrypt.Net.BCrypt.HashPassword(DemoPassword), UserRole.Customer);

        var admin = await db.Users.SingleAsync(u => u.Email == AdminEmail);

        // Verified demo dossiers so the demo user passes the KYC gate
        await UpsertProfileAsync(db, demo.Id, "Demo Customer",
            new DateTime(1990, 1, 15, 0, 0, 0, DateTimeKind.Utc), "Kenya", "+254700000001",
            "1 Meridian Plaza", "Nairobi", "Kenya", IdType.NationalId, "KE-12345678",
            EmploymentStatus.Employed, "50k-100k", admin.Id);
        await UpsertProfileAsync(db, admin.Id, "Bank Administrator",
            new DateTime(1985, 6, 20, 0, 0, 0, DateTimeKind.Utc), "Kenya", "+254700000002",
            "2 Meridian Plaza", "Nairobi", "Kenya", IdType.NationalId, "KE-87654321",
            EmploymentStatus.Employed, "100k+", admin.Id);

        // Bank-side omnibus + clearing accounts for each supported currency
        foreach (var currency in Currencies)
        {
            await EnsureAccountAsync(db, system.Id, $"SYS-{currency}-OMNIBUS", AccountType.Current, currency, null);
            await EnsureAccountAsync(db, system.Id, $"SYS-{currency}-INTCLR", AccountType.Current, currency, null);
            await EnsureAccountAsync(db, system.Id, $"SYS-{currency}-FEECLR", AccountType.Current, currency, null);
            await EnsureAccountAsync(db, system.Id, $"SYS-{currency}-LOANCLR", AccountType.Current, currency, null);
            await EnsureAccountAsync(db, system.Id, $"SYS-{currency}-ODINT", AccountType.Current, currency, null);
        }

        // Demo customer: one interest-bearing savings + one fee-bearing current account
        await EnsureAccountAsync(db, demo.Id, "AC100000000001", AccountType.Savings, "USD", "SAV-STD");
        await EnsureAccountAsync(db, demo.Id, "AC100000000002", AccountType.Current, "USD", "CUR-PER");
    }

    private static async Task<User> UpsertUserAsync(AppDbContext db, string email, string fullName,
        string passwordHash, UserRole role)
    {
        email = email.ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (user is not null) return user;

        user = new User { Email = email, FullName = fullName, PasswordHash = passwordHash, Role = role };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task UpsertProfileAsync(AppDbContext db, Guid userId, string fullLegalName,
        DateTime dob, string nationality, string phone, string address, string city, string country,
        IdType idType, string idNumber, EmploymentStatus employment, string incomeBand, Guid verifiedBy)
    {
        if (await db.CustomerProfiles.AnyAsync(p => p.UserId == userId)) return;

        db.CustomerProfiles.Add(new CustomerProfile
        {
            UserId = userId,
            FullLegalName = fullLegalName,
            DateOfBirth = dob,
            Nationality = nationality,
            Phone = phone,
            AddressLine = address,
            City = city,
            Country = country,
            IdType = idType,
            IdNumber = idNumber,
            EmploymentStatus = employment,
            AnnualIncomeBand = incomeBand,
            KycStatus = KycStatus.Verified,
            RiskRating = RiskRating.Low,
            VerifiedAt = DateTime.UtcNow,
            VerifiedBy = verifiedBy
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureAccountAsync(AppDbContext db, Guid userId, string accountNumber,
        AccountType type, string currency, string? productCode)
    {
        if (await db.Accounts.AnyAsync(a => a.AccountNumber == accountNumber)) return;

        Guid? productId = null;
        if (productCode is not null)
            productId = await db.Products.Where(p => p.Code == productCode)
                .Select(p => (Guid?)p.Id).SingleOrDefaultAsync();

        db.Accounts.Add(new Account
        {
            AccountNumber = accountNumber,
            UserId = userId,
            Type = type,
            Currency = currency,
            Status = AccountStatus.Active,
            Balance = 0m,
            Version = 0,
            ProductId = productId
        });
        await db.SaveChangesAsync();
    }
}
