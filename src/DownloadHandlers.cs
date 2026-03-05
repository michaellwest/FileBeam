using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace FileBeam;

/// <summary>
/// Handles file and ZIP download endpoints, plus /info metadata endpoints.
/// </summary>
internal sealed class DownloadHandlers(HandlerContext ctx)
{
    // GET /download/{**subpath}
    internal IResult DownloadFile(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var info     = new FileInfo(resolved);
        var mimeType = MimeTypes.GetMimeType(resolved);

        httpCtx.Items["fb.file"]  = info.Name;
        httpCtx.Items["fb.bytes"] = info.Length;

        FileStream stream;
        try { stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (IOException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError); }

        return Results.File(stream, mimeType, info.Name, enableRangeProcessing: true);
    }

    // GET /download-zip/{**subpath}
    internal IResult DownloadZip(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var folderName = string.IsNullOrEmpty(subpath)
            ? Path.GetFileName(ctx.RootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (ctx.MaxZipBytes > 0 && HandlerContext.GetDirectorySize(resolved) > ctx.MaxZipBytes)
            return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);

        if (ctx.ZipSemaphore is not null && !ctx.ZipSemaphore.Wait(0))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        return Results.Stream(async stream =>
        {
            try
            {
                var syncIO = httpCtx.Features.Get<IHttpBodyControlFeature>();
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
            }
            finally
            {
                ctx.ZipSemaphore?.Release();
            }
        }, "application/zip", $"{folderName}.zip");
    }

    // GET /upload-area/download/{**subpath}
    // Downloads a file from the upload directory; accessible to rw/admin users (not ro/wo).
    internal IResult DownloadUploadAreaFile(HttpContext httpCtx, string? subpath)
    {
        var role = HandlerContext.GetRole(httpCtx);
        if (role == "ro" || role == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(ctx.UploadDir, subpath));
            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var info     = new FileInfo(resolved);
        var mimeType = MimeTypes.GetMimeType(resolved);

        httpCtx.Items["fb.file"]  = info.Name;
        httpCtx.Items["fb.bytes"] = info.Length;

        FileStream stream;
        try { stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (IOException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError); }

        return Results.File(stream, mimeType, info.Name, enableRangeProcessing: true);
    }

