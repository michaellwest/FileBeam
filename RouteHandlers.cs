using System.Threading.Channels;
using System.Web;
using Microsoft.AspNetCore.Http;

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

    public FileWatcher(string rootDir)
    {
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

    private void OnChanged(object _, FileSystemEventArgs __)
    {
        lock (_lock)
        {
            foreach (var ch in _clients)
                ch.Writer.TryWrite("reload");
        }
    }

    /// <summary>Subscribe a new SSE client; returns a channel to read events from.</summary>
    public Channel<string> Subscribe()
    {
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
    }

    public void Dispose() => _watcher.Dispose();
}

public class RouteHandlers(string rootDir, FileWatcher watcher)
{
    private string SafeResolvePath(string? subpath)
    {
        // Treat null/empty as the root directory
        if (string.IsNullOrEmpty(subpath))
            return rootDir;

        var combined = Path.GetFullPath(Path.Combine(rootDir, subpath));

        // Prevent path traversal
        if (!combined.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal not allowed.");

        return combined;
    }

    // GET /
    public IResult ListDirectory(HttpContext ctx)
        => Browse(ctx, null);

    // GET /browse/{**subpath}
    public IResult BrowseDirectory(HttpContext ctx, string? subpath)
        => Browse(ctx, subpath);

    private IResult Browse(HttpContext ctx, string? subpath)
    {
        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.Forbid(); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(rootDir, resolved);
        if (relPath == ".") relPath = "";

        var dirs  = Directory.GetDirectories(resolved).Select(d => new DirectoryInfo(d)).OrderBy(d => d.Name).ToList();
        var files = Directory.GetFiles(resolved).Select(f => new FileInfo(f)).OrderBy(f => f.Name).ToList();

        var html = HtmlRenderer.RenderDirectory(relPath, dirs, files);
        return Results.Content(html, "text/html");
    }

    // GET /download/{**subpath}
    public IResult DownloadFile(string? subpath)
    {
        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.Forbid(); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var stream   = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var mimeType = MimeTypes.GetMimeType(resolved);
        var fileName = Path.GetFileName(resolved);

        return Results.File(stream, mimeType, fileName, enableRangeProcessing: true);
    }

    // POST /upload/{**subpath}
    public async Task<IResult> UploadFiles(HttpContext ctx, string? subpath)
    {
        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.Forbid(); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var form = await ctx.Request.ReadFormAsync();
        if (form.Files.Count == 0)
            return Results.BadRequest("No files uploaded.");

        foreach (var file in form.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName)) continue;

            // Sanitise filename — strip any directory components
            var safeName = Path.GetFileName(file.FileName);
            var dest     = Path.Combine(resolved, safeName);

            await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await file.CopyToAsync(fs);
        }

        // Redirect back to the directory listing
        var browseUrl = string.IsNullOrEmpty(subpath) ? "/" : $"/browse/{subpath}";
        return Results.Redirect(browseUrl);
    }

    // GET /events  — Server-Sent Events stream for live reload
    public async Task FileEvents(HttpContext ctx)
    {
        ctx.Response.Headers["Content-Type"]      = "text/event-stream";
        ctx.Response.Headers["Cache-Control"]     = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.Body.FlushAsync();

        var ch = watcher.Subscribe();
        try
        {
            await foreach (var msg in ch.Reader.ReadAllAsync(ctx.RequestAborted))
            {
                await ctx.Response.WriteAsync($"data: {msg}\n\n");
                await ctx.Response.Body.FlushAsync();
            }
        }
        catch (OperationCanceledException) { /* client disconnected — normal */ }
        finally
        {
            watcher.Unsubscribe(ch);
        }
    }
}
