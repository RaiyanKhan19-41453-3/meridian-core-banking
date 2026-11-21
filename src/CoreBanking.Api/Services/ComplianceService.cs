using System.Text.Json;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Services;

/// <summary>
/// Regulatory reporting built on the existing AML alerts, ledger and GL:
/// CTR/SAR candidate lists, filing tracking, tax statements, sanctions-list
/// refresh and nostro/vostro-style reconciliation of subledger against GL.
/// </summary>
public class ComplianceService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly GlService _gl;

    public ComplianceService(AppDbContext db, AuditService audit, GlService gl)
    {
        _db = db;
        _audit = audit;
        _gl = gl;
    }

    private static ComplianceAlertDto ToDto(AmlAlert a, string email) => new(
        a.Id,
        a.AlertType == AmlAlertType.HighValue ? "CTR" : "SAR",
        a.AlertType.ToString(), a.Severity.ToString(), a.Status.ToString(),
        a.UserId, email, a.TransactionId, a.Details, a.CreatedAt, a.FiledAt, a.FiledReference);

    public async Task<IReadOnlyList<ComplianceAlertDto>> CtrAsync(DateTime from, DateTime to)
    {
        from = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var toExclusive = DateRange.ExclusiveEnd(to).GetValueOrDefault(DateTime.MaxValue);
        var rows = await _db.AmlAlerts
            .Where(a => a.AlertType == AmlAlertType.HighValue && a.CreatedAt >= from && a.CreatedAt < toExclusive)
            .OrderByDescending(a => a.CreatedAt)
            .Take(1000)
            .ToListAsync();
        return await WithEmailsAsync(rows);
    }

    public async Task<IReadOnlyList<ComplianceAlertDto>> SarAsync(DateTime from, DateTime to)
    {
        from = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var toExclusive = DateRange.ExclusiveEnd(to).GetValueOrDefault(DateTime.MaxValue);
        var rows = await _db.AmlAlerts
            .Where(a => a.AlertType != AmlAlertType.HighValue
                        && a.Severity == AmlSeverity.High
                        && a.CreatedAt >= from && a.CreatedAt < toExclusive)
            .OrderByDescending(a => a.CreatedAt)
            .Take(1000)
            .ToListAsync();
        return await WithEmailsAsync(rows);
    }

    private async Task<IReadOnlyList<ComplianceAlertDto>> WithEmailsAsync(List<AmlAlert> rows)
    {
        var ids = rows.Select(a => a.UserId).Distinct().ToList();
        var emails = await _db.Users.Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email);
        return rows.Select(a => ToDto(a, emails.GetValueOrDefault(a.UserId, "unknown"))).ToList();
    }

    /// <summary>Record a regulatory filing for an alert. Idempotent on the reference.</summary>
    public async Task<bool> FileAsync(Guid alertId, string reference, Guid adminId, string? ip)
    {
        var alert = await _db.AmlAlerts.SingleOrDefaultAsync(a => a.Id == alertId);
        if (alert is null) throw BankException.NotFound("alert_not_found", "Alert does not exist.");
        if (alert.FiledAt is not null) return false; // fast path

        // Atomic claim: concurrent filings with different references would all
        // pass the check above, overwriting each other and emitting duplicate
        // audit rows. Only the caller that flips FiledAt from NULL may file.
        var filedAt = DateTime.UtcNow;
        var trimmed = reference.Trim();
        var claimed = await _db.AmlAlerts
            .Where(a => a.Id == alertId && a.FiledAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.FiledAt, filedAt)
                .SetProperty(a => a.FiledBy, adminId)
                .SetProperty(a => a.FiledReference, trimmed)
                .SetProperty(a => a.Status, AmlAlertStatus.Reviewed)
                .SetProperty(a => a.ReviewedAt, filedAt)
                .SetProperty(a => a.ReviewedBy, adminId));
        if (claimed != 1) return false;

        _audit.Write(adminId, "compliance.filed", nameof(AmlAlert), alert.Id.ToString(),
            new { alert.AlertType, reference = trimmed }, ip);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<TaxStatementDto>> TaxStatementsAsync(Guid? userId, int year)
    {
        var yearStart = DateTime.SpecifyKind(new DateTime(year, 1, 1), DateTimeKind.Utc);
        // Exclusive upper bound: a <= Dec-31-00:00 check silently excluded every
        // transaction posted later on the last day of the year.
        var yearEndExclusive = DateTime.SpecifyKind(new DateTime(year, 12, 31).AddDays(1), DateTimeKind.Utc);

        var accountQuery = _db.Accounts.AsNoTracking()
            .Where(a => !a.AccountNumber.StartsWith("SYS-"));
        if (userId.HasValue) accountQuery = accountQuery.Where(a => a.UserId == userId.Value);
        var accounts = await accountQuery.ToListAsync();
        if (accounts.Count == 0) return Array.Empty<TaxStatementDto>();
        var accountById = accounts.ToDictionary(a => a.Id);
        var ids = accountById.Keys.ToList();

        var txns = await _db.Transactions.AsNoTracking()
            .Where(t => t.CreatedAt >= yearStart && t.CreatedAt < yearEndExclusive)
            .Select(t => new { t.Id, t.Type, t.Description })
            .ToListAsync();
        var txById = txns.ToDictionary(t => t.Id);

        var legs = await _db.LedgerEntries.AsNoTracking()
            .Where(e => ids.Contains(e.AccountId) && e.PostedAt >= yearStart && e.PostedAt < yearEndExclusive)
            .ToListAsync();

        var result = new List<TaxStatementDto>();
        foreach (var account in accounts)
        {
            var mine = legs.Where(e => e.AccountId == account.Id).ToList();
            decimal earned = 0m, interestPaid = 0m, feesPaid = 0m;
            foreach (var leg in mine)
            {
                if (!txById.TryGetValue(leg.TransactionId, out var tx)) continue;
                if (tx.Type == TransactionType.Interest && leg.Amount > 0m) earned += leg.Amount;
                else if (tx.Type == TransactionType.Fee && leg.Amount < 0m)
                {
                    if (tx.Description.StartsWith("Overdraft interest", StringComparison.OrdinalIgnoreCase))
                        interestPaid += -leg.Amount;
                    else
                        feesPaid += -leg.Amount;
                }
            }

            // Loan interest is stored separately in the GL (code 6101); retrieve
            // it per repayment and convert back into the account's currency.
            decimal loanInterest = 0m;
            var repayments = mine.Where(e => e.Amount < 0m
                                             && txById.TryGetValue(e.TransactionId, out var t)
                                             && t.Type == TransactionType.LoanRepayment)
                                 .Select(e => e.TransactionId).Distinct().ToList();
            if (repayments.Count > 0)
            {
                var glInterest = await _db.GlJournals.AsNoTracking()
                    .Where(j => j.TransactionId != null && repayments.Contains(j.TransactionId.Value))
                    .Select(j => new { j.TransactionId, j.UsdRate,
                        Amounts = j.Entries.Where(e => e.GlAccount.Code == "6101").Select(e => (decimal?)e.Amount).ToList() })
                    .ToListAsync();
                foreach (var j in glInterest)
                {
                    var usd = j.Amounts.Where(x => x.HasValue).Sum(x => x!.Value);
                    if (j.UsdRate > 0m) loanInterest += Math.Abs(usd / j.UsdRate);
                }
            }

            result.Add(new TaxStatementDto(account.Id, account.AccountNumber, year, new[]
            {
                new TaxLineDto(account.Currency, Math.Round(earned, 4), Math.Round(interestPaid, 4),
                    Math.Round(feesPaid, 4), Math.Round(loanInterest, 4))
            }));
        }
        return result;
    }

    /// <summary>
    /// Reconcile posted subledger movements against their mirrored GL journals
    /// per currency. Accruals (which have no subledger transaction) are excluded.
    /// </summary>
    public async Task<ReconciliationDto> ReconciliationAsync()
    {
        var lines = new List<ReconciliationLineDto>();
        foreach (var currency in new[] { "USD", "EUR", "GBP", "KES", "NGN", "BDT" })
        {
            var accountIds = await _db.Accounts.AsNoTracking()
                .Where(a => a.Currency == currency).Select(a => a.Id).ToListAsync();
            var ledgerNet = await _db.LedgerEntries.AsNoTracking()
                .Where(e => accountIds.Contains(e.AccountId))
                .Select(e => (decimal?)e.Amount).SumAsync() ?? 0m;

            var txIds = await _db.Transactions.AsNoTracking()
                .Where(t => t.Type != TransactionType.Reversal && t.Entries.Any(e => accountIds.Contains(e.AccountId)))
                .Select(t => t.Id).ToListAsync();
            var glUsdNet = await _db.GlEntries.AsNoTracking()
                .Where(e => e.Journal.TransactionId != null && txIds.Contains(e.Journal.TransactionId.Value))
                .Select(e => (decimal?)e.Amount).SumAsync() ?? 0m;

            var rate = await _gl.UsdRateForAsync(DateTime.UtcNow, currency);
            var expected = Math.Round(ledgerNet * rate, 4);
            var diff = Math.Round(glUsdNet - expected, 4);
            lines.Add(new ReconciliationLineDto(currency, Math.Round(ledgerNet, 4),
                Math.Round(glUsdNet, 4), expected, diff, Math.Abs(diff) <= 0.01m));
        }
        // Net sums are always zero by construction (every posting balances), so
        // they alone can never surface a real discrepancy. Also pair subledger
        // transactions with their GL journals. Internal customer-to-customer
        // transfers legitimately post no GL journal (net-zero balance sheet).
        var withoutJournal = await _db.Transactions.AsNoTracking()
            .Where(t => t.Status == TransactionStatus.Posted
                        && !_db.GlJournals.Any(j => j.TransactionId == t.Id))
            .Include(t => t.Entries).ThenInclude(e => e.Account)
            .Include(t => t.OriginalTransaction).ThenInclude(ot => ot.Entries).ThenInclude(e => e.Account)
            .ToListAsync();
        var missingGl = 0;
        foreach (var t in withoutJournal)
        {
            // A reversal mirrors its original, so judge the original's legs.
            var legs = t.Type == TransactionType.Reversal && t.OriginalTransaction is not null
                ? t.OriginalTransaction.Entries
                : t.Entries;
            var distinctCodes = legs
                .Select(e => GlCodes.CodeForAccountNumber(e.Account.AccountNumber))
                .Distinct().Count();
            // One GL code means a net-zero internal movement — no journal expected.
            if (distinctCodes > 1) missingGl++;
        }
        var orphanGl = await _db.GlJournals.AsNoTracking()
            .Where(j => j.TransactionId != null
                        && !_db.LedgerEntries.Any(e => e.TransactionId == j.TransactionId))
            .CountAsync();

        return new ReconciliationDto(lines,
            lines.All(l => l.Balanced) && missingGl == 0 && orphanGl == 0,
            missingGl, orphanGl);
    }

    /// <summary>Idempotent sanctions-list upsert, fully audited.</summary>
    public async Task<(int Added, int Skipped)> RefreshWatchlistAsync(
        IReadOnlyList<WatchlistEntryRequest> entries, Guid adminId, string? ip)
    {
        var added = 0; var skipped = 0;
        foreach (var entry in entries)
        {
            var name = entry.FullName.Trim();
            var source = entry.Source.Trim();
            if (name.Length == 0 || source.Length == 0) { skipped++; continue; }

            // Serialize refreshes of the same name — concurrent callers would
            // otherwise all pass the existence check and insert duplicates.
            await using var tx = await _db.Database.BeginTransactionAsync();
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(hashtext({0}), 3)", name);
            if (await _db.WatchlistEntries.AnyAsync(w => w.FullName == name && w.Source == source))
            { skipped++; continue; }
            _db.WatchlistEntries.Add(new WatchlistEntry { FullName = name, Source = source });
            _audit.Write(adminId, "compliance.watchlist_added", nameof(WatchlistEntry), name,
                new { name, source }, ip);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            added++;
        }
        return (added, skipped);
    }
}
