using System.Security.Cryptography;
using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Services;

/// <summary>
/// General ledger posting. For every subledger transaction (and every system
/// event like interest accrual) it builds exactly one balanced journal,
/// committed in the same DbContext/transaction as the subledger legs.
/// All legs are normalized to USD via the FX rate at business time.
/// </summary>
public class GlService
{
    private readonly AppDbContext _db;

    public GlService(AppDbContext db) => _db = db;

    /// <summary>
    /// Build a journal for a subledger posting. Legs are per-account signed
    /// amounts in the transaction currency; FX is applied here.
    /// </summary>
    public async Task<GlJournal?> AttachAsync(Transaction transaction, IReadOnlyList<LedgerEntry> legs,
        IReadOnlyDictionary<Guid, Account> accountsById, string currency, DateTime? businessDate = null)
    {
        var rate = await UsdRateForAsync(transaction.CreatedAt, currency);

        // Convention: GL legs are DEBIT-positive, i.e. the inverse of subledger
        // (balance-change) signs. Journals net to zero and every account's
        // running sum keeps a meaningful sign (+ = debit, − = credit).
        var glLegs = legs
            .GroupBy(l => GlCodes.CodeForAccountNumber(accountsById[l.AccountId].AccountNumber))
            .Select(g => (Code: g.Key, Usd: -g.Sum(l => l.Amount) * rate))
            .Where(l => l.Usd != 0m)
            .ToList();

        // Both legs in the same GL account (e.g. customer→customer transfer):
        // no balance-sheet movement — no journal at all.
        if (glLegs.Count == 0) return null;
        if (glLegs.Count == 1)
            throw new InvalidOperationException("GL legs do not sum to zero. This is a bug.");

        return await AttachRawAsync(transaction.Id, transaction.CreatedAt, transaction.Description,
            glLegs, businessDate, rate);
    }

    /// <summary>Post a balanced journal from raw GL legs (already in USD). Used by accruals and other non-subledger events.</summary>
    public async Task<GlJournal> AttachRawAsync(Guid? transactionId, DateTime createdAt, string description,
        IReadOnlyList<(string Code, decimal Usd)> legs, DateTime? businessDate = null, decimal usdRate = 1.00000000m)
    {
        if (legs.Count < 2)
            throw new InvalidOperationException("A GL journal needs at least two legs.");
        if (legs.Sum(l => l.Usd) != 0m)
            throw new InvalidOperationException("GL legs do not sum to zero. This is a bug.");

        var journal = new GlJournal
        {
            Number = GenerateNumber(),
            TransactionId = transactionId,
            BusinessDate = DateTime.SpecifyKind((businessDate ?? createdAt).Date, DateTimeKind.Utc),
            UsdRate = usdRate,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        var codes = legs.Select(l => l.Code).ToList();
        var accounts = await _db.GlAccounts
            .Where(g => codes.Contains(g.Code))
            .ToDictionaryAsync(g => g.Code);

        foreach (var (code, usd) in legs)
        {
            if (!accounts.TryGetValue(code, out var glAccount))
                throw BankException.Conflict("gl_account_missing",
                    $"GL account {code} ({GlCodes.NameFor(code)}) is not seeded.");

            journal.Entries.Add(new GlEntry
            {
                JournalId = journal.Id,
                GlAccountId = glAccount.Id,
                Amount = usd
            });
        }

        _db.GlJournals.Add(journal);
        _db.GlEntries.AddRange(journal.Entries);
        return journal;
    }

    /// <summary>USD per 1 unit of the given currency as of the latest seeded rate ≤ asOf (fallback: newest available).</summary>
    public async Task<decimal> UsdRateForAsync(DateTime asOf, string? currency)
    {
        var ccy = (currency ?? "USD").Trim().ToUpperInvariant();
        if (ccy == "USD") return 1.00000000m;

        var exact = await _db.FxRates
            .Where(r => r.FromCurrency == ccy && r.ToCurrency == "USD" && r.AsOf <= asOf)
            .OrderByDescending(r => r.AsOf)
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync();
        if (exact is not null) return exact.Value;

        var latest = await _db.FxRates
            .Where(r => r.FromCurrency == ccy && r.ToCurrency == "USD")
            .OrderByDescending(r => r.AsOf)
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync();
        return latest ?? throw BankException.Conflict("fx_rate_missing",
            $"No FX rate seeded for {ccy}/USD. GL cannot normalize.");
    }

    private static string GenerateNumber() =>
        $"GLJ-{DateTime.UtcNow:yyyyMMdd}-{RandomNumberGenerator.GetInt32(100000, 999999)}";
}
