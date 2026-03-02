using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileBeam;

/// <summary>
/// Represents an invite token that grants access to FileBeam with a specific role.
/// </summary>
public sealed record InviteToken(
    string          Id,
    string          FriendlyName,
    string          Role,
    DateTimeOffset? ExpiresAt,
    int             UseCount,
    string          CreatedBy,
    bool            IsActive,
    DateTimeOffset  CreatedAt,
    int?            JoinMaxUses    = null,
    int?            BearerMaxUses  = null,
    int             BearerUseCount = 0);

/// <summary>
/// In-memory invite token store with optional JSON file persistence.
/// All operations are thread-safe.
/// </summary>
public sealed class InviteStore
{
    private readonly ConcurrentDictionary<string, InviteToken> _tokens =
        new(StringComparer.Ordinal);

    private readonly string? _filePath;
    private readonly Lock    _saveLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented         = true,
        PropertyNamingPolicy  = JsonNamingPolicy.CamelCase,
    };

    public InviteStore(string? filePath = null)
    {
        _filePath = filePath;
        if (filePath is not null && File.Exists(filePath))
            TryLoad(filePath);
    }

    /// <summary>
    /// Creates a new invite token and persists it if a file path is configured.
    /// </summary>
    public InviteToken Create(string friendlyName, string role, DateTimeOffset? expiresAt, string createdBy,
        int? joinMaxUses = null, int? bearerMaxUses = null)
    {
        var token = new InviteToken(
            Id:             GenerateId(),
            FriendlyName:   friendlyName,
            Role:           role,
            ExpiresAt:      expiresAt,
            UseCount:       0,
            CreatedBy:      createdBy,
            IsActive:       true,
            CreatedAt:      DateTimeOffset.UtcNow,
            JoinMaxUses:    joinMaxUses,
            BearerMaxUses:  bearerMaxUses,
            BearerUseCount: 0);

        _tokens[token.Id] = token;
        SaveIfConfigured();
        return token;
    }

    /// <summary>Returns all invite tokens, newest first.</summary>
    public IReadOnlyList<InviteToken> GetAll() =>
        [.. _tokens.Values.OrderByDescending(t => t.CreatedAt)];

    /// <summary>Looks up a token by ID.</summary>
    public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InviteToken? token) =>
        _tokens.TryGetValue(id, out token);

    /// <summary>
    /// Marks a token inactive. Returns false if the token does not exist.
    /// </summary>
    public bool Revoke(string id)
    {
        if (!_tokens.TryGetValue(id, out var t)) return false;
        _tokens[id] = t with { IsActive = false };
        SaveIfConfigured();
        return true;
    }

    /// <summary>
    /// Updates a token's friendly name, role, expiry, and/or use caps.
    /// Pass <c>clearExpiry = true</c> to remove the expiry entirely.
    /// Pass <c>clearJoinMaxUses = true</c> or <c>clearBearerMaxUses = true</c> to remove a cap.
    /// Returns false if the token does not exist.
    /// </summary>
    public bool Edit(string id, string? friendlyName, string? role, DateTimeOffset? expiresAt,
        bool clearExpiry = false, int? joinMaxUses = null, int? bearerMaxUses = null,
        bool clearJoinMaxUses = false, bool clearBearerMaxUses = false)
    {
        if (!_tokens.TryGetValue(id, out var t)) return false;
        _tokens[id] = t with
        {
            FriendlyName  = friendlyName       ?? t.FriendlyName,
            Role          = role               ?? t.Role,
            ExpiresAt     = clearExpiry        ? null : (expiresAt    ?? t.ExpiresAt),
            JoinMaxUses   = clearJoinMaxUses   ? null : (joinMaxUses   ?? t.JoinMaxUses),
            BearerMaxUses = clearBearerMaxUses ? null : (bearerMaxUses ?? t.BearerMaxUses),
        };
        SaveIfConfigured();
        return true;
    }

    /// <summary>
    /// Validates that the token is active and not expired, atomically increments
    /// <see cref="InviteToken.UseCount"/>, and returns the updated token.
    /// Returns <c>null</c> if the token is missing, inactive, expired, or has reached its join cap.
    /// The token remains active after the cap is reached so that sessions already issued stay valid;
    /// further redemptions are blocked by the cap check.
    /// </summary>
    public InviteToken? TryRedeem(string id)
    {
        if (!_tokens.TryGetValue(id, out var t)) return null;
        if (!t.IsActive) return null;
        if (t.ExpiresAt.HasValue && t.ExpiresAt.Value < DateTimeOffset.UtcNow) return null;
        if (t.JoinMaxUses.HasValue && t.UseCount >= t.JoinMaxUses.Value) return null;

        // Atomic compare-and-swap increment
        InviteToken current, updated;
        do
        {
            current = _tokens[id];
            updated = current with { UseCount = current.UseCount + 1 };
        }
        while (!_tokens.TryUpdate(id, updated, current));

        SaveIfConfigured();
        return updated;
    }

    /// <summary>
    /// Validates the token for Bearer API access, atomically increments
    /// <see cref="InviteToken.BearerUseCount"/>, and returns the updated token.
    /// Returns <c>null</c> if the token is missing, inactive, expired, or has reached its Bearer cap.
    /// Does NOT auto-deactivate — the join link remains unaffected when the Bearer cap is reached.
    /// </summary>
    public InviteToken? TryBearerAuthenticate(string id)
    {
        if (!_tokens.TryGetValue(id, out var t)) return null;
        if (!t.IsActive) return null;
        if (t.ExpiresAt.HasValue && t.ExpiresAt.Value < DateTimeOffset.UtcNow) return null;
        if (t.BearerMaxUses.HasValue && t.BearerUseCount >= t.BearerMaxUses.Value) return null;

        // Atomic compare-and-swap increment; no auto-deactivate for Bearer cap
        InviteToken current, updated;
        do
        {
            current = _tokens[id];
            updated = current with { BearerUseCount = current.BearerUseCount + 1 };
        }
        while (!_tokens.TryUpdate(id, updated, current));

        SaveIfConfigured();
        return updated;
    }

    // ── 12-char URL-safe Base64 ID ─────────────────────────────────────────────
    private static string GenerateId()
    {
        // 9 random bytes → 12 Base64 chars (no padding)
        var bytes = RandomNumberGenerator.GetBytes(9);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    // ── JSON persistence ───────────────────────────────────────────────────────
    private void SaveIfConfigured()
    {
        if (_filePath is null) return;
        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_tokens.Values.ToArray(), JsonOpts);
                File.WriteAllText(_filePath, json);
            }
            catch { /* best-effort */ }
        }
    }

    private void TryLoad(string path)
    {
        try
        {
            var json   = File.ReadAllText(path);
            var tokens = JsonSerializer.Deserialize<InviteToken[]>(json, JsonOpts);
            if (tokens is not null)
                foreach (var t in tokens)
                    _tokens[t.Id] = t;
        }
        catch { /* corrupt or missing — start empty */ }
    }
}
