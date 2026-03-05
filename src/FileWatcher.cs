using System.Threading.Channels;

namespace FileBeam;

/// <summary>
/// Broadcasts a reload signal to all connected SSE clients whenever the
/// watched directory tree changes (create, delete, rename).
/// </summary>
public sealed class FileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly List<Channel<string>> _clients = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _sseLimit;

    public FileWatcher(string rootDir, int maxSseConnections = 50)
    {
        _sseLimit = new SemaphoreSlim(maxSseConnections, maxSseConnections);

        _watcher = new FileSystemWatcher(rootDir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents   = true,
            NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    private void OnChanged(object _, FileSystemEventArgs e)
    {
        // Ignore events for .part temp files (created/deleted during uploads).
        // A rename FROM .part TO the final name has FullPath = final name, so it passes through.
        if (e.FullPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            foreach (var ch in _clients)
                ch.Writer.TryWrite("reload");
        }
    }

    /// <summary>
    /// Subscribe a new SSE client. Returns null if the connection cap has been reached.
    /// </summary>
    public Channel<string>? TrySubscribe()
    {
        if (!_sseLimit.Wait(0))
            return null;

        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (_lock) _clients.Add(ch);
        return ch;
    }

    /// <summary>Unsubscribe when the SSE connection closes.</summary>
    public void Unsubscribe(Channel<string> ch)
    {
        lock (_lock) _clients.Remove(ch);
        ch.Writer.TryComplete();
        _sseLimit.Release();
    }

    public void Dispose() => _watcher.Dispose();
}
