using OtpNet;

namespace CoreBanking.Api.Services;

/// <summary>TOTP (RFC 6238) helper — SHA1, 30s step, 6 digits, ±1 window.</summary>
public static class TotpService
{
    public static string GenerateSecretBase32()
    {
        var key = KeyGeneration.GenerateRandomKey(20); // 160 bits
        return Base32Encoding.ToString(key);
    }

    public static bool Validate(string base32Secret, string code)
    {
        try
        {
            var bytes = Base32Encoding.ToBytes(base32Secret.Replace(" ", "").ToUpperInvariant());
            var totp = new Totp(bytes, step: 30, mode: OtpHashMode.Sha1, totpSize: 6);
            return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: 1, future: 1));
        }
        catch
        {
            return false;
        }
    }

    public static string BuildOtpAuthUri(string email, string base32Secret, string issuer = "Meridian Core Banking")
    {
        var label = Uri.EscapeDataString($"{issuer}:{email}");
        var issuerParam = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={issuerParam}&algorithm=SHA1&digits=6&period=30";
    }
}
