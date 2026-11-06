using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CoreBanking.Api.Data;
using CoreBanking.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CoreBanking.Api.Services;

public record TokenPair(string AccessToken, DateTime AccessTokenExpiresAtUtc,
    string RefreshToken, DateTime RefreshTokenExpiresAtUtc);

public record RotateResult(TokenPair Pair, User User);

/// <summary>
/// Access tokens are short-lived JWTs. Refresh tokens are opaque, single-use,
/// rotated on every use, stored as SHA-256 hashes. Reusing a rotated token is
/// treated as theft: every session of that user is revoked.
/// </summary>
public class TokenService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public TokenService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private string Key => _config["Jwt:Key"]
        ?? throw new InvalidOperationException("Jwt:Key is not configured.");
    private string Issuer => _config["Jwt:Issuer"] ?? "core-banking";
    private string Audience => _config["Jwt:Audience"] ?? "core-banking-clients";
    private int AccessMinutes => int.TryParse(_config["Jwt:ExpiryMinutes"], out var m) ? m : 15;
    private int RefreshDays => int.TryParse(_config["Jwt:RefreshDays"], out var d) ? d : 7;

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
            SecurityAlgorithms.HmacSha256);

        var expires = DateTime.UtcNow.AddMinutes(AccessMinutes);
        var token = new JwtSecurityToken(Issuer, Audience, claims, expires: expires, signingCredentials: credentials);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>Issue a fresh access + refresh pair.</summary>
    public async Task<TokenPair> IssuePairAsync(User user, string? ipAddress, string? userAgent)
    {
        var (accessToken, accessExpires) = CreateAccessToken(user);

        var rawRefresh = GenerateOpaqueToken();
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashToken(rawRefresh),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(RefreshDays),
            IpAddress = ipAddress,
            UserAgent = Truncate(userAgent, 300)
        };
        _db.RefreshTokens.Add(refresh);
        await _db.SaveChangesAsync();

        return new TokenPair(accessToken, accessExpires, rawRefresh, refresh.ExpiresAt);
    }

    /// <summary>
    /// Rotate a refresh token. Throws BankException on invalid/expired/reused tokens.
    /// </summary>
    public async Task<RotateResult> RotateAsync(string rawRefresh, string? ipAddress, string? userAgent)
    {
        var hash = HashToken(rawRefresh);
        var stored = await _db.RefreshTokens
            .Include(r => r.User)
            .SingleOrDefaultAsync(r => r.TokenHash == hash);

        if (stored is null)
            throw BankException.Unprocessable("invalid_refresh_token", "Refresh token is not recognized.");

        if (stored.RevokedAt is not null || stored.ExpiresAt <= DateTime.UtcNow)
        {
            // A revoked/expired token was presented again — probable theft.
            if (stored.RevokedAt is not null)
            {
                await RevokeAllForUserAsync(stored.UserId);
                throw BankException.Conflict("refresh_token_reuse",
                    "Refresh token was already used. All sessions have been revoked.");
            }
            throw BankException.Unprocessable("refresh_token_expired", "Refresh token has expired.");
        }

        var (accessToken, accessExpires) = CreateAccessToken(stored.User);
        var rawNew = GenerateOpaqueToken();
        var next = new RefreshToken
        {
            UserId = stored.UserId,
            TokenHash = HashToken(rawNew),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(RefreshDays),
            IpAddress = ipAddress,
            UserAgent = Truncate(userAgent, 300)
        };

        _db.RefreshTokens.Add(next);
        stored.RevokedAt = DateTime.UtcNow;
        stored.ReplacedById = next.Id;
        await _db.SaveChangesAsync();

        return new RotateResult(new TokenPair(accessToken, accessExpires, rawNew, next.ExpiresAt), stored.User);
    }

    public async Task RevokeAsync(string rawRefresh)
    {
        var hash = HashToken(rawRefresh);
        var stored = await _db.RefreshTokens.SingleOrDefaultAsync(r => r.TokenHash == hash);
        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId)
    {
        await _db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, DateTime.UtcNow));
    }

    public static string HashToken(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string GenerateOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
