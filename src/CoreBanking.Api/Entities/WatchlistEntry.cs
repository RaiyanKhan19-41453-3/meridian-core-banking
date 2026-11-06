namespace CoreBanking.Api.Entities;

/// <summary>
/// Seeded watchlist (fictional demo names, OFAC-style). Screening is a
/// deterministic full-token match of the legal name against each entry.
/// </summary>
public class WatchlistEntry
{
    public long Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // e.g. "OFAC (demo)", "UN (demo)"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
