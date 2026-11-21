using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreBanking.Api.Services;

/// <summary>Stable fingerprint of a request payload — used with Idempotency-Key to detect mismatched replays.</summary>
public static class RequestFingerprint
{
    public static string Compute(object payload)
    {
        // Include the runtime type: DepositRequest and WithdrawRequest have
        // identical properties, so hashing only their JSON would let the same
        // Idempotency-Key replay a deposit as a withdrawal (and vice versa),
        // returning a misleading 200 while moving no money.
        var json = payload.GetType().Name + ":" + JsonSerializer.Serialize(payload, payload.GetType());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
