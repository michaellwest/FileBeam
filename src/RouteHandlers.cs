using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Threading.Channels;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

public class RouteHandlers(
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
    Action<string, string>? debugLog = null)
{
    // Tracks cumulative bytes uploaded per sender key (IP or username).
    // Best-effort only — not atomic across concurrent uploads from the same sender.
    private readonly ConcurrentDictionary<string, long> _senderQuotas = new();

    // Short-lived cache for GetDirectorySize used by DiskSpace (refreshed at most once per 10 s).
    private long _dirSizeCached;
    private long _dirSizeTicks; // Environment.TickCount64 at last refresh

    // In-memory share token store: token → (resolved file path, expiry, creator username).
    private readonly ConcurrentDictionary<string, (string FilePath, DateTimeOffset Expiry, string Creator)> _shareTokens = new();

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

    /// <summary>
    /// Returns the role attached to this request by the auth middleware.
    /// Defaults to <c>"rw"</c> when no role is present (unauthenticated or shared-password).
    /// </summary>
    private static string GetRole(HttpContext ctx) =>
        ctx.Items.TryGetValue("fb.role", out var r) && r is string s ? s : "rw";

    /// <summary>
    /// Returns the total byte size of all files under <paramref name="dir"/> recursively.
    /// Inaccessible files are skipped so the count is best-effort.
    /// </summary>
    private static long GetDirectorySize(string dir)
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
        var role = GetRole(ctx);

        // Write-only users: redirect to /my-uploads when per-sender is active so they
        // can see their own files; otherwise show the plain upload drop zone.
        if (role == "wo")
        {
            bool separateUploadDir = !rootDir.Equals(uploadDir, StringComparison.OrdinalIgnoreCase);
            if (perSender && separateUploadDir)
                return Results.Redirect("/my-uploads");

            var woHtml = HtmlRenderer.RenderDirectory("", [], [], isReadOnly, csrfToken, "name", "asc", role,
                separateUploadDir: separateUploadDir);
            return Results.Content(woHtml, "text/html");
        }

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(rootDir, resolved);
        if (relPath == ".") relPath = "";

        var sort  = ctx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = ctx.Request.Query["order"].FirstOrDefault() ?? "asc";
        var desc  = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);

        var dirs = Directory.GetDirectories(resolved).Select(d => new DirectoryInfo(d));
        dirs = sort switch
        {
            "date" => desc ? dirs.OrderByDescending(d => d.LastWriteTime) : dirs.OrderBy(d => d.LastWriteTime),
            _      => desc ? dirs.OrderByDescending(d => d.Name)           : dirs.OrderBy(d => d.Name)
        };

        var files = Directory.GetFiles(resolved).Select(f => new FileInfo(f));
        files = sort switch
        {
            "size" => desc ? files.OrderByDescending(f => f.Length)        : files.OrderBy(f => f.Length),
            "date" => desc ? files.OrderByDescending(f => f.LastWriteTime) : files.OrderBy(f => f.LastWriteTime),
            _      => desc ? files.OrderByDescending(f => f.Name)          : files.OrderBy(f => f.Name)
        };

        bool separateDir = !rootDir.Equals(uploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks(role, perSender, separateDir, isReadOnly: isReadOnly);
        var html = HtmlRenderer.RenderDirectory(relPath, dirs.ToList(), files.ToList(), isReadOnly, csrfToken, sort, order, role,
            separateUploadDir: separateDir, navLinks: navLinks, perSender: perSender);
        return Results.Content(html, "text/html");
    }

    // GET /upload-area  and  GET /upload-area/browse/{**subpath}
    // Shared upload page when upload dir is separate and --per-sender is off.
    // rw and admin users land here after following the nav link or notice on the browse screen.
    public IResult BrowseUploadArea(HttpContext ctx, string? subpath = null)
    {
        var role = GetRole(ctx);

        // Redirect roles that cannot or should not use this page
        if (isReadOnly || role == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        bool separateDir = !rootDir.Equals(uploadDir, StringComparison.OrdinalIgnoreCase);
        if (!separateDir)
            return Results.Redirect("/");  // same dir — upload is already on the browse screen

        if (perSender)
            return Results.Redirect("/my-uploads");  // per-sender users have their own upload view

        if (role == "wo")
            return Results.Redirect("/");  // wo always shows drop zone on browse

        string resolved;
        try
        {
            resolved = string.IsNullOrEmpty(subpath)
                ? uploadDir
                : Path.GetFullPath(Path.Combine(uploadDir, subpath));

            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(uploadDir, resolved).Replace('\\', '/');
        if (relPath == ".") relPath = "";

        var sort  = ctx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = ctx.Request.Query["order"].FirstOrDefault() ?? "asc";
        var desc  = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);

        var dirs = Directory.GetDirectories(resolved).Select(d => new DirectoryInfo(d));
        dirs = sort switch
        {
            "date" => desc ? dirs.OrderByDescending(d => d.LastWriteTime) : dirs.OrderBy(d => d.LastWriteTime),
            _      => desc ? dirs.OrderByDescending(d => d.Name)           : dirs.OrderBy(d => d.Name)
        };

        var files = Directory.GetFiles(resolved).Select(f => new FileInfo(f));
        files = sort switch
        {
            "size" => desc ? files.OrderByDescending(f => f.Length)        : files.OrderBy(f => f.Length),
            "date" => desc ? files.OrderByDescending(f => f.LastWriteTime) : files.OrderBy(f => f.LastWriteTime),
            _      => desc ? files.OrderByDescending(f => f.Name)          : files.OrderBy(f => f.Name)
        };

        var navLinks = HtmlRenderer.BuildNavLinks(role, perSender, separateDir, showHome: true, isReadOnly: isReadOnly);
        // Pass separateUploadDir:false so the upload form is rendered (files DO land in uploadDir here)
        var html = HtmlRenderer.RenderDirectory(relPath, dirs.ToList(), files.ToList(), isReadOnly, csrfToken, sort, order, role,
            separateUploadDir: false, urlBase: "upload-area", navLinks: navLinks, perSender: perSender);
        return Results.Content(html, "text/html");
    }

    // GET /upload-area/download/{**subpath}
    // Downloads a file from the upload directory; accessible to rw/admin users (not ro/wo).
    public IResult DownloadUploadAreaFile(HttpContext ctx, string? subpath)
    {
        var role = GetRole(ctx);
        if (role == "ro" || role == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(uploadDir, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
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

    // GET /upload-area/download-zip/{**subpath}
    // Downloads a folder from the upload directory as a ZIP; accessible to rw/admin users.
    public IResult DownloadUploadAreaZip(HttpContext ctx, string? subpath)
    {
        var role = GetRole(ctx);
        if (role == "ro" || role == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        string resolved;
        try
        {
            resolved = string.IsNullOrEmpty(subpath)
                ? uploadDir
                : Path.GetFullPath(Path.Combine(uploadDir, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var folderName = string.IsNullOrEmpty(subpath)
            ? Path.GetFileName(uploadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return Results.Stream(async stream =>
        {
            var syncIO = ctx.Features.Get<IHttpBodyControlFeature>();
            if (syncIO != null) syncIO.AllowSynchronousIO = true;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(resolved, file).Replace('\\', '/');
                var entry     = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await fs.CopyToAsync(entryStream);
            }
        }, "application/zip", $"{folderName}.zip");
    }

    // GET /download/{**subpath}
    public IResult DownloadFile(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

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
    public Task<IResult> UploadFiles(HttpContext ctx, string? subpath)
        => Upload(ctx, subpath, redirectBase: "");

    // POST /upload-area/upload/{**subpath}
    // Same upload logic as UploadFiles but redirects back into /upload-area after success.
    public Task<IResult> UploadToUploadArea(HttpContext ctx, string? subpath)
        => Upload(ctx, subpath, redirectBase: "upload-area");

    // POST /my-uploads/upload/{**subpath}
    // Same upload logic as UploadFiles but redirects back into /my-uploads after success.
    public Task<IResult> UploadToMyUploads(HttpContext ctx, string? subpath)
        => Upload(ctx, subpath, redirectBase: "my-uploads");

    private async Task<IResult> Upload(HttpContext ctx, string? subpath, string redirectBase)
    {
        if (isReadOnly)
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        if (GetRole(ctx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

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

        // Enforce total upload directory cap (best-effort; not atomic across concurrent requests)
        var senderKey = ResolveSenderKey(ctx);
        var pending   = form.Files.Sum(f => f.Length);
        if (maxUploadBytesTotal > 0)
        {
            var dirBytes = GetDirectorySize(uploadDir);
            if (dirBytes + pending > maxUploadBytesTotal)
                return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
        }

        // Enforce per-sender cumulative upload quota (best-effort; not atomic across concurrent requests)
        if (maxUploadBytesPerSender > 0)
        {
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

        // Redirect back to the directory that was just uploaded to.
        // For the main browse view: /browse/{subpath} (or / for root).
        // For the my-uploads view: /my-uploads/browse/{subpath} (or /my-uploads for root).
        var browseUrl = string.IsNullOrEmpty(redirectBase)
            ? (string.IsNullOrEmpty(subpath) ? "/" : $"/browse/{subpath}")
            : (string.IsNullOrEmpty(subpath) ? $"/{redirectBase}" : $"/{redirectBase}/browse/{subpath}");
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
    public IResult DeleteFile(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

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

    // POST /delete-dir/{**subpath}
    public IResult DeleteDir(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (string.Equals(resolved, rootDir, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Cannot delete the root directory.");

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        if (Directory.EnumerateFileSystemEntries(resolved).Any())
            return Results.Conflict("Directory is not empty.");

        Directory.Delete(resolved);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var browseUrl = string.IsNullOrEmpty(parentSubpath) ? "/" : $"/browse/{parentSubpath}";
        return Results.Redirect(browseUrl);
    }

    // POST /rename-dir/{**subpath}
    public async Task<IResult> RenameDir(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (string.Equals(resolved, rootDir, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Cannot rename the root directory.");

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var form    = await ctx.Request.ReadFormAsync();
        var newName = Path.GetFileName(form["newname"].ToString());
        if (string.IsNullOrWhiteSpace(newName))
            return Results.BadRequest("No new name provided.");

        var newPath = Path.Combine(Path.GetDirectoryName(resolved)!, newName);
        if (Directory.Exists(newPath))
            return Results.Conflict("A folder with that name already exists.");

        Directory.Move(resolved, newPath);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var browseUrl = string.IsNullOrEmpty(parentSubpath) ? "/" : $"/browse/{parentSubpath}";
        return Results.Redirect(browseUrl);
    }

    // POST /rename/{**subpath}
    public async Task<IResult> RenameFile(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

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

    // POST /share/{**subpath}?ttl=<seconds>  — create a time-limited download token
    public IResult CreateShareLink(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var ttl = shareTtlSeconds;
        if (ctx.Request.Query.TryGetValue("ttl", out var ttlStr)
            && int.TryParse(ttlStr, out var parsedTtl) && parsedTtl > 0)
            ttl = parsedTtl;

        // Lazy eviction of expired tokens before inserting a new one
        foreach (var key in _shareTokens.Keys.ToList())
            if (_shareTokens.TryGetValue(key, out var t) && t.Expiry <= DateTimeOffset.UtcNow)
                _shareTokens.TryRemove(key, out _);

        var creator = ctx.Items.TryGetValue("fb.user", out var u) && u is string s ? s : "?";
        var token   = System.Security.Cryptography.RandomNumberGenerator.GetHexString(64, lowercase: true);
        _shareTokens[token] = (resolved, DateTimeOffset.UtcNow.AddSeconds(ttl), creator);

        return Results.Json(new { url = $"/s/{token}", expiresIn = ttl });
    }

    // GET /s/{token}  — redeem a share token (no auth required)
    public IResult RedeemShareLink(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_shareTokens.TryGetValue(token, out var entry))
            return Results.NotFound("Share link not found.");

        if (entry.Expiry <= DateTimeOffset.UtcNow)
        {
            _shareTokens.TryRemove(token, out _);
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        if (!File.Exists(entry.FilePath))
        {
            _shareTokens.TryRemove(token, out _);
            return Results.NotFound("File no longer exists.");
        }

        var info    = new FileInfo(entry.FilePath);
        var mime    = MimeTypes.GetMimeType(entry.FilePath);
        var stream  = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return Results.File(stream, mime, info.Name, enableRangeProcessing: true);
    }

    // GET /download-zip/{**subpath}
    public IResult DownloadZip(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var folderName = string.IsNullOrEmpty(subpath)
            ? Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return Results.Stream(async stream =>
        {
            var syncIO = ctx.Features.Get<IHttpBodyControlFeature>();
            if (syncIO != null) syncIO.AllowSynchronousIO = true;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(resolved, file).Replace('\\', '/');
                var entry     = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await fs.CopyToAsync(entryStream);
            }
        }, "application/zip", $"{folderName}.zip");
    }

    // POST /mkdir/{**subpath}
    public IResult MkDir(HttpContext ctx, string? subpath)
    {
        if (isReadOnly)
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder name specified.");

        string resolved;
        try { resolved = SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (Directory.Exists(resolved))
            return Results.Conflict("A directory with that name already exists.");

        var parent = Path.GetDirectoryName(resolved);
        if (parent is null || !Directory.Exists(parent))
            return Results.NotFound("Parent directory not found.");

        Directory.CreateDirectory(resolved);

        var relPath = Path.GetRelativePath(rootDir, resolved).Replace('\\', '/');
        return Results.Redirect($"/browse/{relPath}");
    }

    // GET /disk-space  — JSON with available/total bytes for the upload drive,
    // capped by --max-upload-total and/or --max-upload-bytes-per-sender when set.
    public IResult DiskSpace(HttpContext ctx)
    {
        long? driveAvailable = null;
        long? driveTotal     = null;

        try
        {
            var root  = Path.GetPathRoot(uploadDir) ?? uploadDir;
            var drive = new DriveInfo(root);
            driveAvailable = drive.AvailableFreeSpace;
            driveTotal     = drive.TotalSize;
        }
        catch { /* network or virtual drive — skip */ }

        long? available = driveAvailable;
        long? total     = driveTotal;

        if (maxUploadBytesTotal > 0)
        {
            var now = Environment.TickCount64;
            if (now - _dirSizeTicks > 10_000)
            {
                _dirSizeCached = GetDirectorySize(uploadDir);
                _dirSizeTicks  = now;
            }
            var remaining = Math.Max(0L, maxUploadBytesTotal - _dirSizeCached);
            available = available.HasValue ? Math.Min(available.Value, remaining) : remaining;
            total     = total.HasValue    ? Math.Min(total.Value, maxUploadBytesTotal) : maxUploadBytesTotal;
        }

        if (maxUploadBytesPerSender > 0)
        {
            var senderKey = ResolveSenderKey(ctx);
            var already   = _senderQuotas.GetOrAdd(senderKey, 0L);
            var remaining = Math.Max(0L, maxUploadBytesPerSender - already);
            available = available.HasValue ? Math.Min(available.Value, remaining) : remaining;
            total     = total.HasValue    ? Math.Min(total.Value, maxUploadBytesPerSender) : maxUploadBytesPerSender;
        }

        if (!available.HasValue)
            return Results.NoContent();

        return Results.Json(new { availableBytes = available.Value, totalBytes = total!.Value });
    }

    // GET /admin/shares  — list all live share tokens with creator (admin only)
    public IResult ListShareTokens(HttpContext ctx)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var now = DateTimeOffset.UtcNow;
        var live = _shareTokens
            .Where(kv => kv.Value.Expiry > now)
            .Select(kv => new
            {
                tokenPrefix = kv.Key[..8] + "…",
                file        = Path.GetRelativePath(rootDir, kv.Value.FilePath).Replace('\\', '/'),
                creator     = kv.Value.Creator,
                expiresAt   = kv.Value.Expiry.ToString("o"),
                expiresIn   = (int)(kv.Value.Expiry - now).TotalSeconds
            })
            .OrderBy(t => t.expiresAt)
            .ToList();

        return Results.Json(live);
    }

    // GET /admin/revoke  — list active bans (admin only)
    public IResult ListRevocations(HttpContext ctx)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return Results.Json(new
        {
            users = revocationStore?.RevokedUsers ?? [],
            ips   = revocationStore?.RevokedIps   ?? []
        });
    }

    // POST /admin/revoke/user/{username}
    public IResult RevokeUser(HttpContext ctx, string username)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest("Username required.");

        revocationStore?.RevokeUser(username);
        return Results.Ok(new { revoked = username });
    }

    // POST /admin/unrevoke/user/{username}
    public IResult UnrevokeUser(HttpContext ctx, string username)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        revocationStore?.UnrevokeUser(username);
        return Results.Ok(new { unrevoked = username });
    }

    // POST /admin/revoke/ip/{ip}
    public IResult RevokeIp(HttpContext ctx, string ip)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(ip))
            return Results.BadRequest("IP required.");

        revocationStore?.RevokeIp(ip);
        return Results.Ok(new { revokedIp = ip });
    }

    // POST /admin/unrevoke/ip/{ip}
    public IResult UnrevokeIp(HttpContext ctx, string ip)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        revocationStore?.UnrevokeIp(ip);
        return Results.Ok(new { unrevokedIp = ip });
    }

    // GET /my-uploads  and  GET /my-uploads/browse/{**subpath}
    // Scoped to the authenticated sender's subfolder inside uploadDir.
    // Requires --per-sender; returns 404 otherwise (no user-specific folder exists).
    public IResult BrowseMyUploads(HttpContext ctx, string? subpath = null)
    {
        if (!perSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        var role = GetRole(ctx);
        if (role == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var senderKey  = ResolveSenderKey(ctx);
        var senderRoot = Path.Combine(uploadDir, senderKey);

        // Create the sender's subfolder on first visit so the listing renders
        if (!Directory.Exists(senderRoot))
            Directory.CreateDirectory(senderRoot);

        string resolved;
        try
        {
            resolved = string.IsNullOrEmpty(subpath)
                ? senderRoot
                : Path.GetFullPath(Path.Combine(senderRoot, subpath));

            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(senderRoot, resolved).Replace('\\', '/');
        if (relPath == ".") relPath = "";

        var sort  = ctx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = ctx.Request.Query["order"].FirstOrDefault() ?? "asc";
        var desc  = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);

        var dirs = Directory.GetDirectories(resolved).Select(d => new DirectoryInfo(d));
        dirs = sort switch
        {
            "date" => desc ? dirs.OrderByDescending(d => d.LastWriteTime) : dirs.OrderBy(d => d.LastWriteTime),
            _      => desc ? dirs.OrderByDescending(d => d.Name)           : dirs.OrderBy(d => d.Name)
        };

        var files = Directory.GetFiles(resolved).Select(f => new FileInfo(f));
        files = sort switch
        {
            "size" => desc ? files.OrderByDescending(f => f.Length)        : files.OrderBy(f => f.Length),
            "date" => desc ? files.OrderByDescending(f => f.LastWriteTime) : files.OrderBy(f => f.LastWriteTime),
            _      => desc ? files.OrderByDescending(f => f.Name)          : files.OrderBy(f => f.Name)
        };

        bool separateDir = !rootDir.Equals(uploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks(role, perSender, separateDir, showHome: true);
        var html = HtmlRenderer.RenderDirectory(
            relPath, dirs.ToList(), files.ToList(),
            isReadOnly, csrfToken, sort, order, role,
            separateUploadDir: false,  // files uploaded here DO appear in this view
            urlBase: "my-uploads",
            navLinks: navLinks);
        return Results.Content(html, "text/html");
    }

    // GET /my-uploads/download/{**subpath}
    // Resolves relative to the sender's own subfolder; allows wo users to download their own files.
    public IResult DownloadMyUpload(HttpContext ctx, string? subpath)
    {
        if (!perSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        var senderKey  = ResolveSenderKey(ctx);
        var senderRoot = Path.Combine(uploadDir, senderKey);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
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

    // GET /my-uploads/download-zip/{**subpath}
    // Downloads a directory from the sender's subfolder as a ZIP archive.
    public IResult DownloadMyUploadsZip(HttpContext ctx, string? subpath)
    {
        if (!perSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (GetRole(ctx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var senderKey  = ResolveSenderKey(ctx);
        var senderRoot = Path.Combine(uploadDir, senderKey);

        string resolved;
        try
        {
            resolved = string.IsNullOrEmpty(subpath)
                ? senderRoot
                : Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var folderName = string.IsNullOrEmpty(subpath)
            ? senderKey
            : Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return Results.Stream(async stream =>
        {
            var syncIO = ctx.Features.Get<IHttpBodyControlFeature>();
            if (syncIO != null) syncIO.AllowSynchronousIO = true;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(resolved, file).Replace('\\', '/');
                var entry     = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await fs.CopyToAsync(entryStream);
            }
        }, "application/zip", $"{folderName}.zip");
    }

    // GET /admin/uploads/download/{**subpath}
    // Resolves relative to uploadDir; admin only.
    public IResult DownloadAdminUpload(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(uploadDir, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
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

    // GET /my-uploads/info/{**subpath}
    // Returns JSON metadata (including SHA-256 hash) for a file in the sender's own subfolder.
    public async Task<IResult> InfoMyUpload(HttpContext ctx, string? subpath)
    {
        if (!perSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (GetRole(ctx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        var senderKey  = ResolveSenderKey(ctx);
        var senderRoot = Path.Combine(uploadDir, senderKey);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var info     = new FileInfo(resolved);
        var mimeType = MimeTypes.GetMimeType(resolved);

        string sha256;
        await using (var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(fs);
            sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        return Results.Json(new
        {
            name      = info.Name,
            sizeBytes = info.Length,
            size      = HtmlRenderer.FormatSizePublic(info.Length),
            modified  = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
            mimeType,
            sha256
        });
    }

    // POST /my-uploads/delete/{**subpath}
    // Deletes a file from the sender's own subfolder; redirects to parent in /my-uploads.
    public IResult DeleteMyUpload(HttpContext ctx, string? subpath)
    {
        if (!perSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (GetRole(ctx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        var senderKey  = ResolveSenderKey(ctx);
        var senderRoot = Path.Combine(uploadDir, senderKey);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        File.Delete(resolved);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/my-uploads"
            : $"/my-uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // GET /info/{**subpath}
    // Returns JSON metadata (including SHA-256 hash) for a file in the download directory.
    public async Task<IResult> InfoFile(HttpContext ctx, string? subpath)
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

        string sha256;
        await using (var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
        {
            var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(fs);
            sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        return Results.Json(new
        {
            name      = info.Name,
            sizeBytes = info.Length,
            size      = HtmlRenderer.FormatSizePublic(info.Length),
            modified  = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
            mimeType,
            sha256
        });
    }

    // POST /my-uploads/rename/{**subpath}
    // Renames a file in the sender's own subfolder; redirects to parent in /my-uploads.
    public async Task<IResult> RenameMyUpload(HttpContext ctx, string? subpath)
    {
        if (!perSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (GetRole(ctx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        var senderKey  = ResolveSenderKey(ctx);
        var senderRoot = Path.Combine(uploadDir, senderKey);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
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
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/my-uploads"
            : $"/my-uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // POST /my-uploads/rename-dir/{**subpath}
    // Renames a folder in the sender's own subfolder; redirects to parent in /my-uploads.
    public async Task<IResult> RenameMyUploadDir(HttpContext ctx, string? subpath)
    {
        if (!perSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (GetRole(ctx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

        var senderKey  = ResolveSenderKey(ctx);
        var senderRoot = Path.Combine(uploadDir, senderKey);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (string.Equals(resolved, senderRoot, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Cannot rename your own root folder.");

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var form    = await ctx.Request.ReadFormAsync();
        var newName = Path.GetFileName(form["newname"].ToString());
        if (string.IsNullOrWhiteSpace(newName))
            return Results.BadRequest("No new name provided.");

        var newPath = Path.Combine(Path.GetDirectoryName(resolved)!, newName);
        if (Directory.Exists(newPath))
            return Results.Conflict("A folder with that name already exists.");

        Directory.Move(resolved, newPath);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/my-uploads"
            : $"/my-uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // POST /admin/uploads/delete/{**subpath}
    // Deletes a file from the upload directory; redirects to parent in /admin/uploads. Admin only.
    public IResult DeleteAdminUpload(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(uploadDir, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        File.Delete(resolved);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/admin/uploads"
            : $"/admin/uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // POST /admin/uploads/rename/{**subpath}
    // Renames a file in the upload directory; redirects to parent in /admin/uploads. Admin only.
    public async Task<IResult> RenameAdminUpload(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(uploadDir, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
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
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/admin/uploads"
            : $"/admin/uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // POST /admin/uploads/rename-dir/{**subpath}
    // Renames a folder in the upload directory; redirects to parent in /admin/uploads. Admin only.
    public async Task<IResult> RenameAdminUploadDir(HttpContext ctx, string? subpath)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(uploadDir, subpath));
            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var form    = await ctx.Request.ReadFormAsync();
        var newName = Path.GetFileName(form["newname"].ToString());
        if (string.IsNullOrWhiteSpace(newName))
            return Results.BadRequest("No new name provided.");

        var newPath = Path.Combine(Path.GetDirectoryName(resolved)!, newName);
        if (Directory.Exists(newPath))
            return Results.Conflict("A folder with that name already exists.");

        Directory.Move(resolved, newPath);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/admin/uploads"
            : $"/admin/uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // GET /admin/uploads  and  GET /admin/uploads/browse/{**subpath}
    // Read-only browse of the full upload directory. Admin only.
    public IResult BrowseAdminUploads(HttpContext ctx, string? subpath = null)
    {
        if (GetRole(ctx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // The catch-all route /admin/uploads/{**subpath} can receive "browse/foo" when the
        // more-specific /admin/uploads/browse/{**subpath} route is not matched first.
        // Strip the "browse/" prefix so path resolution is always relative to uploadDir.
        if (subpath != null && subpath.StartsWith("browse/", StringComparison.OrdinalIgnoreCase))
            subpath = subpath["browse/".Length..];
        else if (string.Equals(subpath, "browse", StringComparison.OrdinalIgnoreCase))
            subpath = null;

        string resolved;
        try
        {
            resolved = string.IsNullOrEmpty(subpath)
                ? uploadDir
                : Path.GetFullPath(Path.Combine(uploadDir, subpath));

            if (!resolved.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(uploadDir, resolved).Replace('\\', '/');
        if (relPath == ".") relPath = "";

        var sort  = ctx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = ctx.Request.Query["order"].FirstOrDefault() ?? "asc";
        var desc  = string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase);

        var dirs = Directory.GetDirectories(resolved).Select(d => new DirectoryInfo(d));
        dirs = sort switch
        {
            "date" => desc ? dirs.OrderByDescending(d => d.LastWriteTime) : dirs.OrderBy(d => d.LastWriteTime),
            _      => desc ? dirs.OrderByDescending(d => d.Name)           : dirs.OrderBy(d => d.Name)
        };

        var files = Directory.GetFiles(resolved).Select(f => new FileInfo(f));
        files = sort switch
        {
            "size" => desc ? files.OrderByDescending(f => f.Length)        : files.OrderBy(f => f.Length),
            "date" => desc ? files.OrderByDescending(f => f.LastWriteTime) : files.OrderBy(f => f.LastWriteTime),
            _      => desc ? files.OrderByDescending(f => f.Name)          : files.OrderBy(f => f.Name)
        };

        bool separateDir = !rootDir.Equals(uploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks("admin", perSender, separateDir, showHome: true);
        var html = HtmlRenderer.RenderDirectory(
            relPath, dirs.ToList(), files.ToList(),
            isReadOnly: true,
            csrfToken, sort, order, role: "admin",
            separateUploadDir: false,
            urlBase: "admin/uploads",
            navLinks: navLinks);
        return Results.Content(html, "text/html");
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
