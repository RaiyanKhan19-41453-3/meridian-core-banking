using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CoreBanking.Api.Data;
using CoreBanking.Api.Dtos;
using CoreBanking.Api.Entities;
using CoreBanking.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private const string DummyHash = "$2a$11$C6UzMDM.H6dfI/f/IKcEeO7ZBZkJy0qkESKfQzbfXtq0eT1kMJxLu";

    private readonly AppDbContext _db;
    private readonly TokenService _tokens;
    private readonly AuditService _audit;
    private readonly AmlService _aml;

    public AuthController(AppDbContext db, TokenService tokens, AuditService audit, AmlService aml)
    {
        _db = db;
        _tokens = tokens;
        _audit = audit;
        _aml = aml;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string? UserAgent => Request.Headers.UserAgent.ToString();

    private TokenPairResponse ToResponse(TokenPair pair, User user) =>
        new(pair.AccessToken, pair.AccessTokenExpiresAtUtc,
            pair.RefreshToken, pair.RefreshTokenExpiresAtUtc,
            user.Email, user.FullName, user.Role.ToString());

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        PasswordPolicy.Validate(req.Password);

        var email = req.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email))
            throw BankException.Conflict("email_taken", "An account with this email already exists.");

        var user = new User
        {
            Email = email,
            FullName = req.FullName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Role = UserRole.Customer
        };
        _db.Users.Add(user);
        // Every registration starts an empty KYC dossier (Pending) and is watchlist-screened
        var profile = await KycProfileFactory.CreatePendingAsync(_db, user);
        _audit.Write(null, "auth.register", nameof(User), user.Id.ToString(), new { user.Email }, ClientIp);

        var match = await _aml.ScreenWatchlistAsync(user.FullName);
        if (match is not null)
        {
            profile.RiskRating = RiskRating.High;
            await _aml.FlagWatchlistMatchAsync(user.Id, user.FullName, match, ClientIp);
            _audit.Write(null, "kyc.watchlist_match", nameof(CustomerProfile), profile.Id.ToString(),
                new { match.FullName, match.Source }, ClientIp);
        }
        await _db.SaveChangesAsync();

        var pair = await _tokens.IssuePairAsync(user, ClientIp, UserAgent);
        return Ok(ToResponse(pair, user));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.SingleOrDefaultAsync(u => u.Email == email);

        // Lockout check first — even a correct password is rejected during lockout.
        if (user?.LockoutEnd is not null && user.LockoutEnd > DateTime.UtcNow)
        {
            _audit.Write(user.Id, "auth.login_locked", nameof(User), user.Id.ToString(), null, ClientIp);
            await _db.SaveChangesAsync();
            throw new BankException("account_locked",
                $"Account is locked due to failed sign-in attempts. Try again after {user.LockoutEnd:HH:mm} UTC.", 423);
        }

        var hash = user?.PasswordHash ?? DummyHash;
        var passwordOk = BCrypt.Net.BCrypt.Verify(req.Password, hash);

        if (user is null || !passwordOk)
        {
            if (user is not null)
            {
                user.FailedLoginAttempts += 1;
                var lockedOut = false;
                if (user.FailedLoginAttempts >= MaxFailedAttempts)
                {
                    user.LockoutEnd = DateTime.UtcNow.Add(LockoutDuration);
                    user.FailedLoginAttempts = 0;
                    lockedOut = true;
                }
                _audit.Write(user.Id, lockedOut ? "auth.lockout" : "auth.login_failed",
                    nameof(User), user.Id.ToString(), null, ClientIp);
                await _db.SaveChangesAsync();
            }
            throw BankException.Unauthorized("invalid_credentials", "Invalid email or password.");
        }

        // Password OK — TOTP required when enabled.
        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(req.TotpCode))
                throw BankException.Unprocessable("totp_required", "Two-factor authentication code is required.");
            if (!TotpService.Validate(user.TwoFactorSecret!, req.TotpCode))
            {
                user.FailedLoginAttempts += 1;
                if (user.FailedLoginAttempts >= MaxFailedAttempts)
                {
                    user.LockoutEnd = DateTime.UtcNow.Add(LockoutDuration);
                    user.FailedLoginAttempts = 0;
                }
                _audit.Write(user.Id, "auth.totp_failed", nameof(User), user.Id.ToString(), null, ClientIp);
                await _db.SaveChangesAsync();
                throw BankException.Unprocessable("invalid_totp", "Two-factor authentication code is invalid.");
            }
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        var pair = await _tokens.IssuePairAsync(user, ClientIp, UserAgent);
        _audit.Write(user.Id, "auth.login", nameof(User), user.Id.ToString(), null, ClientIp);
        await _db.SaveChangesAsync();

        return Ok(ToResponse(pair, user));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req)
    {
        var result = await _tokens.RotateAsync(req.RefreshToken, ClientIp, UserAgent);
        return Ok(ToResponse(result.Pair, result.User));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest req)
    {
        await _tokens.RevokeAsync(req.RefreshToken);
        return NoContent();
    }

    [Authorize]
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll()
    {
        var user = await LoadCurrentUserAsync(asNoTracking: false);
        if (user is null) return Unauthorized();
        await _tokens.RevokeAllForUserAsync(user.Id);
        _audit.Write(user.Id, "auth.logout_all", nameof(User), user.Id.ToString(), null, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await LoadCurrentUserAsync(asNoTracking: true);
        if (user is null) return Unauthorized();
        var kycStatus = await _db.CustomerProfiles.AsNoTracking()
            .Where(p => p.UserId == user.Id)
            .Select(p => p.KycStatus.ToString())
            .SingleOrDefaultAsync() ?? KycStatus.Pending.ToString();
        return Ok(new MeResponse(user.Id, user.Email, user.FullName, user.Role.ToString(), user.TwoFactorEnabled, kycStatus, user.CreatedAt));
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var user = await LoadCurrentUserAsync(asNoTracking: false);
        if (user is null) return Unauthorized();

        if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword, user.PasswordHash))
            throw BankException.Unprocessable("wrong_current_password", "Current password is incorrect.");

        PasswordPolicy.Validate(req.NewPassword);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await _tokens.RevokeAllForUserAsync(user.Id); // force re-login everywhere
        _audit.Write(user.Id, "auth.password_changed", nameof(User), user.Id.ToString(), null, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- TOTP 2FA ----------

    [Authorize]
    [HttpPost("2fa/setup")]
    public async Task<IActionResult> SetupTwoFactor()
    {
        var user = await LoadCurrentUserAsync(asNoTracking: false);
        if (user is null) return Unauthorized();
        if (user.TwoFactorEnabled)
            throw BankException.Conflict("totp_already_enabled", "Two-factor authentication is already enabled.");

        var secret = TotpService.GenerateSecretBase32();
        user.TwoFactorSecret = secret;
        user.TwoFactorEnabled = false; // pending confirmation
        _audit.Write(user.Id, "auth.totp_setup", nameof(User), user.Id.ToString(), null, ClientIp);
        await _db.SaveChangesAsync();

        return Ok(new TotpSetupResponse(secret, TotpService.BuildOtpAuthUri(user.Email, secret)));
    }

    [Authorize]
    [HttpPost("2fa/enable")]
    public async Task<IActionResult> EnableTwoFactor([FromBody] TotpCodeRequest req)
    {
        var user = await LoadCurrentUserAsync(asNoTracking: false);
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret))
            throw BankException.BadRequest("totp_not_setup", "Call 2fa/setup first.");
        if (!TotpService.Validate(user.TwoFactorSecret, req.Code))
            throw BankException.Unprocessable("invalid_totp", "Code is invalid or expired.");

        user.TwoFactorEnabled = true;
        _audit.Write(user.Id, "auth.totp_enabled", nameof(User), user.Id.ToString(), null, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize]
    [HttpPost("2fa/disable")]
    public async Task<IActionResult> DisableTwoFactor([FromBody] TotpCodeRequest req)
    {
        var user = await LoadCurrentUserAsync(asNoTracking: false);
        if (user is null) return Unauthorized();
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TwoFactorSecret))
            throw BankException.BadRequest("totp_not_enabled", "Two-factor authentication is not enabled.");
        if (!TotpService.Validate(user.TwoFactorSecret, req.Code))
            throw BankException.Unprocessable("invalid_totp", "Code is invalid or expired.");

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        await _tokens.RevokeAllForUserAsync(user.Id); // security downgrade — force re-login
        _audit.Write(user.Id, "auth.totp_disabled", nameof(User), user.Id.ToString(), null, ClientIp);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---------- helpers ----------

    private async Task<User?> LoadCurrentUserAsync(bool asNoTracking)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sub is null || !Guid.TryParse(sub, out var id)) return null;
        var query = _db.Users.AsQueryable();
        if (asNoTracking) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(u => u.Id == id);
    }
}
