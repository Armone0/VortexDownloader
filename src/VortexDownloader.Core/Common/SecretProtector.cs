using System.Security.Cryptography;
using System.Text;

namespace VortexDownloader.Core.Common;

/// <summary>
/// Encrypts small secrets (passwords) for the local JSON files with Windows DPAPI, bound to the current user.
/// Elsewhere it stores nothing: a secret is never written in plain text.
/// </summary>
public static class SecretProtector
{
    // App-specific entropy: another program running as the same user cannot unprotect the blob without it.
    private static readonly byte[] Entropy = "VortexDownloader.Secret.v1"u8.ToArray();

    /// <summary>Base64 DPAPI blob, or null for an empty secret or on a non-Windows OS.</summary>
    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || !OperatingSystem.IsWindows()) return null;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>The secret, or null when the blob is missing, corrupt, or from another user/machine.</summary>
    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob) || !OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
