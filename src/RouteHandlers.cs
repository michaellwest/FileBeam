using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

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

    private void OnChanged(object _, FileSystemEventArgs __)
    {
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

public class RouteHandlers(
    string rootDir,
    string uploadDir,
    FileWatcher watcher,
    bool isReadOnly = false,
    bool perSender = false,
    long maxFileSize = 0,
    long maxUploadBytesPerSender = 0,
    int maxDirDepth = 10,
    int maxFilesPerDir = 1000,
    string csrfToken = "",
    Action<string, string>? debugLog = null)
{
    // Tracks cumulative bytes uploaded per sender key (IP or username).
    // Best-effort only — not atomic across concurrent uploads from the same sender.
    private readonly ConcurrentDictionary<string, long> _senderQuotas = new();

    private string SafeResolvePath(string? subpath)
    {
        // Treat null/empty as the root directory
        if (string.IsNullOrEmpty(subpath))
            return rootDir;

        var combined = Path.GetFullPath(Path.Combine(rootDir, subpath));

        // Prevent path traversal
        if (!combined.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal not allowed.");

        // Prevent symlink / junction traversal escaping the root
        if (HasReparsePointInChain(rootDir, combined))
            throw new UnauthorizedAccessException("Symlink traversal not allowed.");

        return combined;
    }

    /// <summary>
    /// Resolves an upload path relative to <paramref name="root"/> (defaults to <see cref="uploadDir"/>).
    /// Path traversal and symlink traversal are always validated against the base <see cref="uploadDir"/>
    /// so that per-sender subfolders cannot escape the drop root.
    /// </summary>
    private string SafeResolveUploadPath(string? subpath, string? root = null)
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
    private static bool HasReparsePointInChain(string root, string path)
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
    private string ResolveSenderKey(HttpContext ctx)
    {
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
        return SanitizeName(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>Replaces characters that are invalid in file/folder names with underscores.</summary>
    private static string SanitizeName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
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
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(rootDir, resolved);
        if (relPath == ".") relPath = "";

        var dirs  = Directory.GetDirectories(resolved).Select(d => new DirectoryInfo(d)).OrderBy(d => d.Name).ToList();
        var files = Directory.GetFiles(resolved).Select(f => new FileInfo(f)).OrderBy(f => f.Name).ToList();

        var html = HtmlRenderer.RenderDirectory(relPath, dirs, files, isReadOnly, csrfToken);
        return Results.Content(html, "text/html");
    }

    // GET /download/{**subpath}
    public IResult DownloadFile(HttpContext ctx, string? subpath)
    {
        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var info     = new FileInfo(resolved);
        var mimeType = MimeTypes.GetMimeType(resolved);

        ctx.Items["fb.file"]  = info.Name;
        ctx.Items["fb.bytes"] = info.Length;

        FileStream stream;
        try { stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (IOException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError); }

        return Results.File(stream, mimeType, info.Name, enableRangeProcessing: true);
    }

    // POST /upload/{**subpath}
    public async Task<IResult> UploadFiles(HttpContext ctx, string? subpath)
    {
        if (isReadOnly)
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        // Uploads land in uploadDir (private when separate from rootDir).
        // With --per-sender, each sender gets their own subfolder named after their
        // Basic Auth username (if authenticated) or remote IP address.
        var dropRoot = perSender
            ? Path.Combine(uploadDir, ResolveSenderKey(ctx))
            : uploadDir;

        string resolved;
        try { resolved = SafeResolveUploadPath(subpath, dropRoot); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        // Enforce maximum directory depth relative to the upload root
        var depth = resolved.Split(Path.DirectorySeparatorChar).Length
                  - uploadDir.Split(Path.DirectorySeparatorChar).Length;
        if (depth > maxDirDepth)
            return Results.BadRequest("Upload path exceeds maximum directory depth.");

        // Create the target subdirectory if it doesn't exist yet (mirrors source structure).
        if (!Directory.Exists(resolved))
            Directory.CreateDirectory(resolved);

        // Reject if disk free space falls below 512 MB
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(resolved)!);
            if (drive.AvailableFreeSpace < 512L * 1024 * 1024)
                return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
        }
        catch { /* DriveInfo can fail for network or virtual paths — skip the check */ }

        var form = await ctx.Request.ReadFormAsync();
        if (form.Files.Count == 0)
            return Results.BadRequest("No files uploaded.");

        // Enforce per-file size limit
        if (maxFileSize > 0)
        {
            foreach (var f in form.Files)
            {
                if (f.Length > maxFileSize)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
        }

        // Enforce per-directory file count cap (also bounds ResolveUniqueFileName iterations)
        var existingCount = Directory.GetFiles(resolved).Length;
        if (existingCount + form.Files.Count > maxFilesPerDir)
            return Results.StatusCode(StatusCodes.Status507InsufficientStorage);

        // Enforce per-sender cumulative upload quota (best-effort; not atomic across concurrent requests)
        var senderKey = ResolveSenderKey(ctx);
        if (maxUploadBytesPerSender > 0)
        {
            var pending = form.Files.Sum(f => f.Length);
            var already = _senderQuotas.GetOrAdd(senderKey, 0L);
            if (already + pending > maxUploadBytesPerSender)
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        long totalBytes = 0;
        int  fileCount  = 0;
        string? firstName = null;

        foreach (var file in form.Files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName)) continue;

            // Sanitise filename — strip any directory components, then resolve a unique name
            var safeName  = Path.GetFileName(file.FileName);
            var finalName = ResolveUniqueFileName(resolved, safeName);
            var dest      = Path.Combine(resolved, finalName);
            var partDest  = dest + ".part";
            var uploadId  = ctx.Items.TryGetValue("fb.request.id", out var idObj) ? idObj as string : null;
            var uploadDirName = Path.GetFileName(uploadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var relPartDest   = "/" + uploadDirName + "/" + Path.GetRelativePath(uploadDir, partDest).Replace('\\', '/');
            debugLog?.Invoke(uploadId ?? "?", relPartDest);

            try
            {
                await using var fs = new FileStream(partDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await file.CopyToAsync(fs, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client cancelled mid-upload — remove the incomplete .part file.
                if (File.Exists(partDest)) File.Delete(partDest);
                throw;
            }

            // Promote the temporary file to its final name.
            File.Move(partDest, dest);

            firstName  ??= finalName;
            totalBytes  += file.Length;
            fileCount++;
        }

        // Update per-sender cumulative quota after a successful upload
        if (maxUploadBytesPerSender > 0)
            _senderQuotas.AddOrUpdate(senderKey, totalBytes, (_, existing) => existing + totalBytes);

        ctx.Items["fb.bytes"] = totalBytes;
        ctx.Items["fb.count"] = fileCount;
        if (fileCount == 1 && firstName != null)
            ctx.Items["fb.file"] = firstName;

        // Redirect back to the directory listing
        var browseUrl = string.IsNullOrEmpty(subpath) ? "/" : $"/browse/{subpath}";
        return Results.Redirect(browseUrl);
    }

    /// <summary>
    /// Returns a filename that does not collide with any existing file in <paramref name="dir"/>.
    /// If <paramref name="fileName"/> is already taken, appends " (1)", " (2)", … before the extension.
    /// The loop is capped at 100 to bound filesystem stat calls; a GUID suffix is used as a final fallback.
    /// </summary>
    private static string ResolveUniqueFileName(string dir, string fileName)
    {
        if (!File.Exists(Path.Combine(dir, fileName)))
            return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);

        for (int i = 1; i <= 100; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate)))
                return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    // POST /delete/{**subpath}
    public IResult DeleteFile(string? subpath)
    {
        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        File.Delete(resolved);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var browseUrl = string.IsNullOrEmpty(parentSubpath) ? "/" : $"/browse/{parentSubpath}";
        return Results.Redirect(browseUrl);
    }

    // POST /rename/{**subpath}
    public async Task<IResult> RenameFile(HttpContext ctx, string? subpath)
    {
        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var form    = await ctx.Request.ReadFormAsync();
        var newName = Path.GetFileName(form["newname"].ToString());
        if (string.IsNullOrWhiteSpace(newName))
            return Results.BadRequest("No new name provided.");

        var newPath = Path.Combine(Path.GetDirectoryName(resolved)!, newName);
        if (File.Exists(newPath))
            return Results.Conflict("A file with that name already exists.");

        File.Move(resolved, newPath);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var browseUrl = string.IsNullOrEmpty(parentSubpath) ? "/" : $"/browse/{parentSubpath}";
        return Results.Redirect(browseUrl);
    }

    // GET /events  — Server-Sent Events stream for live reload
    public async Task FileEvents(HttpContext ctx)
    {
        var ch = watcher.TrySubscribe();
        if (ch is null)
        {
            // SSE connection cap reached — reject with 503
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        ctx.Response.Headers["Content-Type"]      = "text/event-stream";
        ctx.Response.Headers["Cache-Control"]     = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.Body.FlushAsync();

        var lifetime = ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            ctx.RequestAborted, lifetime.ApplicationStopping);

        try
        {
            await foreach (var msg in ch.Reader.ReadAllAsync(cts.Token))
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
