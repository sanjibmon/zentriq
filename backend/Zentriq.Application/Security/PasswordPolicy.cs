namespace Zentriq.Application.Security;

public static class PasswordPolicy
{
    public const int MinLength = 12;

    public static string? Validate(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";
        if (password.Length > 256) return "Password is too long.";
        int classes = (password.Any(char.IsUpper) ? 1 : 0) + (password.Any(char.IsLower) ? 1 : 0) +
                      (password.Any(char.IsDigit) ? 1 : 0) + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (classes < 3) return "Password must contain at least three of: upper-case, lower-case, digits, symbols.";
        if (email is not null && email.Contains('@') &&
            password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) && email.Split('@')[0].Length >= 3)
            return "Password must not contain the account name.";
        return null;
    }
}
