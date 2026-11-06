using System.Security.Cryptography;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Services;

/// <summary>
/// Revolving credit on Current accounts. ODL-VAR: unsecured, scored decision
/// (min 650). ODL-SEC: collateral-backed by a pledged Savings balance — the
/// pledged amount cannot drop below the coverage requirement while the
/// facility is active. Approvals move no money; they raise withdrawable
/// headroom. USD v1, one facility per account, KYC-gated.
/// </summary>
public class OverdraftService
{
    public const string VarProductCode = "ODL-VAR";
    public const string SecProductCode = "ODL-SEC";

    public const decimal VarAnnualRate = 24.0000m;      // p.a. on overdrawn balance
    public const decimal SecAnnualRate = 12.0000m;      // p.a. — collateral-secured pricing
    public const decimal VarMinLimit = 500m;
    public const decimal VarMaxLimit = 25_000m;
    public const decimal SecMinLimit = 500m;
    public const decimal SecMaxLimit = 100_000m;
    public const int MinCreditScore = 650;              // unsecured only
    public const decimal SecuredLtvRatio = 0.90m;       // limit ≤ 90% of pledged balance

    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public OverdraftService(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public static IReadOnlyList<OverdraftTermsDto> Terms() => new[]
    {
        new OverdraftTermsDto(VarProductCode, "USD", VarAnnualRate, VarMinLimit, VarMaxLimit,
            MinCreditScore, RequiredCollateralRatio: null, Secured: false),
        new OverdraftTermsDto(SecProductCode, "USD", SecAnnualRate, SecMinLimit, SecMaxLimit,
            MinCreditScore: 0, RequiredCollateralRatio: 1m / SecuredLtvRatio, Secured: true),
    };

    public async Task<OverdraftFacilityDto> ApplyAsync(Guid userId, ApplyOverdraftRequest req, string? ip)
    {
        await KycGate.RequireVerifiedAsync(_db, userId);

        var productCode = string.IsNullOrWhiteSpace(req.ProductCode)
            ? VarProductCode : req.ProductCode.Trim().ToUpperInvariant();
        if (productCode is not (VarProductCode or SecProductCode))
            throw BankException.BadRequest("unknown_product", $"Product must be {VarProductCode} or {SecProductCode}.");

        var account = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == req.AccountId);
        if (account is null) throw BankException.NotFound("account_not_found", "Account does not exist.");
        if (account.UserId != userId) throw BankException.Forbidden("not_account_owner", "You do not own this account.");
        if (account.Type != AccountType.Current)
            throw BankException.Unprocessable("current_only", "Overdraft facilities are available on Current accounts only.");
        if (account.Currency != "USD")
            throw BankException.Unprocessable("currency_mismatch", "Overdraft facilities are denominated in USD in v1.");
        if (account.Status != AccountStatus.Active)
            throw BankException.Unprocessable("account_not_active", "Account is not active.");

        var existing = await _db.OverdraftFacilities
            .Where(f => f.AccountId == account.Id && f.Status != OverdraftStatus.Closed && f.Status != OverdraftStatus.Rejected)
            .Select(f => f.Id)
            .SingleOrDefaultAsync();
        if (existing != Guid.Empty)
            throw BankException.Conflict("facility_exists", "This account already has an overdraft facility.");

        if (productCode == VarProductCode)
            return await ApplyUnsecuredAsync(userId, account, req.RequestedLimit, ip);
        return await ApplySecuredAsync(userId, account, req, ip);
    }

    private async Task<OverdraftFacilityDto> ApplyUnsecuredAsync(Guid userId, Account account,
        decimal requestedLimit, string? ip)
    {
        if (requestedLimit < VarMinLimit || requestedLimit > VarMaxLimit)
            throw BankException.BadRequest("limit_out_of_range",
                $"Unsecured limit must be between {VarMinLimit:F0} and {VarMaxLimit:F0} USD.");

        var profile = await _db.CustomerProfiles.SingleAsync(p => p.UserId == userId);
        var score = CreditScoring.Score(profile, requestedLimit);
        var approved = score >= MinCreditScore;
        var reason = approved
            ? $"Score {score} ≥ {MinCreditScore} ({VarProductCode} minimum)"
            : $"Score {score} below {MinCreditScore} ({VarProductCode} minimum)";

        return await CreateFacilityAsync(userId, account, requestedLimit, VarAnnualRate, score,
            reason, approved, productCode: VarProductCode, ip);
    }

