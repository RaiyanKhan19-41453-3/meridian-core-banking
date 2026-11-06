using System.ComponentModel.DataAnnotations;

namespace CoreBanking.Api.Dtos;

public record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(8), MaxLength(100)] string Password,
    [Required, MaxLength(200)] string FullName);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password,
    [StringLength(12)] string? TotpCode);

public record TokenPairResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    string Email,
    string FullName,
    string Role);

public record RefreshRequest([Required] string RefreshToken);
public record LogoutRequest([Required] string RefreshToken);

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(8), MaxLength(100)] string NewPassword);

public record MeResponse(
    Guid Id,
    string Email,
    string FullName,
    string Role,
    bool TwoFactorEnabled,
    string KycStatus,
    DateTime CreatedAt);

public record TotpSetupResponse(string SecretBase32, string OtpAuthUri);
public record TotpCodeRequest([Required, RegularExpression(@"^\d{6}$")] string Code);
