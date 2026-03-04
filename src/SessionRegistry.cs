using System.Collections.Concurrent;

namespace FileBeam;

/// <summary>
/// Represents a single active invite-based session entry.
/// </summary>
public sealed record SessionInfo(
    string          InviteId,
    string          InviteName,
    string          Role,
    string          Ip,
    string          AuthMethod,
    DateTimeOffset  LastSeen);

/// <summary>
/// Thread-safe in-memory registry of active invite-based sessions.
/// Sessions are keyed by "{inviteId}|{ip}|{authMethod}" and updated on every
/// successful Bearer or cookie authentication.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Upserts a session entry, refreshing <see cref="SessionInfo.LastSeen"/> if it already exists.
    /// </summary>
    public void Touch(string inviteId, string inviteName, string role, string ip, string authMethod)
    {
        var key  = $"{inviteId}|{ip}|{authMethod}";
        var info = new SessionInfo(inviteId, inviteName, role, ip, authMethod, DateTimeOffset.UtcNow);
        _sessions[key] = info;
    }

    /// <summary>
    /// Returns all sessions that are still considered active:
    /// <list type="bullet">
    ///   <item><description>Last seen within the past 30 minutes.</description></item>
    ///   <item><description>Underlying invite token is still active and not expired.</description></item>
    /// </list>
    /// Results are ordered newest-first by <see cref="SessionInfo.LastSeen"/>.
    /// </summary>
    public IReadOnlyList<SessionInfo> GetActive(InviteStore inviteStore)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        var now    = DateTimeOffset.UtcNow;

        var result = new List<SessionInfo>();
        foreach (var (key, session) in _sessions)
        {
            // Prune stale sessions
            if (session.LastSeen < cutoff)
            {
                _sessions.TryRemove(key, out _);
                continue;
            }

            // Prune sessions whose invite has been revoked or expired
            if (!inviteStore.TryGet(session.InviteId, out var invite) ||
                !invite!.IsActive ||
                (invite.ExpiresAt.HasValue && invite.ExpiresAt.Value < now))
            {
                _sessions.TryRemove(key, out _);
                continue;
            }

            result.Add(session);
        }

        result.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
        return result;
    }

    /// <summary>
    /// Removes all sessions associated with the given invite ID.
    /// Called after an invite is revoked.
    /// </summary>
    public void RemoveByInvite(string inviteId)
    {
        foreach (var key in _sessions.Keys.Where(k => k.StartsWith(inviteId + "|", StringComparison.Ordinal)).ToList())
            _sessions.TryRemove(key, out _);
    }
}
