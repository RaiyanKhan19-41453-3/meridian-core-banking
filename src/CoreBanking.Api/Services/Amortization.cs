namespace CoreBanking.Api.Services;

public readonly record struct AmortizationRow(
    int InstallmentNo,
    DateTime DueDate,
    decimal Principal,
    decimal Interest,
    decimal Total);

/// <summary>
/// Fixed-rate EMI (annuity) schedule on a reducing balance, monthly compounding.
/// <paramref name="annualInterestRate"/> is a decimal FRACTION (0.18 = 18% p.a.),
/// the convention used by the products/loan_products seed data.
/// Installment amounts are rounded to 4dp; the final row absorbs any rounding
/// remainder so Σ Principal == Principal exactly.
/// </summary>
public static class Amortization
{
    public static List<AmortizationRow> BuildSchedule(decimal principal, decimal annualInterestRate,
        int termMonths, DateTime firstDueDateUtc)
    {
        if (principal <= 0m) throw new ArgumentOutOfRangeException(nameof(principal));
        if (termMonths <= 0) throw new ArgumentOutOfRangeException(nameof(termMonths));
        if (annualInterestRate < 0m) throw new ArgumentOutOfRangeException(nameof(annualInterestRate));

        var r = annualInterestRate / 12m; // monthly rate; 0.18 p.a. -> 0.015 monthly
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