    private async Task<OverdraftFacilityDto> ApplySecuredAsync(Guid userId, Account account,
        ApplyOverdraftRequest req, string? ip)
    {
        if (req.RequestedLimit < SecMinLimit || req.RequestedLimit > SecMaxLimit)
            throw BankException.BadRequest("limit_out_of_range",
                $"Secured limit must be between {SecMinLimit:F0} and {SecMaxLimit:F0} USD.");

        if (req.CollateralAccountId is null)
            throw BankException.BadRequest("collateral_required",
                "ODL-SEC requires CollateralAccountId (a pledged Savings account).");

        var collateral = await _db.Accounts.SingleOrDefaultAsync(a => a.Id == req.CollateralAccountId);
        if (collateral is null) throw BankException.NotFound("collateral_not_found", "Collateral account does not exist.");
        if (collateral.Id == account.Id)
            throw BankException.BadRequest("collateral_is_facility_account",
                "The pledged account cannot be the same as the account receiving the facility.");
        if (collateral.UserId != userId)
            throw BankException.Forbidden("not_collateral_owner", "You do not own the collateral account.");
        if (collateral.Type != AccountType.Savings)
            throw BankException.Unprocessable("savings_collateral_only", "Collateral must be a Savings account.");
        if (collateral.Status != AccountStatus.Active)
            throw BankException.Unprocessable("collateral_not_active", "Collateral account is not active.");
        if (collateral.Currency != "USD")
            throw BankException.Unprocessable("currency_mismatch", "Collateral must be USD in v1.");
        if (collateral.AccountNumber.StartsWith("SYS-", StringComparison.OrdinalIgnoreCase))
            throw BankException.BadRequest("invalid_collateral", "System accounts cannot be pledged.");

        var requiredCollateral = Math.Round(req.RequestedLimit / SecuredLtvRatio, 4, MidpointRounding.AwayFromZero);
        var approved = collateral.Balance >= requiredCollateral;
        var profile = await _db.CustomerProfiles.SingleAsync(p => p.UserId == userId);
        var score = CreditScoring.Score(profile, 0m); // informational only for secured approvals
        var reason = approved
            ? $"Collateral {collateral.Balance:F2} covers required {requiredCollateral:F2} (limit ≤ 90% of pledge)"
            : $"Collateral {collateral.Balance:F2} below required {requiredCollateral:F2} (limit ≤ 90% of pledge)";

        return await CreateFacilityAsync(userId, account, req.RequestedLimit, SecAnnualRate, score,
            reason, approved, productCode: SecProductCode, ip,
            collateralAccountId: collateral.Id, collateralAmount: requiredCollateral);
    }

    private async Task<OverdraftFacilityDto> CreateFacilityAsync(Guid userId, Account account,
        decimal requestedLimit, decimal annualRate, int score, string reason, bool approved,
        string productCode, string? ip,
        Guid? collateralAccountId = null, decimal? collateralAmount = null)
    {
        var facility = new OverdraftFacility
        {
            Reference = await GenerateReferenceAsync(),
            UserId = userId,
            AccountId = account.Id,
            ProductCode = productCode,
            Limit = requestedLimit,
            AnnualInterestRate = annualRate,
            CreditScore = score,
            DecisionReason = reason,
            Status = approved ? OverdraftStatus.Active : OverdraftStatus.Rejected,
            AppliedAt = DateTime.UtcNow,
            ApprovedAt = approved ? DateTime.UtcNow : null,
            CollateralAccountId = collateralAccountId,
            CollateralAmount = collateralAmount
        };

        _db.OverdraftFacilities.Add(facility);
        _audit.Write(userId, approved ? "overdraft.approved" : "overdraft.rejected",
            nameof(OverdraftFacility), facility.Id.ToString(),
            new { facility.Reference, accountId = account.Id, productCode, requestedLimit, score,
                  collateralAccountId, collateralAmount }, ip);
        await _db.SaveChangesAsync();

        return await ToDtoAsync(facility, account, collateralAccountId);
    }