    // GET /upload-area/download-zip/{**subpath}
    // Downloads a folder from the upload directory as a ZIP; accessible to rw/admin users.
    internal IResult DownloadUploadAreaZip(HttpContext httpCtx, string? subpath)
    {
        var role = HandlerContext.GetRole(httpCtx);
        if (role == "ro" || role == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        string resolved;
        try
        {
            resolved = string.IsNullOrEmpty(subpath)
                ? ctx.UploadDir
                : Path.GetFullPath(Path.Combine(ctx.UploadDir, subpath));
            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var folderName = string.IsNullOrEmpty(subpath)
            ? Path.GetFileName(ctx.UploadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (ctx.MaxZipBytes > 0 && HandlerContext.GetDirectorySize(resolved) > ctx.MaxZipBytes)
            return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);

        if (ctx.ZipSemaphore is not null && !ctx.ZipSemaphore.Wait(0))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        return Results.Stream(async stream =>
        {
            try
            {
                var syncIO = httpCtx.Features.Get<IHttpBodyControlFeature>();
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
            }
            finally
            {
                ctx.ZipSemaphore?.Release();
            }
        }, "application/zip", $"{folderName}.zip");
    }

    // GET /my-uploads/download/{**subpath}
    // Resolves relative to the sender's own subfolder; allows wo users to download their own files.
    internal IResult DownloadMyUpload(HttpContext httpCtx, string? subpath)
    {
        if (!ctx.PerSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        var senderKey  = ctx.ResolveSenderKey(httpCtx);
        var senderRoot = Path.Combine(ctx.UploadDir, senderKey);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var info     = new FileInfo(resolved);
        var mimeType = MimeTypes.GetMimeType(resolved);

        httpCtx.Items["fb.file"]  = info.Name;
        httpCtx.Items["fb.bytes"] = info.Length;

        FileStream stream;
        try { stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (IOException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError); }

        return Results.File(stream, mimeType, info.Name, enableRangeProcessing: true);
    }

    // GET /my-uploads/download-zip/{**subpath}
    // Downloads a directory from the sender's subfolder as a ZIP archive.
    internal IResult DownloadMyUploadsZip(HttpContext httpCtx, string? subpath)
    {
        if (!ctx.PerSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (HandlerContext.GetRole(httpCtx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var senderKey  = ctx.ResolveSenderKey(httpCtx);
        var senderRoot = Path.Combine(ctx.UploadDir, senderKey);

        string resolved;
        try
        {
            resolved = string.IsNullOrEmpty(subpath)
                ? senderRoot
                : Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var folderName = string.IsNullOrEmpty(subpath)
            ? senderKey
            : Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (ctx.MaxZipBytes > 0 && HandlerContext.GetDirectorySize(resolved) > ctx.MaxZipBytes)
            return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);

        if (ctx.ZipSemaphore is not null && !ctx.ZipSemaphore.Wait(0))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        return Results.Stream(async stream =>
        {
            try
            {
                var syncIO = httpCtx.Features.Get<IHttpBodyControlFeature>();
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
            }
            finally
            {
                ctx.ZipSemaphore?.Release();
            }
        }, "application/zip", $"{folderName}.zip");
    }

    // GET /admin/uploads/download/{**subpath}
    // Resolves relative to uploadDir; admin only.
    internal IResult DownloadAdminUpload(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(ctx.UploadDir, subpath));
            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var info     = new FileInfo(resolved);
        var mimeType = MimeTypes.GetMimeType(resolved);

        httpCtx.Items["fb.file"]  = info.Name;
        httpCtx.Items["fb.bytes"] = info.Length;

        FileStream stream;
        try { stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        catch (IOException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError); }

        return Results.File(stream, mimeType, info.Name, enableRangeProcessing: true);
    }

    // POST /download-zip
    // Bulk-downloads selected files from serveDir as a ZIP; blocked for wo.
    internal async Task<IResult> BulkDownloadFiles(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        BulkPathsRequest? req;
        try { req = await JsonSerializer.DeserializeAsync<BulkPathsRequest>(httpCtx.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return Results.BadRequest("Invalid JSON body."); }

        if (req is null || req.Paths is null || req.Paths.Length == 0)
            return Results.BadRequest("No paths specified.");

        return BulkZip(httpCtx, req.Paths, resolveRoot: ctx.RootDir, validateRoot: ctx.RootDir);
    }

    // POST /upload-area/download-zip
    // Bulk-downloads selected files from uploadDir as a ZIP; accessible to rw/admin.
    internal async Task<IResult> BulkDownloadUploadAreaFiles(HttpContext httpCtx)
    {
        var role = HandlerContext.GetRole(httpCtx);
        if (role == "ro" || role == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        BulkPathsRequest? req;
        try { req = await JsonSerializer.DeserializeAsync<BulkPathsRequest>(httpCtx.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return Results.BadRequest("Invalid JSON body."); }

        if (req is null || req.Paths is null || req.Paths.Length == 0)
            return Results.BadRequest("No paths specified.");

        return BulkZip(httpCtx, req.Paths, resolveRoot: ctx.UploadDir, validateRoot: ctx.UploadDir);
    }

    // POST /my-uploads/download-zip
    // Bulk-downloads selected files from the sender's own subfolder; blocked for wo.
    internal async Task<IResult> BulkDownloadMyUploads(HttpContext httpCtx)
    {
        if (!ctx.PerSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (HandlerContext.GetRole(httpCtx) == "wo")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        BulkPathsRequest? req;
        try { req = await JsonSerializer.DeserializeAsync<BulkPathsRequest>(httpCtx.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return Results.BadRequest("Invalid JSON body."); }

        if (req is null || req.Paths is null || req.Paths.Length == 0)
            return Results.BadRequest("No paths specified.");

        var senderKey  = ctx.ResolveSenderKey(httpCtx);
        var senderRoot = Path.Combine(ctx.UploadDir, senderKey);
        return BulkZip(httpCtx, req.Paths, resolveRoot: senderRoot, validateRoot: ctx.UploadDir);
    }

    // POST /admin/uploads/download-zip
    // Bulk-downloads selected files from uploadDir as a ZIP; admin only.
    internal async Task<IResult> BulkDownloadAdminUploads(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        BulkPathsRequest? req;
        try { req = await JsonSerializer.DeserializeAsync<BulkPathsRequest>(httpCtx.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return Results.BadRequest("Invalid JSON body."); }

        if (req is null || req.Paths is null || req.Paths.Length == 0)
            return Results.BadRequest("No paths specified.");

        return BulkZip(httpCtx, req.Paths, resolveRoot: ctx.UploadDir, validateRoot: ctx.UploadDir);
    }

    /// <summary>
    /// Shared bulk-ZIP helper. Resolves each path against <paramref name="resolveRoot"/>,
    /// validates against <paramref name="validateRoot"/>, skips missing files, and streams
    /// a ZIP archive named <c>selection.zip</c>.
    /// </summary>
    private IResult BulkZip(HttpContext httpCtx, string[] paths, string resolveRoot, string validateRoot)
    {
        var resolvedFiles = new List<(string entryName, string fullPath)>();
        foreach (var p in paths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(resolveRoot, p));
                if (!full.StartsWith(validateRoot, StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (HandlerContext.HasReparsePointInChain(validateRoot, full))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

            if (File.Exists(full))
                resolvedFiles.Add((p.Replace('\\', '/'), full));
        }

        if (resolvedFiles.Count == 0)
            return Results.NotFound("No valid files found.");

        if (ctx.MaxZipBytes > 0)
        {
            long totalBytes = 0;
            foreach (var (_, fp) in resolvedFiles)
                try { totalBytes += new FileInfo(fp).Length; } catch { }
            if (totalBytes > ctx.MaxZipBytes)
                return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
        }

        if (ctx.ZipSemaphore is not null && !ctx.ZipSemaphore.Wait(0))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var captured = resolvedFiles;
        return Results.Stream(async stream =>
        {
            try
            {
                var syncIO = httpCtx.Features.Get<IHttpBodyControlFeature>();
                if (syncIO != null) syncIO.AllowSynchronousIO = true;
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var (entryName, fullPath) in captured)
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                    await fs.CopyToAsync(entryStream);
                }
            }
            finally
            {
                ctx.ZipSemaphore?.Release();
            }
        }, "application/zip", "selection.zip");
    }

    // GET /info/{**subpath}
    // Returns JSON metadata (including SHA-256 hash) for a file in the download directory.
    internal async Task<IResult> InfoFile(HttpContext httpCtx, string? subpath)
    {
        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
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

    // GET /my-uploads/info/{**subpath}
    // Returns JSON metadata (including SHA-256 hash) for a file in the sender's own subfolder.
    internal async Task<IResult> InfoMyUpload(HttpContext httpCtx, string? subpath)
    {
        if (!ctx.PerSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (HandlerContext.GetRole(httpCtx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        var senderKey  = ctx.ResolveSenderKey(httpCtx);
        var senderRoot = Path.Combine(ctx.UploadDir, senderKey);

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(senderRoot, subpath));
            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
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
}
