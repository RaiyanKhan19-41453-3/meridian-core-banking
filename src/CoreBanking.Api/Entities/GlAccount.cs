namespace CoreBanking.Api.Entities;

public enum GlAccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense
}

/// <summary>
/// Chart of accounts (USD base currency). Leaf accounts only — no control
/// accounts with children for now. Balanced double-entry always.
/// </summary>
public class GlAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Four-digit code, e.g. 1001.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public GlAccountType Type { get; set; }

    public ICollection<GlEntry> Entries { get; set; } = new List<GlEntry>();
}
