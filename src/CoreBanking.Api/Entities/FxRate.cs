namespace CoreBanking.Api.Entities;

/// <summary>
/// FX rates for the USD-base general ledger. One rate = USD per 1 unit of
/// foreign currency. GL postings are normalized to USD at posting time and the
/// applied rate is stored on the journal for audit.
/// </summary>
public class FxRate
{
    public long Id { get; set; }
    public string FromCurrency { get; set; } = string.Empty;
    public string ToCurrency { get; set; } = "USD";
    public decimal Rate { get; set; }
    public DateTime AsOf { get; set; }
}
