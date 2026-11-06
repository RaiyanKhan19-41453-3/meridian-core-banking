using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBanking.Api.Services;

public record LedgerResult(
    Transaction Transaction,
    IReadOnlyList<LedgerEntry> Entries,
    bool Replayed,
    bool Overdraft);

/// <summary>
/// The money engine. Every posting:
///   1. is idempotent (Idempotency-Key == transactions.reference + request fingerprint),
///   2. takes an explicit DB transaction and locks the affected rows (SELECT ... FOR UPDATE),
///   3. enforces account status and single-transaction / daily-outflow limits,
///   4. writes immutable double-entry ledger legs whose amounts sum to zero,
///   5. atomically updates cached balances (version-checked as a backstop).
/// </summary>
public class LedgerService
{
    private const string SystemUserEmail = "system@meridian.internal";
    private const string SystemAccountPrefix = "SYS-";

    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly GlService _gl;
    private readonly AmlService _aml;
    private readonly IConfiguration _config;

    public LedgerService(AppDbContext db, AuditService audit, GlService gl, AmlService aml, IConfiguration config)
    {
        _db = db;
        _audit = audit;
        _gl = gl;
        _aml = aml;
        _config = config;
    }

    private decimal MaxSingleTransaction =>
        decimal.TryParse(_config["Limits:MaxSingleTransaction"], out var v) ? v : 250_000m;

    private decimal MaxDailyOutflow =>
        decimal.TryParse(_config["Limits:MaxDailyOutflow"], out var v) ? v : 50_000m;

    // ---------- public API ----------

    public async Task<LedgerResult> DepositAsync(Guid userId, Guid accountId, decimal amount,
        string? description, string idempotencyKey, string requestHash, string? ip)
    {
        ValidateAmount(amount);
        ValidateIdempotencyKey(idempotencyKey);
        await RequireVerifiedAsync(userId);

        await using var tx = await _db.Database.BeginTransactionAsync();

        var replay = await TryReplayAsync(idempotencyKey, requestHash);
        if (replay is not null) return replay;

        var customerProbe = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => new { a.Id, a.UserId, a.Currency })
            .SingleOrDefaultAsync();
        if (customerProbe is null) throw BankException.NotFound("account_not_found", "Account does not exist.");
        if (customerProbe.UserId != userId)
            throw BankException.Forbidden("not_account_owner", "You do not own this account.");

        var omnibus = await EnsureSystemOmnibusAsync(customerProbe.Currency);
        var locked = await LockAccountsAsync(new[] { customerProbe.Id, omnibus.Id });
        var byId = locked.ToDictionary(a => a.Id);
        var customer = byId[customerProbe.Id];
        var bank = byId[omnibus.Id];

        EnsureActive(customer, "destination");

        var transaction = BuildTransaction(idempotencyKey, requestHash, TransactionType.Deposit,
            userId, description);
        var entries = BuildEntries(transaction, from: bank, to: customer, amount);
        EnsureEntriesSumToZero(entries);

        _db.Transactions.Add(transaction);
        _db.LedgerEntries.AddRange(entries);
        await _gl.AttachAsync(transaction, entries, byId, customer.Currency);
        bank.Balance += -amount; bank.Version += 1;
        customer.Balance += amount; customer.Version += 1;

        _audit.Write(userId, "money.deposit", nameof(Transaction), transaction.Id.ToString(),
            new { reference = idempotencyKey, amount, currency = customer.Currency, to = customer.AccountNumber }, ip);

