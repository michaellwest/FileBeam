using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace FileBeam;

public sealed record AutoLoginToken(string Token, DateTimeOffset ExpiresAt, bool Used);

/// <summary>Manages the single active admin auto-login token embedded in the startup QR code.</summary>
public sealed class AutoLoginStore
{
    private AutoLoginToken? _token;
    private readonly object _lock = new();

    // Short-lived single-use continuation tokens: bridging the QR redirect to the landing page.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _contTokens = new();

    // Long-lived, multi-use session bearer tokens issued after QR auto-login.
    // Used by JavaScript-initiated requests (fetch, XHR, EventSource) in environments
    // where cookies are not reliably persisted (e.g. mobile QR-scanner webviews).
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionBearers = new();

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

    /// <summary>
    /// Creates a short-lived (default 60 s), single-use continuation token.
    /// The token is embedded in the redirect URL after the QR auto-login token is validated,
    /// so the session cookie can be set by the auth middleware on the landing page request
    /// rather than on an intermediate redirect response. This avoids mobile browsers
    /// and QR-scanner webviews that don't forward <c>Set-Cookie</c> across redirects.
    /// </summary>
    public string GenerateContinuationToken(int ttlSeconds = 60)
    {
        // Evict any expired entries to keep the dictionary small.
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _contTokens.Keys.ToList())
            if (_contTokens.TryGetValue(key, out var exp) && exp <= now)
                _contTokens.TryRemove(key, out _);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _contTokens[token] = now.AddSeconds(ttlSeconds);
        return token;
    }

    /// <summary>Validates and atomically burns a continuation token. Returns true on success.</summary>
    public bool TryRedeemContinuation(string token)
    {
        if (!_contTokens.TryRemove(token, out var expiry))
            return false;
        return expiry > DateTimeOffset.UtcNow;
    }

    // ── Session bearer tokens ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a long-lived (default 24 h) reusable Bearer token for admin access.
    /// Unlike continuation tokens, session bearers are <em>not</em> burned on use; they remain
    /// valid until they expire or the process restarts. They are intended for JavaScript-initiated
    /// sub-requests (EventSource, fetch, XHR) when the session cookie cannot be persisted by the
    /// browser (e.g. mobile QR-scanner webviews).
    /// </summary>
    public string GenerateSessionBearer(int ttlHours = 24)
    {
        // Evict any expired entries to keep the dictionary small.
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _sessionBearers.Keys.ToList())
            if (_sessionBearers.TryGetValue(key, out var exp) && exp <= now)
                _sessionBearers.TryRemove(key, out _);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _sessionBearers[token] = now.AddHours(ttlHours);
        return token;
    }

    /// <summary>
    /// Validates a session Bearer token. Returns <c>true</c> if valid and unexpired.
    /// Does <em>not</em> burn the token — the same token may be reused for multiple requests.
    /// </summary>
    public bool TryValidateSessionBearer(string token) =>
        _sessionBearers.TryGetValue(token, out var expiry) && expiry > DateTimeOffset.UtcNow;
}
