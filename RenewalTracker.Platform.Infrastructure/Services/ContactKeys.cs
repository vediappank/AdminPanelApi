namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// The comparable form of an e-mail address or mobile number, so the same contact written differently
/// is still recognised as a duplicate:
///   e-mail  " Ravi@Gmail.com "                       -> "ravi@gmail.com"
///   mobile  "+971 58 997 0732" / "00971589970732" /
///           "058-997-0732" / "589970732"             -> "589970732"
///           "+91 98765 43210" / "09876543210"        -> "9876543210"
/// Mobile: digits only, leading zeros (00 prefix, trunk 0) dropped, then a known country code dropped
/// when the number is long enough to carry one. The same rules are used in the Business Panel API (Common/ContactKeys.cs).
/// </summary>
public static class ContactKeys
{
    // Longest first, so 971 wins over 9. Gulf + India + UK + US/Canada.
    private static readonly string[] CountryCodes = { "971", "966", "974", "968", "965", "973", "91", "44", "1" };

    public static string? Email(string? value)
    {
        var e = (value ?? string.Empty).Trim().ToLowerInvariant();
        return e.Length == 0 ? null : e;
    }

    public static string? Mobile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray()).TrimStart('0');
        if (digits.Length >= 11)
        {
            foreach (var cc in CountryCodes)
            {
                if (digits.StartsWith(cc, StringComparison.Ordinal) && digits.Length - cc.Length >= 8)
                {
                    digits = digits[cc.Length..].TrimStart('0');
                    break;
                }
            }
        }
        return digits.Length >= 6 ? digits : null;
    }

    /// <summary>Last 7 digits - a cheap SQL pre-filter (LIKE '%' + tail); the full key is compared in C#.</summary>
    public static string Tail(string mobileKey) => mobileKey.Length > 7 ? mobileKey[^7..] : mobileKey;

    /// <summary>SQL expression: the column with spaces, dashes, plus, brackets and dots removed.</summary>
    public static string DigitsSql(string column) =>
        $"REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE({column}, N' ', N''), N'-', N''), N'+', N''), N'(', N''), N')', N''), N'.', N'')";
}
