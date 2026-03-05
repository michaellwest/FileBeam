using System.Security.Cryptography;
using System.Text;

namespace FileBeam;

public sealed record AutoLoginToken(string Token, DateTimeOffset ExpiresAt, bool Used);

/// <summary>Manages the single active admin auto-login token embedded in the startup QR code.</summary>
public sealed class AutoLoginStore
{
    private AutoLoginToken? _token;
    private readonly object _lock = new();

    /// <summary>Generates a fresh token (invalidates any previous unused token).</summary>
    public AutoLoginToken Generate()
    {
        var raw   = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var token = new AutoLoginToken(raw, DateTimeOffset.UtcNow.AddMinutes(5), Used: false);
        lock (_lock) { _token = token; }
        return token;
    }

    /// <summary>Validates and atomically burns the token. Returns true on success.</summary>
    public bool TryRedeem(string provided)
    {
        lock (_lock)
        {
            if (_token is null || _token.Used || _token.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(_token.Token),
                    Encoding.UTF8.GetBytes(provided)))
                return false;
            _token = _token with { Used = true };
            return true;
        }
    }

    /// <summary>Returns the current unexpired unused token, or null.</summary>
    public AutoLoginToken? GetActive()
    {
        lock (_lock)
        {
            return _token is { Used: false } t && t.ExpiresAt > DateTimeOffset.UtcNow ? t : null;
        }
    }
}
