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
/// Passwords may contain colons — only the first colon is the delimiter.
/// </summary>
public static class CredentialStore
{
    /// <summary>Describes a line that was skipped during parsing.</summary>
    public sealed record ParseWarning(int LineNumber, string LineText, string Reason);

    /// <summary>
    /// Reads credentials from <paramref name="path"/>, discarding parse warnings.
    /// Returns an empty dictionary if the file does not exist or has no valid entries.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadFile(string path) =>
        LoadFileWithDiagnostics(path).Credentials;

    /// <summary>
    /// Reads credentials from <paramref name="path"/> and also returns a list of
    /// skipped-line warnings.  Returns empty collections if the file does not exist.
    /// </summary>
    public static (IReadOnlyDictionary<string, string> Credentials, IReadOnlyList<ParseWarning> Warnings)
        LoadFileWithDiagnostics(string path)
    {
        var dict     = new Dictionary<string, string>(StringComparer.Ordinal);
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

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                warnings.Add(new ParseWarning(lineNum, line, "missing ':'"));
                continue;
            }

            var username = line[..colon];
            var password = line[(colon + 1)..];

            if (username.Length == 0)
            {
                warnings.Add(new ParseWarning(lineNum, line, "empty username"));
                continue;
            }

            if (password.Length == 0)
            {
                warnings.Add(new ParseWarning(lineNum, line, "empty password"));
                continue;
            }

            dict[username] = password;
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
    private IReadOnlyDictionary<string, string> _current;
    private bool _disposed;

    /// <summary>
    /// Raised on the thread-pool after credentials are successfully reloaded (or emptied
    /// after deletion).  The argument is the new <see cref="Current"/> snapshot.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, string>>? Reloaded;

    /// <summary>The most recently loaded credential set (thread-safe).</summary>
    public IReadOnlyDictionary<string, string> Current
    {
        get { lock (_lock) return _current; }
    }

    public CredentialFileWatcher(string path)
    {
        _path = Path.GetFullPath(path);
        var dir  = Path.GetDirectoryName(_path)!;
        var file = Path.GetFileName(_path);

        // Load existing file (may be empty if file doesn't exist yet)
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
        // Reset the debounce window on every event
        _debounce.Change(300, System.Threading.Timeout.Infinite);

    private void Reload()
    {
        IReadOnlyDictionary<string, string> next;
        try
        {
            next = CredentialStore.LoadFile(_path);
        }
        catch
        {
            // File transiently locked or deleted mid-read — treat as empty
            next = new Dictionary<string, string>();
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
