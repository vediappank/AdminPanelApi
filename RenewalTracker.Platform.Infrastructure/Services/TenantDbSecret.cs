using System.Security.Cryptography;
using System.Text;

namespace RenewalTracker.Platform.Infrastructure.Services;

/// <summary>
/// Encrypts the password of a tenant database's own SQL user (Manual mode: the user Plesk creates with the
/// database) before it is stored in SuperAdminPanel.dbo.TenantDatabases.DbPassword.
///
/// AES-256-GCM. Stored text: "v1:" + Base64(nonce[12] | tag[16] | cipher). The key is SHA-256 of the configured
/// text - TenantDatabase:CredentialKey here and MultiTenant:CredentialKey in the Business Panel API must be the
/// same value, because the Business Panel decrypts the password to open the tenant database.
/// The Business Panel API has an identical copy of this class (Tenancy/TenantDbSecret.cs).
/// </summary>
public static class TenantDbSecret
{
    private const string Version = "v1:";
    private const int NonceSize = 12, TagSize = 16;

    public static string Protect(string plainText, string key)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(DeriveKey(key), TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);
        return Version + Convert.ToBase64String(packed);
    }

    /// <summary>Throws CryptographicException when the text was encrypted with another key or is damaged.</summary>
    public static string Unprotect(string protectedText, string key)
    {
        if (!protectedText.StartsWith(Version, StringComparison.Ordinal))
            throw new CryptographicException("Unknown password format.");
        var packed = Convert.FromBase64String(protectedText[Version.Length..]);
        if (packed.Length < NonceSize + TagSize) throw new CryptographicException("Password value is damaged.");

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipher = packed.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(DeriveKey(key), TagSize))
            aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
