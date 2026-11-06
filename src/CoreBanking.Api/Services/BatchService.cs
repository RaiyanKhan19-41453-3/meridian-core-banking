using System.Text.Json;
using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreBanking.Api.Services;

public record BatchResult(BatchRun Run, bool Replayed);

/// <summary>Shared mutable counters for one batch execution.</summary>
public sealed class BatchCounters
{
    public int Processed;
    public int Failed;
    public readonly List<object> Errors = new();
}

/// <summary>
/// Day-end and month-end jobs. Every posting is double-entry, FX-normalized
/// into the USD general ledger, and atomic per account. Jobs are idempotent:
/// a (job, run_date) pair runs at most once; a repeat call replays the stored
/// result. Per-account work commits in its own transaction — one bad account
/// never blocks the rest; failures are recorded and the run is marked Partial.
/// </summary>
public class BatchService
{
    private readonly AppDbContext _db;
    private readonly GlService _gl;
    private readonly AuditService _audit;
    private readonly LoanService _loans;
    private readonly ILogger<BatchService> _logger;

    public BatchService(AppDbContext db, GlService gl, AuditService audit, LoanService loans, ILogger<BatchService> logger)
    {
        _db = db;
        _gl = gl;
        _audit = audit;
        _loans = loans;
        _logger = logger;
    }

    // ---------- entry points ----------

    public async Task<BatchResult> RunDayEndAsync(DateTime? asOf, Guid? adminId, string? ip)
        => await RunGuardedAsync(BatchJobType.DayEnd,
            DateTime.SpecifyKind((asOf?.Date ?? DateTime.UtcNow.Date).Date, DateTimeKind.Utc), async (run, counters) =>
        {
            var accruals = await AccrueInterestAsync(run.RunDate, adminId, ip, counters);
            var overdraftAccruals = await AccrueOverdraftInterestAsync(run.RunDate, adminId, ip, counters);
            var capitalized = 0;
            var fees = 0;
            var overdraftCharges = 0;
            var loansPaid = 0;
            var loansMissed = 0;

            if (run.RunDate == new DateTime(run.RunDate.Year, run.RunDate.Month,
                    DateTime.DaysInMonth(run.RunDate.Year, run.RunDate.Month)))
                capitalized = await CapitalizeInterestAsync(run.RunDate, adminId, ip, counters);

            if (run.RunDate.Day == 1)
            {
                fees = await ChargeFeesForMonthAsync(run.RunDate.Year, run.RunDate.Month, adminId, ip, counters);
                overdraftCharges = await ChargeOverdraftInterestAsync(run.RunDate.Year, run.RunDate.Month, adminId, ip, counters);
            }

            (loansPaid, loansMissed) = await _loans.ProcessRepaymentsAsync(run.RunDate, counters, adminId, ip);

            return new { accruals, overdraftAccruals, capitalized, feesCharged = fees,
                         overdraftCharged = overdraftCharges,
                         loanInstallmentsPaid = loansPaid, loanInstallmentsMissed = loansMissed };
        }, adminId, ip);

    public async Task<BatchResult> RunLoanRepaymentsAsync(DateTime asOf, Guid? adminId, string? ip)
        => await RunGuardedAsync(BatchJobType.Repayment, asOf.Date, async (run, counters) =>
        {
            var (paid, missed) = await _loans.ProcessRepaymentsAsync(run.RunDate, counters, adminId, ip);
            return new { loanInstallmentsPaid = paid, loanInstallmentsMissed = missed };
        }, adminId, ip);

    public async Task<BatchResult> RunOverdraftChargeAsync(int year, int month, Guid? adminId, string? ip)
        => await RunGuardedAsync(BatchJobType.OverdraftCharge,
            DateTime.SpecifyKind(new DateTime(year, month, 1), DateTimeKind.Utc), async (run, counters) =>
            {
                var charged = await ChargeOverdraftInterestAsync(year, month, adminId, ip, counters);
                return new { charged };
            }, adminId, ip);

    public async Task<BatchResult> RunOverdraftAccrualAsync(DateTime asOf, Guid? adminId, string? ip)
        => await RunGuardedAsync(BatchJobType.OverdraftAccrual, asOf.Date, async (run, counters) =>
        {
            var accrued = await AccrueOverdraftInterestAsync(run.RunDate, adminId, ip, counters);
            return new { overdraftAccruals = accrued };
        }, adminId, ip);

