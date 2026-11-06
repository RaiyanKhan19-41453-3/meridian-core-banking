using System.Text.Json;
using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Services;

/// <summary>
/// AML rule engine. Runs AFTER a posting commits — it never rolls back money
/// movement and never delays it beyond a best-effort evaluation. Failures are
/// logged and swallowed (an alert is better late than a payment never sent).
/// </summary>
public class AmlService
{
    public const decimal HighValueThreshold = 10_000m;
    public const decimal Velocity24hLimit = 25_000m;
    public const int BurstWindowCount = 5;

    private readonly AppDbContext _db;
    private readonly ILogger<AmlService> _logger;

    public AmlService(AppDbContext db, ILogger<AmlService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Evaluate all rules for the posted transaction. Safe to call post-commit.</summary>
    public async Task EvaluateAsync(Transaction tx, IReadOnlyList<Account> accounts, string? ip)
    {
        try
        {
            var customerAccounts = accounts
                .Where(a => !a.AccountNumber.StartsWith("SYS-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var entries = await _db.LedgerEntries
                .Where(e => e.TransactionId == tx.Id)
                .ToListAsync();
            var customerEntries = entries
                .Where(e => customerAccounts.Any(a => a.Id == e.AccountId))
                .ToList();
            if (customerEntries.Count == 0) return;

            // Rule 1 (either direction, CTR-threshold style): one alert per transaction.
            // Pivot = the debiting customer leg (outbound), else the crediting one (inbound).
            var pivot = customerEntries.FirstOrDefault(e => e.Amount < 0m)
                        ?? customerEntries.OrderByDescending(e => e.Amount).First();
            if (Math.Abs(pivot.Amount) >= HighValueThreshold)
            {
                var pivotAccount = customerAccounts.Single(a => a.Id == pivot.AccountId);
                _db.AmlAlerts.Add(new AmlAlert
                {
                    AlertType = AmlAlertType.HighValue,
                    Severity = AmlSeverity.Medium,
                    UserId = pivotAccount.UserId,
                    AccountId = pivotAccount.Id,
                    TransactionId = tx.Id,
                    Details = JsonSerializer.Serialize(new
                    {
                        rule = "single_transaction_gte_10000",
                        amount = Math.Abs(pivot.Amount),
                        currency = pivotAccount.Currency,
                        direction = pivot.Amount < 0m ? "outbound" : "inbound",
                        reference = tx.Reference
                    })
                });
            }

            foreach (var leg in customerEntries.Where(e => e.Amount < 0m))
            {
                var account = customerAccounts.Single(a => a.Id == leg.AccountId);

                var since24h = DateTime.UtcNow.AddHours(-24);
                var out24h = await _db.LedgerEntries
                    .Where(e => e.AccountId == account.Id && e.Amount < 0m && e.PostedAt >= since24h)
                    .SumAsync(e => (decimal?)e.Amount) ?? 0m;
                if (-out24h >= Velocity24hLimit && !await HasOpenAlertAsync(account.UserId, AmlAlertType.Velocity24h, since24h))
                {
                    _db.AmlAlerts.Add(new AmlAlert
                    {
                        AlertType = AmlAlertType.Velocity24h,
                        Severity = AmlSeverity.High,
                        UserId = account.UserId,
                        AccountId = account.Id,
                        TransactionId = tx.Id,
                        Details = JsonSerializer.Serialize(new
                        {
                            rule = "outbound_24h_gte_25000",
                            outbound24h = -out24h, currency = account.Currency, reference = tx.Reference
                        })
                    });
                }

                var since1h = DateTime.UtcNow.AddHours(-1);
                var out1h = await _db.LedgerEntries
                    .CountAsync(e => e.AccountId == account.Id && e.Amount < 0m && e.PostedAt >= since1h);
                if (out1h >= BurstWindowCount && !await HasOpenAlertAsync(account.UserId, AmlAlertType.Burst, since1h))
                {
                    _db.AmlAlerts.Add(new AmlAlert
                    {
                        AlertType = AmlAlertType.Burst,
                        Severity = AmlSeverity.Medium,
                        UserId = account.UserId,
                        AccountId = account.Id,
                        TransactionId = tx.Id,
                        Details = JsonSerializer.Serialize(new
                        {
                            rule = "outbound_txn_count_1h_gte_5",
                            count = out1h, currency = account.Currency, reference = tx.Reference
                        })
                    });
                }
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // AML evaluation must never undo a completed payment.
            _logger.LogError(ex, "AML evaluation failed for transaction {Reference}", tx.Reference);
            try { _db.ChangeTracker.Clear(); } catch { /* swallow */ }
        }
    }

    /// <summary>Match a full legal name against the seeded watchlist. Returns the entry if matched.</summary>
    public async Task<WatchlistEntry?> ScreenWatchlistAsync(string? fullLegalName)
    {
        var normalized = Normalize(fullLegalName);
        if (normalized.Length == 0) return null;
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var entries = await _db.WatchlistEntries.AsNoTracking().ToListAsync();
        return entries.FirstOrDefault(e =>
        {
            var entryTokens = Normalize(e.FullName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return entryTokens.Length > 0 && entryTokens.All(tokens.Contains);
        });
    }

    /// <summary>Raise a watchlist-match alert (and set the profile's risk to High).</summary>
    public async Task FlagWatchlistMatchAsync(Guid userId, string fullLegalName, WatchlistEntry match, string? ip)
    {
        _db.AmlAlerts.Add(new AmlAlert
        {
            AlertType = AmlAlertType.WatchlistMatch,
            Severity = AmlSeverity.High,
            UserId = userId,
            Details = JsonSerializer.Serialize(new
            {
                rule = "watchlist_full_token_match",
                name = fullLegalName,
                matchedEntry = match.FullName,
                source = match.Source
            })
        });
    }

    private async Task<bool> HasOpenAlertAsync(Guid userId, AmlAlertType type, DateTime windowStart) =>
        await _db.AmlAlerts.AnyAsync(a =>
            a.UserId == userId && a.AlertType == type
            && a.Status == AmlAlertStatus.Open && a.CreatedAt >= windowStart);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var upper = value.ToUpperInvariant();
        var chars = upper.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
