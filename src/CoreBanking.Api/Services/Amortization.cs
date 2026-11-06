namespace CoreBanking.Api.Services;

public readonly record struct AmortizationRow(
    int InstallmentNo,
    DateTime DueDate,
    decimal Principal,
    decimal Interest,
    decimal Total);

/// <summary>
/// Fixed-rate EMI (annuity) schedule on a reducing balance, monthly compounding.
/// Installment amounts are rounded to 4dp; the final row absorbs any rounding
/// remainder so Σ Principal == Principal exactly.
/// </summary>
public static class Amortization
{
    public static List<AmortizationRow> BuildSchedule(decimal principal, decimal annualInterestRatePercent,
        int termMonths, DateTime firstDueDateUtc)
    {
        if (principal <= 0m) throw new ArgumentOutOfRangeException(nameof(principal));
        if (termMonths <= 0) throw new ArgumentOutOfRangeException(nameof(termMonths));
        if (annualInterestRatePercent < 0m) throw new ArgumentOutOfRangeException(nameof(annualInterestRatePercent));

        var r = annualInterestRatePercent / 100m / 12m;
        var rows = new List<AmortizationRow>(termMonths);

        if (r == 0m)
        {
            var flat = Math.Round(principal / termMonths, 4);
            var remaining = principal;
            for (var i = 1; i <= termMonths; i++)
            {
                var p = i == termMonths ? remaining : flat;
                remaining -= p;
                rows.Add(new AmortizationRow(i, firstDueDateUtc.AddMonths(i - 1), p, 0m, p));
            }
            return rows;
        }

        var factor = (double)Math.Pow((double)(1m + r), termMonths);
        var emi = principal * r * (decimal)factor / ((decimal)factor - 1m);
        emi = Math.Round(emi, 4, MidpointRounding.AwayFromZero);

        var outstanding = principal;
        for (var i = 1; i <= termMonths; i++)
        {
            var interest = Math.Round(outstanding * r, 4, MidpointRounding.AwayFromZero);
            var principalPart = i == termMonths
                ? outstanding
                : Math.Round(emi - interest, 4, MidpointRounding.AwayFromZero);
            var total = principalPart + interest;
            rows.Add(new AmortizationRow(i, firstDueDateUtc.AddMonths(i - 1), principalPart, interest, total));
            outstanding -= principalPart;
        }

        return rows;
    }

    /// <summary>Total interest over the life of the loan.</summary>
    public static decimal TotalInterest(IEnumerable<AmortizationRow> rows) => rows.Sum(r => r.Interest);
}