    public async Task<BatchResult> RunCapitalizationAsync(DateTime asOf, Guid? adminId, string? ip)
        => await RunGuardedAsync(BatchJobType.Capitalization, asOf.Date, async (run, counters) =>
        {
            var capitalized = await CapitalizeInterestAsync(asOf.Date, adminId, ip, counters);
            return new { capitalized };
        }, adminId, ip);

    public async Task<BatchResult> RunFeeChargeAsync(int year, int month, Guid? adminId, string? ip)
        => await RunGuardedAsync(BatchJobType.FeeRun,
            DateTime.SpecifyKind(new DateTime(year, month, 1), DateTimeKind.Utc), async (run, counters) =>
        {
            var feesCharged = await ChargeFeesForMonthAsync(year, month, adminId, ip, counters);
            return new { feesCharged };
        }, adminId, ip);

    public async Task<List<BatchRun>> RecentRunsAsync(int count = 20) =>
        await _db.BatchRuns.AsNoTracking()
            .OrderByDescending(b => b.Id)
            .Take(Math.Clamp(count, 1, 100))
            .ToListAsync();

    // ---------- steps ----------

    private async Task<int> AccrueInterestAsync(DateTime date, Guid? adminId, string? ip, BatchCounters counters)
    {
        date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var candidates = await _db.Accounts
            .Include(a => a.Product)
            .Where(a => a.Status == AccountStatus.Active
                        && a.ProductId != null
                        && a.Product!.AnnualInterestRate > 0m
                        && a.Balance > 0m
                        && !a.AccountNumber.StartsWith("SYS-"))
            .ToListAsync();

        var accrued = 0;
        foreach (var account in candidates)
        {
            // idempotency: skip accounts already accrued for this date (safe rerun)
            if (await _db.InterestAccruals.AnyAsync(a => a.AccountId == account.Id && a.AccrualDate == date))
            {
                counters.Processed++;
                accrued++;
                continue;
            }

            var interest = Math.Round(account.Balance * account.Product!.AnnualInterestRate / 100 / 365,
                4, MidpointRounding.AwayFromZero);
            if (interest <= 0m)
            {
                counters.Processed++;
                continue;
            }

            var ok = await RunItemAsync(counters, $"accrue:{account.AccountNumber}", async () =>
            {
                var usd = interest * await _gl.UsdRateForAsync(date, account.Currency);
                await _gl.AttachRawAsync(null, date,
                    $"Interest accrual {account.AccountNumber} {date:yyyy-MM-dd}",
                    new[] { (GlCodes.InterestExpense, usd), (GlCodes.InterestPayable, -usd) },
                    date);

                _db.InterestAccruals.Add(new InterestAccrual
                {
                    AccountId = account.Id,
                    AccrualDate = date,
                    Amount = interest,
                    Capitalized = false
                });
                await _db.SaveChangesAsync();
            });

            if (ok) { counters.Processed++; accrued++; }
        }

        return accrued;
    }

    private async Task<int> AccrueOverdraftInterestAsync(DateTime date, Guid? adminId, string? ip, BatchCounters counters)
    {
        date = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var facilities = await _db.OverdraftFacilities
            .Include(f => f.Account)
            .Where(f => f.Status == OverdraftStatus.Active
                        && f.Account.Status == AccountStatus.Active
                        && f.Account.Balance < 0m)
            .ToListAsync();

        var accrued = 0;
        foreach (var facility in facilities)
        {
            // idempotency: skip facilities already accrued for this date (safe rerun)
            if (await _db.OverdraftAccruals.AnyAsync(a => a.FacilityId == facility.Id && a.AccrualDate == date))
            {
                counters.Processed++;
                continue;
            }

            var owed = -facility.Account.Balance * facility.AnnualInterestRate / 100m / 365m;
            owed = Math.Round(owed, 4, MidpointRounding.AwayFromZero);
            if (owed <= 0m)
            {
                counters.Processed++;
                continue;
            }

            var ok = await RunItemAsync(counters, $"odaccrue:{facility.Reference}", async () =>
            {
                var usd = owed * await _gl.UsdRateForAsync(date, facility.Account.Currency);
                await _gl.AttachRawAsync(null, date,
                    $"Overdraft interest accrual {facility.Account.AccountNumber} {date:yyyy-MM-dd}",
                    new[] { (GlCodes.InterestReceivable, usd), (GlCodes.LoanInterestIncome, -usd) },
                    date);

                _db.OverdraftAccruals.Add(new OverdraftAccrual
                {
                    FacilityId = facility.Id,
                    AccountId = facility.AccountId,
                    AccrualDate = date,
                    Amount = owed,
                    Charged = false
                });
                await _db.SaveChangesAsync();
            });

            if (ok) { counters.Processed++; accrued++; }
        }

        return accrued;
    }