        if (await TryCommitAsync(tx, idempotencyKey, requestHash) is { } raced) return raced;
        await _aml.EvaluateAsync(transaction, byId.Values.ToList(), ip);
        return new LedgerResult(transaction, entries, Replayed: false, Overdraft: false);
    }

    public async Task<LedgerResult> WithdrawAsync(Guid userId, Guid accountId, decimal amount,
        string? description, string idempotencyKey, string requestHash, string? ip)
    {
        ValidateAmount(amount);
        ValidateIdempotencyKey(idempotencyKey);
        await RequireVerifiedAsync(userId);

        var probe = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => new { a.UserId, a.Currency })
            .SingleOrDefaultAsync();
        if (probe is null) throw BankException.NotFound("account_not_found", "Account does not exist.");
        if (probe.UserId != userId)
            throw BankException.Forbidden("not_account_owner", "You do not own this account.");

        var omnibus = await EnsureSystemOmnibusAsync(probe.Currency);

        return await PostMovementAsync(idempotencyKey, requestHash, TransactionType.Withdrawal, userId,
            fromId: accountId, toId: omnibus.Id, amount, description, ip,
            enforceOutflowLimits: true, allowReversalOverdraft: false);
    }

    public async Task<LedgerResult> TransferAsync(Guid userId, Guid fromAccountId, string toAccountNumber,
        decimal amount, string? description, string idempotencyKey, string requestHash, string? ip)
    {
        ValidateAmount(amount);
        ValidateIdempotencyKey(idempotencyKey);
        await RequireVerifiedAsync(userId);
        if (string.IsNullOrWhiteSpace(toAccountNumber))
            throw BankException.BadRequest("missing_destination", "Destination account number is required.");

        var fromProbe = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == fromAccountId)
            .Select(a => new { a.UserId, a.Currency })
            .SingleOrDefaultAsync();
        if (fromProbe is null) throw BankException.NotFound("account_not_found", "Source account does not exist.");
        if (fromProbe.UserId != userId)
            throw BankException.Forbidden("not_account_owner", "You do not own the source account.");

        var to = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountNumber == toAccountNumber.Trim().ToUpperInvariant())
            .Select(a => new { a.Id, a.Currency })
            .SingleOrDefaultAsync();
        if (to is null) throw BankException.NotFound("destination_not_found", "Destination account does not exist.");
        if (to.Id == fromAccountId) throw BankException.BadRequest("same_account", "Source and destination are the same account.");
        if (to.Currency != fromProbe.Currency)
            throw BankException.Unprocessable("fx_not_supported",
                $"Cross-currency movement rejected ({fromProbe.Currency} → {to.Currency}). Same currency only.");

        return await PostMovementAsync(idempotencyKey, requestHash, TransactionType.Transfer, userId,
            fromId: fromAccountId, toId: to.Id, amount, description, ip,
            enforceOutflowLimits: true, allowReversalOverdraft: false);
    }

    /// <summary>Admin-only reversal of a posted, non-reversal transaction.</summary>
    public async Task<LedgerResult> ReverseAsync(Guid adminId, Guid transactionId,
        string? description, string idempotencyKey, string requestHash, string? ip)
    {
        ValidateIdempotencyKey(idempotencyKey);

        await using var tx = await _db.Database.BeginTransactionAsync();

        var replay = await TryReplayAsync(idempotencyKey, requestHash);
        if (replay is not null) return replay;

        var original = await _db.Transactions
            .Include(t => t.Entries)
            .SingleOrDefaultAsync(t => t.Id == transactionId);
        if (original is null) throw BankException.NotFound("transaction_not_found", "Transaction does not exist.");
        if (original.Type == TransactionType.Reversal)
            throw BankException.BadRequest("reversal_of_reversal", "A reversal cannot itself be reversed.");
        if (original.Status == TransactionStatus.Reversed)
            throw BankException.Conflict("already_reversed", "Transaction has already been reversed.");

        var accountIds = original.Entries.Select(e => e.AccountId).Distinct().ToArray();
        var locked = await LockAccountsAsync(accountIds);
        var byId = locked.ToDictionary(a => a.Id);

        var reversal = new Transaction
        {
            Reference = idempotencyKey,
            RequestHash = requestHash,
            OriginalTransactionId = original.Id,
            Type = TransactionType.Reversal,
            Status = TransactionStatus.Posted,
            CreatedBy = adminId,
            Description = string.IsNullOrWhiteSpace(description)
                ? $"Reversal of transaction {original.Reference}"
                : description.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        var entries = new List<LedgerEntry>();
        bool overdraft = false;
        foreach (var leg in original.Entries)
        {
            var account = byId[leg.AccountId];
            var mirrored = -leg.Amount;
            var balanceAfter = account.Balance + mirrored;
            if (balanceAfter < 0) overdraft = true; // reversals may overdraw — flagged + audited

            entries.Add(new LedgerEntry
            {
                TransactionId = reversal.Id,
                AccountId = account.Id,
                Amount = mirrored,
                BalanceAfter = balanceAfter,
                PostedAt = DateTime.UtcNow
            });
            account.Balance = balanceAfter;
            account.Version += 1;
        }

        EnsureEntriesSumToZero(entries);
        original.Status = TransactionStatus.Reversed;

        _db.Transactions.Add(reversal);
        _db.LedgerEntries.AddRange(entries);
        await _gl.AttachAsync(reversal, entries, byId, byId.Values.First().Currency);

        _audit.Write(adminId, "transaction.reverse", nameof(Transaction), original.Id.ToString(),
            new { originalReference = original.Reference, reversalReference = reversal.Reference, overdraft }, ip);

        if (await TryCommitAsync(tx, idempotencyKey, requestHash) is { } raced) return raced;
        return new LedgerResult(reversal, entries, Replayed: false, Overdraft: overdraft);
    }

    /// <summary>Any money movement requires a verified KYC dossier.</summary>
    private async Task RequireVerifiedAsync(Guid userId) =>
        await KycGate.RequireVerifiedAsync(_db, userId);

    // ---------- shared paths ----------

    private async Task<LedgerResult> PostMovementAsync(
        string idempotencyKey, string requestHash, TransactionType type, Guid actorId,
        Guid fromId, Guid toId, decimal amount, string? description, string? ip,
        bool enforceOutflowLimits, bool allowReversalOverdraft)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();

        var replay = await TryReplayAsync(idempotencyKey, requestHash);
        if (replay is not null) return replay;

        var locked = await LockAccountsAsync(new[] { fromId, toId });
        var byId = locked.ToDictionary(a => a.Id);
        var from = byId[fromId];
        var to = byId[toId];

        EnsureActive(from, "source");
        EnsureActive(to, "destination");

        if (from.Currency != to.Currency)
            throw BankException.Unprocessable("fx_not_supported",
                $"Cross-currency movement rejected ({from.Currency} → {to.Currency}).");

        if (enforceOutflowLimits)
        {
            // Pledged collateral: balance can never drop below the pledged amount while the facility is Active
            var collateralHold = await _db.OverdraftFacilities.AsNoTracking()
                .Where(f => f.CollateralAccountId == from.Id && f.Status == OverdraftStatus.Active)
                .SumAsync(f => (decimal?)f.CollateralAmount) ?? 0m;

            var facilityHeadroom = await _db.OverdraftFacilities.AsNoTracking()
                .Where(f => f.AccountId == from.Id && f.Status == OverdraftStatus.Active)
                .Select(f => (decimal?)f.Limit)
                .SingleOrDefaultAsync() ?? 0m;

            var available = from.Balance + facilityHeadroom - collateralHold;
            if (available < amount)
            {
                var grossHeadroom = from.Balance + facilityHeadroom;
                if (collateralHold > 0m && grossHeadroom >= amount)
                    throw BankException.Unprocessable("collateral_locked",
                        $"Pledged as collateral for a Secured Overdraft ({collateralHold:F2} locked). Withdrawals below the pledge are not allowed while it is active.");

                throw BankException.Unprocessable("insufficient_funds",
                    $"Insufficient funds: available {grossHeadroom:F2} {from.Currency}"
                    + (facilityHeadroom > 0m ? $" (includes overdraft of {facilityHeadroom:F2})" : string.Empty)
                    + (collateralHold > 0m ? $" (pledged: {collateralHold:F2})" : string.Empty)
                    + ".");
            }

            var outToday = await GetOutflowTodayAsync(from.Id);
            if (outToday + amount > MaxDailyOutflow)
                throw BankException.Unprocessable("daily_limit_exceeded",
                    $"Daily outflow limit would be exceeded (used {outToday:F2} + {amount:F2} > {MaxDailyOutflow:F2} {from.Currency}).");
        }

        var transaction = BuildTransaction(idempotencyKey, requestHash, type, actorId, description);
        var entries = BuildEntries(transaction, from, to, amount);
        EnsureEntriesSumToZero(entries);

        _db.Transactions.Add(transaction);
        _db.LedgerEntries.AddRange(entries);
        await _gl.AttachAsync(transaction, entries, byId, from.Currency);

        from.Balance += -amount; from.Version += 1;
        to.Balance += amount; to.Version += 1;

        _audit.Write(actorId, $"money.{type.ToString().ToLowerInvariant()}", nameof(Transaction),
            transaction.Id.ToString(),
            new { reference = idempotencyKey, amount, currency = from.Currency, from = from.AccountNumber, to = to.AccountNumber }, ip);

        if (await TryCommitAsync(tx, idempotencyKey, requestHash) is { } raced) return raced;
        await _aml.EvaluateAsync(transaction, byId.Values.ToList(), ip);
        return new LedgerResult(transaction, entries, Replayed: false, Overdraft: false);
    }

    // ---------- helpers ----------

    private async Task<LedgerResult?> TryReplayAsync(string idempotencyKey, string requestHash)
    {
        var existing = await _db.Transactions
            .AsNoTracking()
            .Include(t => t.Entries)
            .SingleOrDefaultAsync(t => t.Reference == idempotencyKey);
        if (existing is null) return null;

        if (existing.RequestHash != requestHash)
            throw BankException.Conflict("idempotency_key_reuse",
                "This Idempotency-Key was already used with a different payload.");

        return new LedgerResult(existing, existing.Entries.ToList(), Replayed: true, Overdraft: false);
    }

    /// <summary>Returns a replayed result if a racing request won the unique-index race; otherwise commits and returns null.</summary>
    private async Task<LedgerResult?> TryCommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, string key, string requestHash)
    {
        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return null;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync();
            var existing = await _db.Transactions
                .AsNoTracking()
                .Include(t => t.Entries)
                .SingleAsync(t => t.Reference == key);
            return new LedgerResult(existing, existing.Entries.ToList(),
                Replayed: true, Overdraft: false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync();
            throw BankException.Conflict("concurrent_modification",
                "Account was modified concurrently. Nothing was posted — please retry.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == "23505";

    private static Transaction BuildTransaction(string key, string requestHash, TransactionType type,
        Guid actorId, string? description) => new()
    {
        Reference = key,
        RequestHash = requestHash,
        Type = type,
        Status = TransactionStatus.Posted,
        CreatedBy = actorId,
        Description = string.IsNullOrWhiteSpace(description) ? type.ToString() : description.Trim(),
        CreatedAt = DateTime.UtcNow
    };

    private static List<LedgerEntry> BuildEntries(Transaction tx, Account from, Account to, decimal amount)
    {
        var now = DateTime.UtcNow;
        return new List<LedgerEntry>
        {
            new() { TransactionId = tx.Id, AccountId = from.Id, Amount = -amount,
                    BalanceAfter = from.Balance - amount, PostedAt = now },
            new() { TransactionId = tx.Id, AccountId = to.Id, Amount = +amount,
                    BalanceAfter = to.Balance + amount, PostedAt = now },
        };
    }

    private static void EnsureEntriesSumToZero(IEnumerable<LedgerEntry> entries)
    {
        var sum = entries.Sum(e => e.Amount);
        if (sum != 0m)
            throw new InvalidOperationException($"Ledger imbalance: entries sum to {sum}. This is a bug.");
    }

    private static void EnsureActive(Account account, string role)
    {
        if (account.Status == AccountStatus.Frozen)
            throw BankException.Unprocessable("account_frozen", $"The {role} account is frozen.");
        if (account.Status == AccountStatus.Closed)
            throw BankException.Unprocessable("account_closed", $"The {role} account is closed.");
    }

    private async Task<decimal> GetOutflowTodayAsync(Guid accountId)
    {
        var dayStart = DateTime.UtcNow.Date;
        var sum = await _db.LedgerEntries
            .Where(e => e.AccountId == accountId && e.PostedAt >= dayStart && e.Amount < 0)
            .SumAsync(e => (decimal?)e.Amount) ?? 0m;
        return -sum;
    }

    /// <summary>Lock the given account rows in deterministic (id) order for the current transaction.</summary>
    private async Task<List<Account>> LockAccountsAsync(Guid[] ids)
    {
        var unique = ids.Distinct().OrderBy(i => i).ToArray();
        var parameters = unique.Select((id, i) => new NpgsqlParameter("p" + i, id)).ToArray();
        var where = string.Join(" OR ", unique.Select((_, i) => $"id = @p{i}"));
        var sql = "SELECT id, account_number, user_id, type, currency, status, balance, version, created_at, product_id " +
                  $"FROM accounts WHERE ({where}) ORDER BY id FOR UPDATE";

        var result = await _db.Accounts.FromSqlRaw(sql, parameters).ToListAsync();
        if (result.Count != unique.Length)
            throw BankException.NotFound("account_not_found", "One or more accounts were not found.");
        return result;
    }

    /// <summary>Find-or-create the bank-side omnibus account for a currency (inside the caller's transaction).</summary>
    private async Task<Account> EnsureSystemOmnibusAsync(string currency)
    {
        currency = currency.Trim().ToUpperInvariant();
        var number = $"{SystemAccountPrefix}{currency}-OMNIBUS";

        var existing = await _db.Accounts.SingleOrDefaultAsync(a => a.AccountNumber == number);
        if (existing is not null) return existing;

        var systemUser = await _db.Users.SingleOrDefaultAsync(u => u.Email == SystemUserEmail)
            ?? throw new InvalidOperationException("System user is missing — seed the database first.");

        var account = new Account
        {
            AccountNumber = number,
            UserId = systemUser.Id,
            Type = AccountType.Current,
            Currency = currency,
            Status = AccountStatus.Active,
            Balance = 0m,
            Version = 0
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync(); // inside caller's transaction — created atomically with it
        return account;
    }

    private decimal ValidateAmount(decimal amount)
    {
        if (amount <= 0m)
            throw BankException.BadRequest("invalid_amount", "Amount must be positive.");
        if (decimal.Round(amount, 4) != amount)
            throw BankException.BadRequest("invalid_amount_precision", "Amount may have at most 4 decimal places.");
        if (amount > MaxSingleTransaction)
            throw BankException.BadRequest("amount_exceeds_limit",
                $"Amount exceeds the single-transaction limit of {MaxSingleTransaction:F2}.");
        return amount;
    }

    private static void ValidateIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw BankException.BadRequest("missing_idempotency_key",
                "Idempotency-Key header is required for money movements.");
        if (key.Length > 64 || key.Any(char.IsControl))
            throw BankException.BadRequest("invalid_idempotency_key",
                "Idempotency-Key is invalid (max 64 chars, no control characters).");
    }
}
