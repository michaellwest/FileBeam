using System.Security.Cryptography;
using System.Text;

namespace FileBeam;

/// <summary>
/// Loads per-user credentials from a plain-text file.
///
/// File format (one entry per line):
///   username:password
///
/// Lines that are blank or start with '#' are ignored.
/// Duplicate usernames: last entry wins.
/// </summary>
public static class CredentialStore
{
    /// <summary>
    /// Reads credentials from <paramref name="path"/>.
    /// Returns an empty dictionary if the file does not exist or has no valid entries.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
            return dict;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)          // no colon, or colon at position 0 (empty username)
                continue;

            var username = line[..colon];
            var password = line[(colon + 1)..];

            if (username.Length == 0 || password.Length == 0)
                continue;

            dict[username] = password;
        }

        return dict;
    }

    /// <summary>
    /// Constant-time check: does <paramref name="submitted"/> match
    /// <paramref name="expected"/>?  Uses <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// to prevent timing-based username/password enumeration.
    /// </summary>
    public static bool VerifyPassword(string submitted, string expected)
    {
        var a = Encoding.UTF8.GetBytes(submitted);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
