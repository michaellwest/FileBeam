using System.Security.Cryptography;
using System.Text;

namespace FileBeam;

/// <summary>
/// Helpers for admin Basic Auth and Bearer token authentication.
/// Extracted for testability — used by the auth middleware in Program.cs.
/// </summary>
internal static class AdminAuth
{
    /// <summary>
    /// Generates a cryptographically random 16-character alphanumeric password.
    /// </summary>
    internal static string GeneratePassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    /// <summary>
    /// Resolves the admin password using the following priority:
    /// <list type="number">
    ///   <item><paramref name="envVar"/> — FILEBEAM_ADMIN_PASSWORD environment variable</item>
    ///   <item><paramref name="cliFlag"/> — --admin-password CLI flag</item>
    ///   <item><paramref name="keyFilePath"/> — read from file if it exists</item>
    ///   <item>Auto-generate and write to <paramref name="keyFilePath"/></item>
    /// </list>
    /// </summary>
    /// <param name="envVar">Value of FILEBEAM_ADMIN_PASSWORD env var, or null if unset.</param>
    /// <param name="cliFlag">Value of --admin-password CLI flag, or null if not provided.</param>
    /// <param name="keyFilePath">Absolute path to filebeam-admin.key (for read/write).</param>
    /// <param name="generated">True if a new password was auto-generated and written to the key file.</param>
    internal static string ResolveAdminPassword(
        string? envVar,
        string? cliFlag,
        string  keyFilePath,
        out bool generated)
    {
        generated = false;

        if (!string.IsNullOrEmpty(envVar))
            return envVar;

        if (!string.IsNullOrEmpty(cliFlag))
            return cliFlag;

        if (File.Exists(keyFilePath))
        {
            try
            {
                var stored = File.ReadAllText(keyFilePath).Trim();
                if (!string.IsNullOrEmpty(stored))
                    return stored;
            }
            catch { /* fall through to generate */ }
        }

        generated = true;
        var password = GeneratePassword();
        File.WriteAllText(keyFilePath, password);
        return password;
    }

    /// <summary>
    /// Tries to authenticate an admin via HTTP Basic Auth.
    /// Uses constant-time comparison to prevent timing-based enumeration.
    /// Returns true and sets <paramref name="user"/> to the admin username on success.
    /// </summary>
    internal static bool TryAdminBasicAuth(
        string  authHeader,
        string  adminUsername,
        string  adminPassword,
        out string? user)
    {
        user = null;
        if (!authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader[6..]));
            var colon   = decoded.IndexOf(':');
            if (colon < 0) return false;

            var submittedUser = decoded[..colon];
            var submittedPass = decoded[(colon + 1)..];

            if (submittedUser != adminUsername) return false;

            var a = Encoding.UTF8.GetBytes(submittedPass);
            var b = Encoding.UTF8.GetBytes(adminPassword);
            if (!CryptographicOperations.FixedTimeEquals(a, b)) return false;

            user = submittedUser;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Tries to authenticate via HTTP Bearer token.
    /// Delegates to <see cref="InviteStore.TryBearerAuthenticate"/> which validates activity,
    /// expiry, and the Bearer cap, then atomically increments <see cref="InviteToken.BearerUseCount"/>.
    /// The join <see cref="InviteToken.UseCount"/> is NOT affected.
    /// Returns true and sets role/user on success.
    /// </summary>
    internal static bool TryBearerAuth(
        string      authHeader,
        InviteStore inviteStore,
        out string? role,
        out string? user)
    {
        role = null;
        user = null;

        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var tokenId = authHeader[7..].Trim();
        if (string.IsNullOrEmpty(tokenId)) return false;

        var invite = inviteStore.TryBearerAuthenticate(tokenId);
        if (invite is null) return false;

        role = invite.Role;
        user = $"invite:{invite.FriendlyName}";
        return true;
    }
}