    public async Task<OverdraftFacilityDto> CloseAsync(Guid callerId, bool callerIsAdmin, Guid facilityId,
        string? reason, string? ip)
    {
        var facility = await _db.OverdraftFacilities
            .Include(f => f.Account)
            .SingleOrDefaultAsync(f => f.Id == facilityId);
        if (facility is null) throw BankException.NotFound("facility_not_found", "Facility does not exist.");
        if (facility.UserId != callerId && !callerIsAdmin)
            throw BankException.Forbidden("not_facility_owner", "This facility belongs to another customer.");
        if (facility.Status == OverdraftStatus.Closed) return await ToDtoAsync(facility, facility.Account);

        if (facility.Account.Balance < 0m)
            throw BankException.Conflict("balance_negative",
                $"Cannot close an overdrawn account (balance {facility.Account.Balance:F2}). Repay the overdraft first.");

        facility.Status = OverdraftStatus.Closed;
        facility.ClosedAt = DateTime.UtcNow;
        facility.ClosedBy = callerId;
        facility.CloseReason = reason?.Trim();

        _audit.Write(callerId, "overdraft.closed", nameof(OverdraftFacility), facility.Id.ToString(),
            new { facility.Reference, byAdmin = callerIsAdmin, reason, collateralReleased = facility.CollateralAccountId is not null }, ip);
        await _db.SaveChangesAsync();

        return await ToDtoAsync(facility, facility.Account);
    }

    public async Task<List<OverdraftFacilityDto>> ListMineAsync(Guid userId)
    {
        var facilities = await _db.OverdraftFacilities.AsNoTracking()
            .Include(f => f.Account)
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.AppliedAt)
            .ToListAsync();
        var result = new List<OverdraftFacilityDto>(facilities.Count);
        foreach (var f in facilities)
            result.Add(await ToDtoAsync(f, f.Account));
        return result;
    }

    // ---------- mapping ----------

    private async Task<OverdraftFacilityDto> ToDtoAsync(OverdraftFacility f, Account account, Guid? collateralAccountId = null)
    {
        var accrued = await _db.OverdraftAccruals.AsNoTracking()
            .Where(a => a.FacilityId == f.Id && !a.Charged)
            .SumAsync(a => (decimal?)a.Amount) ?? 0m;

        var headroom = f.Status == OverdraftStatus.Active
            ? Math.Max(0m, Math.Min(0m, account.Balance) + f.Limit)
            : 0m;

        string? collateralNumber = null;
        if (f.CollateralAccountId is not null)
            collateralNumber = await _db.Accounts.AsNoTracking()
                .Where(a => a.Id == f.CollateralAccountId)
                .Select(a => a.AccountNumber)
                .SingleOrDefaultAsync();

        return new OverdraftFacilityDto(
            f.Id, f.Reference, f.AccountId, account.AccountNumber,
            f.Limit, f.AnnualInterestRate, f.Status.ToString(),
            f.CreditScore, f.DecisionReason,
            f.AppliedAt, f.ApprovedAt, f.ClosedAt,
            account.Balance, headroom, accrued,
            f.ProductCode, f.CollateralAccountId, f.CollateralAmount, collateralNumber);
    }

    private async Task<string> GenerateReferenceAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = $"OD-{DateTime.UtcNow:yyyy}-{RandomNumberGenerator.GetInt32(100000, 999999)}";
            if (!await _db.OverdraftFacilities.AnyAsync(f => f.Reference == candidate))
                return candidate;
        }
        throw BankException.Conflict("reference_generation_failed", "Could not allocate a facility reference — please retry.");
    }
}
