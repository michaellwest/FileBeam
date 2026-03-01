using System.Security.Cryptography;
using System.Text;

namespace FileBeam;

/// <summary>
/// A single user credential entry loaded from the credentials file.
/// </summary>
/// <param name="Password">The stored password (used for constant-time comparison).</param>
/// <param name="Role">
/// The permission role: <c>admin</c>, <c>rw</c>, <c>ro</c>, or <c>wo</c>.
/// Defaults to <c>rw</c> when no role is specified in the file.
/// </param>
public sealed record UserCredential(string Password, string Role);

/// <summary>
/// Loads per-user credentials from a plain-text file.
///
/// File format (one entry per line):
///   username:password
///   username:password:role
///
/// <c>role</c> is optional and must be one of: <c>admin</c>, <c>rw</c>, <c>ro</c>, <c>wo</c>
/// (case-insensitive).  When omitted the user is assigned <c>rw</c>.
///
/// The role is detected by checking whether the last colon-delimited segment of the
/// line is a recognised role keyword.  Passwords that happen to end with :<c>admin</c>,
/// :<c>rw</c>, :<c>ro</c>, or :<c>wo</c> should use an explicit role suffix to avoid
/// ambiguity, or be changed to avoid the conflict.
///
/// Lines that are blank or start with '#' are ignored.
/// Duplicate usernames: last entry wins.
/// </summary>
public static class CredentialStore
{
    /// <summary>Valid role identifiers (case-insensitive).</summary>
    public static readonly IReadOnlySet<string> ValidRoles =
        new HashSet<string>(["admin", "rw", "ro", "wo"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Describes a line that was skipped during parsing.</summary>
    public sealed record ParseWarning(int LineNumber, string LineText, string Reason);

    /// <summary>
    /// Reads credentials from <paramref name="path"/>, discarding parse warnings.
    /// Returns an empty dictionary if the file does not exist or has no valid entries.
    /// </summary>
    public static IReadOnlyDictionary<string, UserCredential> LoadFile(string path) =>
        LoadFileWithDiagnostics(path).Credentials;

    /// <summary>
    /// Reads credentials from <paramref name="path"/> and also returns a list of
    /// skipped-line warnings.  Returns empty collections if the file does not exist.
    /// </summary>
    public static (IReadOnlyDictionary<string, UserCredential> Credentials, IReadOnlyList<ParseWarning> Warnings)
        LoadFileWithDiagnostics(string path)
    {
        var dict     = new Dictionary<string, UserCredential>(StringComparer.Ordinal);
        var warnings = new List<ParseWarning>();

        if (!File.Exists(path))
            return (dict, warnings);

        int lineNum = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNum++;
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // First colon separates username from the rest.
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                warnings.Add(new ParseWarning(lineNum, line, "missing ':'"));
                continue;
            }

            var username = line[..colon];
            var rest     = line[(colon + 1)..];

            if (username.Length == 0)
            {
                warnings.Add(new ParseWarning(lineNum, line, "empty username"));
                continue;
            }

            // Detect optional trailing :role — check whether the last colon-delimited
            // segment is a recognised role keyword.
            string password;
            string role;
            var lastColon = rest.LastIndexOf(':');
            if (lastColon >= 0 && ValidRoles.Contains(rest[(lastColon + 1)..]))
            {
                password = rest[..lastColon];
                role     = rest[(lastColon + 1)..].ToLowerInvariant();
            }
            else
            {
                password = rest;
                role     = "rw";
            }

            if (password.Length == 0)
            {
                warnings.Add(new ParseWarning(lineNum, line, "empty password"));
                continue;
            }

            dict[username] = new UserCredential(password, role);
        }

        return (dict, warnings);
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

/// <summary>
/// Watches a credentials file for changes and hot-reloads it with a 300 ms debounce.
///
/// If the file is missing at construction time, <see cref="Current"/> starts empty and
/// the watcher will load it automatically when the file appears.
/// If the file is deleted at runtime, <see cref="Current"/> reverts to an empty dictionary
/// (all logins rejected) until the file reappears.
/// </summary>
public sealed class CredentialFileWatcher : IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher _fsw;
    private readonly System.Threading.Timer _debounce;
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, UserCredential> _current;
    private bool _disposed;

    /// <summary>
    /// Raised on the thread-pool after credentials are successfully reloaded (or emptied
    /// after deletion).  The argument is the new <see cref="Current"/> snapshot.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, UserCredential>>? Reloaded;

    /// <summary>The most recently loaded credential set (thread-safe).</summary>
    public IReadOnlyDictionary<string, UserCredential> Current
    {
        get { lock (_lock) return _current; }
    }

    public CredentialFileWatcher(string path)
    {
        _path = Path.GetFullPath(path);
        var dir  = Path.GetDirectoryName(_path)!;
        var file = Path.GetFileName(_path);

        _current = CredentialStore.LoadFile(_path);

        _debounce = new System.Threading.Timer(_ => Reload(), null,
            System.Threading.Timeout.Infinite,
            System.Threading.Timeout.Infinite);

        _fsw = new FileSystemWatcher(dir, file)
        {
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents   = true
        };

        _fsw.Created += OnChanged;
        _fsw.Changed += OnChanged;
        _fsw.Deleted += OnChanged;
        _fsw.Renamed += OnChanged;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        _debounce.Change(300, System.Threading.Timeout.Infinite);

    private void Reload()
    {
        IReadOnlyDictionary<string, UserCredential> next;
        try
        {
            next = CredentialStore.LoadFile(_path);
        }
        catch
        {
            next = new Dictionary<string, UserCredential>();
        }

        lock (_lock) _current = next;
        Reloaded?.Invoke(next);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _debounce.Dispose();
    }
}