    private async Task<int> ChargeOverdraftInterestAsync(int year, int month, Guid? adminId, string? ip, BatchCounters counters)
    {
        var period = $"{year:D4}{month:D2}";
        var monthEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);

        var groups = await _db.OverdraftAccruals
            .Where(a => !a.Charged && a.AccrualDate <= monthEnd)
            .GroupBy(a => a.FacilityId)
            .Select(g => new { FacilityId = g.Key, Total = g.Sum(a => a.Amount) })
            .ToListAsync();

        var charged = 0;
        foreach (var group in groups)
        {
            var total = group.Total;
            if (total <= 0m) continue;

            var facility = await _db.OverdraftFacilities
                .Include(f => f.Account)
                .SingleAsync(f => f.Id == group.FacilityId);
            var reference = $"ODINT-{facility.Reference}-{period}";

            // deterministic reference → reruns never double-charge
            if (await _db.Transactions.AnyAsync(t => t.Reference == reference))
            {
                await MarkOverdraftChargedAsync(group.FacilityId, monthEnd);
                counters.Processed++;
                charged++;
                continue;
            }

            var ok = await RunItemAsync(counters, $"odcharge:{facility.Reference}", async () =>
            {
                var clearing = await EnsureClearingAccountAsync(facility.Account.Currency, "ODINT");
                var locked = await LockAccountsAsync(new[] { facility.AccountId, clearing.Id });
                var byId = locked.ToDictionary(a => a.Id);
                var customer = byId[facility.AccountId];
                var sweep = byId[clearing.Id];

                var usd = await _gl.UsdRateForAsync(monthEnd, customer.Currency);
                var tx = new Transaction
                {
                    Reference = reference,
                    RequestHash = RequestFingerprint.Compute(new { reference }),
                    Type = TransactionType.Fee,
                    Status = TransactionStatus.Posted,
                    CreatedBy = adminId ?? await SystemUserIdAsync(),
                    Description = $"Overdraft interest {new DateTime(year, month, 1):MMMM yyyy}",
                    CreatedAt = DateTime.UtcNow
                };

                var entries = new List<LedgerEntry>
                {
                    new() { TransactionId = tx.Id, AccountId = customer.Id, Amount = -total,
                            BalanceAfter = customer.Balance - total, PostedAt = DateTime.UtcNow },
                    new() { TransactionId = tx.Id, AccountId = sweep.Id, Amount = total,
                            BalanceAfter = sweep.Balance + total, PostedAt = DateTime.UtcNow },
                };

                _db.Transactions.Add(tx);
                _db.LedgerEntries.AddRange(entries);

                // GL: Dr Customer Deposits (total) · Cr Interest Receivable (total)
                await _gl.AttachRawAsync(tx.Id, DateTime.UtcNow, tx.Description,
                    new[]
                    {
                        (GlCodes.CustomerDeposits, total * usd),
                        (GlCodes.InterestReceivable, -total * usd),
                    }, monthEnd);

                customer.Balance -= total; customer.Version += 1;
                sweep.Balance += total; sweep.Version += 1;

                await MarkOverdraftChargedAsync(group.FacilityId, monthEnd, tx.Id);

                _audit.Write(adminId, "overdraft.interest_charged", nameof(Transaction), tx.Id.ToString(),
                    new { reference, amount = total, account = customer.AccountNumber, facility = facility.Reference }, ip);

                await _db.SaveChangesAsync();
            });

            if (ok) { counters.Processed++; charged++; }
        }

