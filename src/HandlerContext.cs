using System.Text;
using Microsoft.AspNetCore.Http;

namespace FileBeam;

/// <summary>
/// Shared state and cross-cutting helpers used by all domain handler classes.
/// One instance is created at startup and injected into each handler.
/// </summary>
internal sealed class HandlerContext(
    string rootDir,
    string uploadDir,
    FileWatcher watcher,
    bool isReadOnly = false,
    bool perSender = false,
    long maxFileSize = 0,
    long maxUploadBytesPerSender = 0,
    long maxUploadBytesTotal = 0,
    int maxDirDepth = 10,
    int maxFilesPerDir = 1000,
    string csrfToken = "",
    int shareTtlSeconds = 3600,
    RevocationStore? revocationStore = null,
    InviteStore? inviteStore = null,
    byte[]? sessionKey = null,
    bool isTls = false,
    Action<string, string>? debugLog = null,
    string configJson = "",
    string cliCommand = "",
    string? auditLogPath = null,
    TimeSpan? uploadTtl = null,
    string? adminExemptPath = null,
    SessionRegistry? sessionRegistry = null,
    int maxConcurrentZips = 2,
    long maxZipBytes = 0,
    AutoLoginStore? autoLoginStore = null,
    AuditLogger? auditLogger = null,
    string adminUsername = "admin",
    string adminPassword = "",
    Func<string, bool>? isLockedOut = null,
    Action<string, bool>? recordAuth = null)
{
    internal string        RootDir                  => rootDir;
    internal string        UploadDir                => uploadDir;
    internal FileWatcher   Watcher                  => watcher;
    internal bool          IsReadOnly               => isReadOnly;
    internal bool          PerSender                => perSender;
    internal long          MaxFileSize              => maxFileSize;
    internal long          MaxUploadBytesPerSender  => maxUploadBytesPerSender;
    internal long          MaxUploadBytesTotal      => maxUploadBytesTotal;
    internal int           MaxDirDepth              => maxDirDepth;
    internal int           MaxFilesPerDir           => maxFilesPerDir;
    internal string        CsrfToken                => csrfToken;
    internal int           ShareTtlSeconds          => shareTtlSeconds;
    internal RevocationStore?  RevocationStore      => revocationStore;
    internal InviteStore?      InviteStore          => inviteStore;
    internal byte[]?           SessionKey           => sessionKey;
    internal bool              IsTls                => isTls;
    internal Action<string, string>? DebugLog       => debugLog;
    internal string        ConfigJson               => configJson;
    internal string        CliCommand               => cliCommand;
    internal string?       AuditLogPath             => auditLogPath;
    internal TimeSpan?     UploadTtl                => uploadTtl;
    internal string?       AdminExemptPath          => adminExemptPath;
    internal SessionRegistry? SessionRegistry       => sessionRegistry;

    internal bool HasAuditLog => !string.IsNullOrEmpty(auditLogPath) && auditLogPath != "-";
    internal bool HasSessions => sessionRegistry is not null;

    internal int  MaxConcurrentZips => maxConcurrentZips;
    internal long MaxZipBytes       => maxZipBytes;
    internal AutoLoginStore? AutoLoginStore => autoLoginStore;
    internal AuditLogger?    AuditLogger    => auditLogger;
    internal string          AdminUsername  => adminUsername;
    internal string          AdminPassword  => adminPassword;
    /// <summary>Returns true if the given IP is currently locked out due to brute-force failures.</summary>
    internal Func<string, bool>?      IsLockedOut => isLockedOut;
    /// <summary>Records an auth attempt result (success=true clears failures, false increments).</summary>
    internal Action<string, bool>?    RecordAuth  => recordAuth;

    /// <summary>
    /// Semaphore capping simultaneous ZIP streams. Null when <see cref="MaxConcurrentZips"/> is 0 (unlimited).
    /// </summary>
    internal SemaphoreSlim? ZipSemaphore { get; } =
        maxConcurrentZips > 0 ? new SemaphoreSlim(maxConcurrentZips, maxConcurrentZips) : null;

    // Short-lived cache for GetDirectorySize used by DiskSpace (refreshed at most once per 10 s).
    // Shared between UploadHandlers (quota tracking) and DiskSpace endpoint.
    internal long DirSizeCached;
    internal long DirSizeTicks; // Environment.TickCount64 at last refresh

    // ── Path resolution helpers ────────────────────────────────────────────

    /// <summary>
    /// Resolves a subpath relative to <see cref="RootDir"/>, preventing path traversal and symlink escape.
    /// Returns the root directory when <paramref name="subpath"/> is null or empty.
    /// </summary>
    internal string SafeResolvePath(string? subpath)
    {
        if (string.IsNullOrEmpty(subpath))
            return rootDir;

        var combined = Path.GetFullPath(Path.Combine(rootDir, subpath));

        if (!combined.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal not allowed.");

        if (HasReparsePointInChain(rootDir, combined))
            throw new UnauthorizedAccessException("Symlink traversal not allowed.");

        return combined;
    }

    /// <summary>
    /// Resolves an upload path relative to <paramref name="root"/> (defaults to <see cref="UploadDir"/>).
    /// Path traversal and symlink traversal are always validated against the base <see cref="UploadDir"/>
    /// so that per-sender subfolders cannot escape the drop root.
    /// </summary>
    internal string SafeResolveUploadPath(string? subpath, string? root = null)
    {
        root ??= uploadDir;

        if (string.IsNullOrEmpty(subpath))
            return root;

        var combined = Path.GetFullPath(Path.Combine(root, subpath));

        if (!combined.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal not allowed.");

        if (HasReparsePointInChain(uploadDir, combined))
            throw new UnauthorizedAccessException("Symlink traversal not allowed.");

        return combined;
    }

    /// <summary>
    /// Walks from <paramref name="path"/> up to (but not including) <paramref name="root"/>,
    /// checking every existing path component for being a symlink or directory junction.
    /// Returns true if any reparse point is found.
    /// </summary>
    internal static bool HasReparsePointInChain(string root, string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current) && current.Length > root.Length)
        {
            try
            {
                if (Directory.Exists(current) || File.Exists(current))
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                        return true;
                }
            }
            catch { /* access denied or invalid path — skip */ }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// Returns a folder-safe identifier for the uploading sender.
    /// Prefers the authenticated Basic Auth username; falls back to the remote IP.
    /// </summary>
    internal string ResolveSenderKey(HttpContext ctx)
    {
        // 1. Basic Auth username (stable, explicitly chosen by the user)
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var colon   = decoded.IndexOf(':');
                if (colon > 0) return SanitizeName(decoded[..colon]);
            }
            catch { /* malformed — fall through */ }
        }
        // 2. Cookie session user set by invite-based auth (e.g. "invite:Alice")
        if (ctx.Items.TryGetValue("fb.user", out var u) && u is string sessionUser && !string.IsNullOrEmpty(sessionUser))
            return SanitizeName(sessionUser);
        // 3. Remote IP — last resort (not unique behind NAT / IPv6)
        return SanitizeName(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>
    /// Returns the role attached to this request by the auth middleware.
    /// Defaults to <c>"rw"</c> when no role is present (unauthenticated or shared-password).
    /// </summary>
    internal static string GetRole(HttpContext ctx) =>
        ctx.Items.TryGetValue("fb.role", out var r) && r is string s ? s : "rw";

    /// <summary>
    /// Returns the total byte size of all files under <paramref name="dir"/> recursively.
    /// Inaccessible files are skipped so the count is best-effort.
    /// </summary>
    internal static long GetDirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* skip inaccessible file */ }
            }
        }
        catch { /* directory unreadable */ }
        return total;
    }

    /// <summary>Replaces characters that are invalid in file/folder names with underscores.</summary>
    internal static string SanitizeName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
