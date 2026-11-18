using CoreBanking.Api.Entities;

namespace CoreBanking.Api.Services;

/// <summary>
/// Deterministic internal credit score (300–850) for instant loan decisions.
/// Inputs are the customer's verified profile and the requested exposure.
///
/// <para>CIB (Credit Information Bureau) readiness — when a bureau score is
/// supplied at application time, it is blended into the internal score:
///
/// <c>final = clamp(round(0.6 × cib + 0.4 × internal), 300, 850)</c>
///
/// A supplied <c>cibScore</c> must be in [300, 850]. Decisions, audit logs and
/// DTOs record both raw bureau score and the source, so an external bureau
/// integration can replace the manual input without changing decision logic.</para>
/// </summary>
public static class CreditScoring
{
    public const int Minimum = 300;
    public const int Maximum = 850;
    /// <summary>Weight of the bureau (CIB) score in the blended final score.</summary>
    public const double CibWeight = 0.6;

    public static void ValidateCibScore(int? cibScore)
    {
        if (cibScore is null) return;
        if (cibScore < Minimum || cibScore > Maximum)
            throw BankException.BadRequest("cib_score_out_of_range",
                $"CIB score must be between {Minimum} and {Maximum}.");
    }

    /// <summary>Score for the requested principal. Exposure haircut applies above 20,000.</summary>
    public static int Score(CustomerProfile profile, decimal principal) =>
        ScoreInternal(profile, principal);

    /// <summary>Score for the requested principal with an optional bureau (CIB) score.
    /// <paramref name="usdRate"/> converts the local-currency principal into USD
    /// so exposure haircuts mean the same thing in every currency.</summary>
    public static (int FinalScore, int? CibScore, string Source) Score(
        CustomerProfile profile, decimal principal, int? cibScore, decimal usdRate = 1m)
    {
        var internalScore = ScoreInternal(profile, principal * usdRate);
        return Blend(internalScore, cibScore);
    }

    public static (int FinalScore, int? CibScore, string Source) Blend(int internalScore, int? cibScore)
    {
        if (cibScore is null)
            return (internalScore, null, "Internal");

        ValidateCibScore(cibScore);
        var blended = (int)Math.Round(
            CibWeight * cibScore.Value + (1 - CibWeight) * internalScore,
            MidpointRounding.AwayFromZero);
        return (Math.Clamp(blended, Minimum, Maximum), cibScore, "CIB Bureau");
    }

    private static int ScoreInternal(CustomerProfile profile, decimal principal)
    {
        var score = 700;

        var band = (profile.AnnualIncomeBand ?? "").Trim().ToLowerInvariant();
        score += band switch
        {
            "100k+" => 200,
            "50k-100k" => 150,
            "25k-50k" => -100,
            "<25k" => -300,
            _ => -150 // unknown
        };

        if (principal > 20_000m) score -= 150;       // elevated exposure
        if (principal > 100_000m) score -= 150;      // very large exposure

        // Profile seasoning: verified at least 30 days ago
        if (profile.VerifiedAt is { } verified && verified <= DateTime.UtcNow.AddDays(-30))
            score += 50;

        return Math.Clamp(score, Minimum, Maximum);
    }
}
