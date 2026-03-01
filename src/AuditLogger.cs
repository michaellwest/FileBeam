using System.Text.Json;
using System.Threading.Channels;

namespace FileBeam;

/// <summary>
/// Non-blocking, newline-delimited JSON audit logger for file transfer events.
/// Writes are queued to an unbounded channel and drained by a single background task
/// so they never delay HTTP responses.
/// </summary>
public sealed class AuditLogger : IAsyncDisposable
{
    private readonly string? _path;
    private readonly long    _maxSize;
    private readonly Channel<string> _channel;
    private readonly Task            _worker;
    private readonly CancellationTokenSource _cts = new();

    public AuditLogger(string? path, long maxSize = 0)
    {
        _path    = path;
        _maxSize = maxSize;
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _worker  = Task.Run(RunAsync);
    }

    /// <summary>
    /// Enqueue an audit entry for writing. Never blocks the caller.
    /// </summary>
    public void Log(
        string    timestamp,
        string?   username,
        string    remoteIp,
        string    action,
        string    path,
        long      bytes,
        int       statusCode)
    {
        var line = JsonSerializer.Serialize(new
        {
            timestamp,
            username,
            remote_ip = remoteIp,
            action,
            path,
            bytes,
            status_code = statusCode
        });
        _channel.Writer.TryWrite(line);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var line in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    if (_path is null)
                    {
                        Console.WriteLine(line);
                    }
                    else
                    {
                        await File.AppendAllTextAsync(_path, line + Environment.NewLine);

                        if (_maxSize > 0 && File.Exists(_path))
                        {
                            var info = new FileInfo(_path);
                            if (info.Length >= _maxSize)
                                File.Move(_path, _path + ".1", overwrite: true);
                        }
                    }
                }
                catch { /* best-effort; individual write failure must not crash the worker */ }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        _cts.Dispose();
    }
}
