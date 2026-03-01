using System.Collections.Concurrent;

namespace FileBeam;

/// <summary>
/// In-memory store for immediately banned usernames and IP addresses.
/// Cooperates with credential file hot-reload: removing a user from the credentials file
/// provides persistence across restarts; adding them here provides instant effect this session.
/// All operations are thread-safe. State is cleared on process restart.
/// </summary>
public sealed class RevocationStore
{
    private readonly ConcurrentDictionary<string, bool> _users =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, bool> _ips = new();

    public bool IsUserRevoked(string username) => _users.ContainsKey(username);
    public bool IsIpRevoked(string ip) => _ips.ContainsKey(ip);

    public void RevokeUser(string username)   => _users[username] = true;
    public void UnrevokeUser(string username) => _users.TryRemove(username, out _);

    public void RevokeIp(string ip)   => _ips[ip] = true;
    public void UnrevokeIp(string ip) => _ips.TryRemove(ip, out _);

    public IReadOnlyList<string> RevokedUsers => [.. _users.Keys];
    public IReadOnlyList<string> RevokedIps   => [.. _ips.Keys];
}
