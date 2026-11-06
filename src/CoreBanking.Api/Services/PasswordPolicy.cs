namespace CoreBanking.Api.Services;

public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 100;

    /// <summary>Throws BankException(weak_password) listing every unmet rule.</summary>
    public static void Validate(string? password)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            problems.Add("password is required");
        }
        else
        {
            if (password.Length < MinLength) problems.Add($"at least {MinLength} characters");
            if (password.Length > MaxLength) problems.Add($"at most {MaxLength} characters");
            if (!password.Any(char.IsDigit)) problems.Add("at least one digit");
            if (!password.Any(char.IsLetter)) problems.Add("at least one letter");
        }

        if (problems.Count > 0)
            throw BankException.BadRequest("weak_password", "Password must contain " + string.Join(", ", problems) + ".");
    }
}
