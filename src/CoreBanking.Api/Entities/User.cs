namespace CoreBanking.Api.Entities;

public enum UserRole
{
    Customer,
    Admin
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Customer;

    // --- TOTP 2FA ---
    public string? TwoFactorSecret { get; set; }
    public bool TwoFactorEnabled { get; set; }

    // --- Brute-force protection ---
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Account> Accounts { get; set; } = new List<Account>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
