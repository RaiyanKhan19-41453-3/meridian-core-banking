using CoreBanking.Api.Entities;

namespace CoreBanking.Api.Services;

/// <summary>
/// Deterministic internal credit score (300–850) for instant loan decisions.
/// Inputs are the customer's verified profile and the requested exposure.
/// </summary>
public static class CreditScoring
{
    public const int Minimum = 300;
    public const int Maximum = 850;

    /// <summary>Score for the requested principal. Exposure haircut applies above 20,000.</summary>
    public static int Score(CustomerProfile profile, decimal principal)
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
