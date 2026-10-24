using System.Text.RegularExpressions;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// Checks for the logos uploaded on the Admin Panel's Branding page and on a tenant's Control Center.
/// A logo is kept as a data URL ("data:image/png;base64,....") together with the other settings, so it
/// needs no file storage or hosting and both panels can show it straight from the settings they load.
/// </summary>
public static class BrandingLogo
{
    /// <summary>Largest logo file accepted (bytes) - a header logo never needs more.</summary>
    public const int MaxBytes = 300 * 1024;

    public const int MaxNameLength = 150;

    private static readonly Regex DataUrl = new(
        @"^data:image/(png|jpeg|jpg|gif|webp|svg\+xml);base64,(?<data>[A-Za-z0-9+/=]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Null when the logo is fine (or empty = no logo); otherwise the message for the user.</summary>
    public static string? Error(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        var match = DataUrl.Match(dataUrl.Trim());
        if (!match.Success) return "Logo must be a PNG, JPG, SVG, WEBP or GIF image.";
        try
        {
            var bytes = Convert.FromBase64String(match.Groups["data"].Value);
            if (bytes.Length == 0) return "The logo file is empty.";
            if (bytes.Length > MaxBytes) return $"The logo is {bytes.Length / 1024} KB - the limit is {MaxBytes / 1024} KB. Use a smaller image.";
        }
        catch (FormatException)
        {
            return "The logo file could not be read - choose the image again.";
        }
        return null;
    }

    /// <summary>Trimmed name, or null when empty. Longer than MaxNameLength is an error (see NameError).</summary>
    public static string? CleanName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    public static string? NameError(string? name) =>
        CleanName(name) is { Length: > MaxNameLength } ? $"Name: up to {MaxNameLength} characters." : null;

    /// <summary>Logo as stored: trimmed, or null when empty.</summary>
    public static string? CleanLogo(string? dataUrl) => string.IsNullOrWhiteSpace(dataUrl) ? null : dataUrl.Trim();
}
