using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreBanking.Api.Services;

/// <summary>Stable fingerprint of a request payload — used with Idempotency-Key to detect mismatched replays.</summary>
public static class RequestFingerprint
{
    public static string Compute(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