        return charged;
    }

    private async Task MarkOverdraftChargedAsync(Guid facilityId, DateTime asOf, Guid? transactionId = null)
    {
        var rows = await _db.OverdraftAccruals
            .Where(a => a.FacilityId == facilityId && !a.Charged && a.AccrualDate <= asOf)
            .ToListAsync();
        foreach (var a in rows)
        {
            a.Charged = true;
            a.ChargedAt = DateTime.UtcNow;
            if (transactionId is not null) a.ChargeTransactionId = transactionId;
        }
        await _db.SaveChangesAsync();
    }

    private async Task<int> CapitalizeInterestAsync(DateTime asOf, Guid? adminId, string? ip, BatchCounters counters)
    {
        asOf = DateTime.SpecifyKind(asOf.Date, DateTimeKind.Utc);
        var accruals = await _db.InterestAccruals
            .Where(a => !a.Capitalized && a.AccrualDate <= asOf)
            .ToListAsync();

        var count = 0;
        foreach (var group in accruals.GroupBy(a => a.AccountId))
        {
            var total = group.Sum(a => a.Amount);
            if (total <= 0m) continue;

            var account = await _db.Accounts.AsNoTracking().SingleAsync(a => a.Id == group.Key);
            var reference = $"INT-{asOf:yyyyMMdd}-{account.AccountNumber}";

            // deterministic reference → reruns never double-pay
            if (await _db.Transactions.AnyAsync(t => t.Reference == reference))
            {
                await MarkCapitalizedAsync(group, asOf);
                counters.Processed++;
                count++;
                continue;
            }

            var ok = await RunItemAsync(counters, $"capitalize:{account.AccountNumber}", async () =>
            {
                var clearing = await EnsureClearingAccountAsync(account.Currency, "INTCLR");
                var locked = await LockAccountsAsync(new[] { account.Id, clearing.Id });
                var byId = locked.ToDictionary(a => a.Id);
                var customer = byId[account.Id];
                var sweep = byId[clearing.Id];

                var tx = new Transaction
                {
                    Reference = reference,
                    RequestHash = RequestFingerprint.Compute(new { reference }),
                    Type = TransactionType.Interest,
                    Status = TransactionStatus.Posted,
                    CreatedBy = adminId ?? await SystemUserIdAsync(),
                    Description = $"Interest paid up to {asOf:yyyy-MM-dd}",
                    CreatedAt = DateTime.UtcNow
                };

                var entries = new List<LedgerEntry>
                {
                    new() { TransactionId = tx.Id, AccountId = sweep.Id, Amount = -total,
                            BalanceAfter = sweep.Balance - total, PostedAt = DateTime.UtcNow },
                    new() { TransactionId = tx.Id, AccountId = customer.Id, Amount = total,
                            BalanceAfter = customer.Balance + total, PostedAt = DateTime.UtcNow },
                };

                _db.Transactions.Add(tx);
                _db.LedgerEntries.AddRange(entries);
                await _gl.AttachAsync(tx, entries, byId, customer.Currency, asOf);

                sweep.Balance -= total; sweep.Version += 1;
                customer.Balance += total; customer.Version += 1;

                foreach (var accrual in group)
                {
                    accrual.Capitalized = true;
                    accrual.CapitalizedAt = DateTime.UtcNow;
                    accrual.CapitalizationTransactionId = tx.Id;
                }

                _audit.Write(adminId, "interest.capitalized", nameof(Transaction), tx.Id.ToString(),
                    new { reference, amount = total, account = customer.AccountNumber }, ip);

                await _db.SaveChangesAsync();
            });

            if (ok) { counters.Processed++; count++; }
        }

        return count;
    }

    private async Task<int> ChargeFeesForMonthAsync(int year, int month, Guid? adminId, string? ip, BatchCounters counters)
    {
        var period = $"{year:D4}{month:D2}";
        var feeProducts = await _db.Products
            .Where(p => p.MonthlyFee > 0m)
            .ToDictionaryAsync(p => p.Id);
        if (feeProducts.Count == 0) return 0;

        var accounts = await _db.Accounts
            .Where(a => a.ProductId != null && a.Status == AccountStatus.Active
                        && !a.AccountNumber.StartsWith("SYS-"))
            .ToListAsync();

        var count = 0;
        foreach (var account in accounts)
        {
            var product = feeProducts.GetValueOrDefault(account.ProductId!.Value);
            if (product is null) continue;
            if (account.Balance >= product.FeeWaiverMinBalance) continue; // waived

            var reference = $"FEE-{period}-{account.AccountNumber}";
            if (await _db.Transactions.AnyAsync(t => t.Reference == reference)) continue;

            var fee = product.MonthlyFee;
            var ok = await RunItemAsync(counters, $"fee:{account.AccountNumber}", async () =>
            {
                var clearing = await EnsureClearingAccountAsync(account.Currency, "FEECLR");
                var locked = await LockAccountsAsync(new[] { account.Id, clearing.Id });
                var byId = locked.ToDictionary(a => a.Id);
                var customer = byId[account.Id];
                var income = byId[clearing.Id];

                var tx = new Transaction
                {
                    Reference = reference,
                    RequestHash = RequestFingerprint.Compute(new { reference }),
                    Type = TransactionType.Fee,
                    Status = TransactionStatus.Posted,
                    CreatedBy = adminId ?? await SystemUserIdAsync(),
                    Description = $"Monthly account fee {new DateTime(year, month, 1):MMMM yyyy}",
                    CreatedAt = DateTime.UtcNow
                };

                var entries = new List<LedgerEntry>
                {
                    new() { TransactionId = tx.Id, AccountId = customer.Id, Amount = -fee,
                            BalanceAfter = customer.Balance - fee, PostedAt = DateTime.UtcNow },
                    new() { TransactionId = tx.Id, AccountId = income.Id, Amount = fee,
                            BalanceAfter = income.Balance + fee, PostedAt = DateTime.UtcNow },
                };

                _db.Transactions.Add(tx);
                _db.LedgerEntries.AddRange(entries);
                await _gl.AttachAsync(tx, entries, byId, customer.Currency,
                    new DateTime(year, month, 1));

                customer.Balance -= fee; customer.Version += 1; // fees may overdraw — flagged in audit
                income.Balance += fee; income.Version += 1;

                _audit.Write(adminId, "fee.charged", nameof(Transaction), tx.Id.ToString(),
                    new { reference, amount = fee, account = customer.AccountNumber,
                          balanceBefore = customer.Balance + fee, overdraft = customer.Balance < 0 }, ip);

                await _db.SaveChangesAsync();
            });

            if (ok) { counters.Processed++; count++; }
        }

        return count;
    }

    // ---------- plumbing ----------

    private async Task<BatchResult> RunGuardedAsync(BatchJobType jobType, DateTime runDate,
        Func<BatchRun, BatchCounters, Task<object>> body, Guid? adminId, string? ip)
    {
        runDate = DateTime.SpecifyKind(runDate.Date, DateTimeKind.Utc);
        var existing = await _db.BatchRuns.AsNoTracking()
            .SingleOrDefaultAsync(b => b.JobType == jobType && b.RunDate == runDate);
        if (existing is not null)
            return new BatchResult(existing, Replayed: true);

        var run = new BatchRun
        {
            JobType = jobType,
            RunDate = runDate.Date,
            Status = BatchRunStatus.Partial,
            StartedAt = DateTime.UtcNow
        };
        _db.BatchRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(); // claim the (job, date) slot
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            var raced = await _db.BatchRuns.AsNoTracking()
                .SingleAsync(b => b.JobType == jobType && b.RunDate == runDate);
            return new BatchResult(raced, Replayed: true);
        }

        var counters = new BatchCounters();
        object? summary = null;
        try
        {
            summary = await body(run, counters);
        }
        catch (Exception ex)
        {
            counters.Failed++;
            counters.Errors.Add(new { context = "batch.body", error = ex.GetBaseException().Message });
        }

        await FinalizeAsync(run, summary, counters, jobType, adminId, ip);
        return new BatchResult(run, Replayed: false);
    }

    /// <summary>Reattach the claim row to the current DbContext — body operations may have detached it.</summary>
    private void ReattachRun(BatchRun run)
    {
        if (_db.Entry(run).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            _db.Entry(run).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
    }

    /// <summary>Finalize a run — on failure, mark it Failed instead of leaving an eternal Partial.</summary>
    private async Task FinalizeAsync(BatchRun run, object? summary, BatchCounters counters,
        BatchJobType jobType, Guid? adminId, string? ip)
    {
        // Body may call ChangeTracker.Clear() after a rollback (accrual/fee/repayment paths),
        // which detaches the claim row. Reattach so the finalizer always emits UPDATE batch_runs.
        ReattachRun(run);

        try
        {
            run.ItemsProcessed = counters.Processed;
            run.ItemsFailed = counters.Failed;
            run.Status = counters.Failed == 0 ? BatchRunStatus.Succeeded : BatchRunStatus.Partial;
            run.Errors = counters.Errors.Count == 0 ? null : JsonSerializer.Serialize(counters.Errors);
            run.Summary = summary is null ? null : JsonSerializer.Serialize(summary);
            run.FinishedAt = DateTime.UtcNow;

            _audit.Write(adminId, "batch.completed", nameof(BatchRun), run.Id.ToString(),
                new { jobType = jobType.ToString(), runDate = run.RunDate, run.Status, run.ItemsProcessed, run.ItemsFailed }, ip);

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to finalize batch {JobType} run {RunDate} ({Processed} processed, {Failed} failed) — marking run Failed",
                jobType, run.RunDate, counters.Processed, counters.Failed);
            try
            {
                _db.ChangeTracker.Clear();
                var stale = await _db.BatchRuns.SingleOrDefaultAsync(b => b.Id == run.Id);
                if (stale is not null)
                {
                    stale.Status = BatchRunStatus.Failed;
                    stale.FinishedAt = DateTime.UtcNow;
                    stale.Errors = JsonSerializer.Serialize(new[] { new { context = "batch.finalize", error = ex.GetBaseException().Message } });
                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Also failed to mark interrupted batch run {Id} as Failed — boot reconciliation will", run.Id);
            }
        }
    }

    /// <summary>Run one account's item in its own transaction; record the failure and continue.</summary>
    private async Task<bool> RunItemAsync(BatchCounters counters, string item, Func<Task> work)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            await work();
            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _db.ChangeTracker.Clear();
            counters.Failed++;
            counters.Errors.Add(new { item, error = ex.GetBaseException().Message });
            return false;
        }
    }

    private async Task<Guid> SystemUserIdAsync()
    {
        var id = await _db.Users.Where(u => u.Email == "system@meridian.internal")
            .Select(u => (Guid?)u.Id).SingleOrDefaultAsync();
        return id ?? await _db.Users.Where(u => u.Role == UserRole.Admin)
            .OrderBy(u => u.CreatedAt).Select(u => u.Id).FirstAsync();
    }

    private async Task<Account> EnsureClearingAccountAsync(string currency, string suffix)
    {
        var number = $"SYS-{currency}-{suffix}";
        var existing = await _db.Accounts.SingleOrDefaultAsync(a => a.AccountNumber == number);
        if (existing is not null) return existing;

        var account = new Account
        {
            AccountNumber = number,
            UserId = await SystemUserIdAsync(),
            Type = AccountType.Current,
            Currency = currency.Trim().ToUpperInvariant(),
            Status = AccountStatus.Active,
            Balance = 0m,
            Version = 0
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        return account;
    }

    private async Task<List<Account>> LockAccountsAsync(Guid[] ids)
    {
        var unique = ids.Distinct().OrderBy(i => i).ToArray();
        var parameters = unique.Select((id, i) => new NpgsqlParameter("p" + i, id)).ToArray();
        var where = string.Join(" OR ", unique.Select((_, i) => $"id = @p{i}"));
        var sql = "SELECT id, account_number, user_id, type, currency, status, balance, version, created_at, product_id " +
                  $"FROM accounts WHERE ({where}) ORDER BY id FOR UPDATE";
        var result = await _db.Accounts.FromSqlRaw(sql, parameters).ToListAsync();
        if (result.Count != unique.Length)
            throw BankException.NotFound("account_not_found", "One or more accounts were not found during lock.");
        return result;
    }

    private async Task MarkCapitalizedAsync(IEnumerable<InterestAccrual> group, DateTime asOf)
    {
        var referencePrefix = $"INT-{asOf:yyyyMMdd}-";
        var txId = await _db.Transactions
            .Where(t => t.Reference.StartsWith(referencePrefix))
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync();
        foreach (var a in group)
        {
            a.Capitalized = true;
            a.CapitalizedAt = DateTime.UtcNow;
            if (txId is not null) a.CapitalizationTransactionId = txId;
        }
        await _db.SaveChangesAsync();
    }
}
