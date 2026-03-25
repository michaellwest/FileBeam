using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace FileBeam;

/// <summary>
/// Handles file upload endpoints, per-sender quota tracking, and the disk-space endpoint.
/// Supports both multipart form uploads and raw octet-stream uploads with optional
/// <c>Content-Range</c> for resumable chunked transfers.
/// </summary>
internal sealed partial class UploadHandlers(HandlerContext ctx)
{
    /// <summary>Minimum file size (50 MB) before the browser client switches to chunked upload.</summary>
    internal const long ChunkedThreshold = 50L * 1024 * 1024;

    /// <summary>Regex for parsing <c>Content-Range: bytes START-END/TOTAL</c> headers.</summary>
    [GeneratedRegex(@"^bytes\s+(\d+)-(\d+)/(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContentRangeRegex();

    // Tracks cumulative bytes uploaded per sender key (IP or username).
    // Best-effort only — not atomic across concurrent uploads from the same sender.
    private readonly ConcurrentDictionary<string, long> _senderQuotas = new();

    // POST /upload/{**subpath}
    internal Task<IResult> UploadFiles(HttpContext httpCtx, string? subpath)
        => IsStreamUpload(httpCtx)
            ? UploadStream(httpCtx, subpath, redirectBase: "")
            : Upload(httpCtx, subpath, redirectBase: "");

    // POST /upload-area/upload/{**subpath}
    // Same upload logic as UploadFiles but redirects back into /upload-area after success.
    internal Task<IResult> UploadToUploadArea(HttpContext httpCtx, string? subpath)
        => IsStreamUpload(httpCtx)
            ? UploadStream(httpCtx, subpath, redirectBase: "upload-area")
            : Upload(httpCtx, subpath, redirectBase: "upload-area");

    // POST /my-uploads/upload/{**subpath}
    // Same upload logic as UploadFiles but redirects back into /my-uploads after success.
    internal Task<IResult> UploadToMyUploads(HttpContext httpCtx, string? subpath)
        => IsStreamUpload(httpCtx)
            ? UploadStream(httpCtx, subpath, redirectBase: "my-uploads")
            : Upload(httpCtx, subpath, redirectBase: "my-uploads");

    /// <summary>Returns true when the request uses the raw stream upload path.</summary>
    private static bool IsStreamUpload(HttpContext httpCtx)
        => httpCtx.Request.ContentType?.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<IResult> Upload(HttpContext httpCtx, string? subpath, string redirectBase)
    {
        if (ctx.IsReadOnly)
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        if (HandlerContext.GetRole(httpCtx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Uploads land in uploadDir (private when separate from rootDir).
        // With --per-sender, each sender gets their own subfolder named after their
        // Basic Auth username (if authenticated) or remote IP address.
        var dropRoot = ctx.PerSender
            ? Path.Combine(ctx.UploadDir, ctx.ResolveSenderKey(httpCtx))
            : ctx.UploadDir;

        string resolved;
        try { resolved = ctx.SafeResolveUploadPath(subpath, dropRoot); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        // Enforce maximum directory depth relative to the upload root
        var depth = resolved.Split(Path.DirectorySeparatorChar).Length
                  - ctx.UploadDir.Split(Path.DirectorySeparatorChar).Length;
        if (depth > ctx.MaxDirDepth)
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

        var form = await httpCtx.Request.ReadFormAsync();
        if (form.Files.Count == 0)
            return Results.BadRequest("No files uploaded.");

        // Enforce per-file size limit
        if (ctx.MaxFileSize > 0)
        {
            foreach (var f in form.Files)
            {
                if (f.Length > ctx.MaxFileSize)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
        }

        // Enforce per-directory file count cap (also bounds ResolveUniqueFileName iterations)
        var existingCount = Directory.GetFiles(resolved).Length;
        if (existingCount + form.Files.Count > ctx.MaxFilesPerDir)
            return Results.StatusCode(StatusCodes.Status507InsufficientStorage);

        // Enforce total upload directory cap (best-effort; not atomic across concurrent requests)
        var senderKey = ctx.ResolveSenderKey(httpCtx);
        var pending   = form.Files.Sum(f => f.Length);
        if (ctx.MaxUploadBytesTotal > 0)
        {
            var dirBytes = HandlerContext.GetDirectorySize(ctx.UploadDir);
            if (dirBytes + pending > ctx.MaxUploadBytesTotal)
                return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
        }

        // Enforce per-sender cumulative upload quota (best-effort; not atomic across concurrent requests)
        if (ctx.MaxUploadBytesPerSender > 0)
        {
            var already = _senderQuotas.GetOrAdd(senderKey, 0L);
            if (already + pending > ctx.MaxUploadBytesPerSender)
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
            var uploadId  = httpCtx.Items.TryGetValue("fb.request.id", out var idObj) ? idObj as string : null;
            var uploadDirName = Path.GetFileName(ctx.UploadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var relPartDest   = "/" + uploadDirName + "/" + Path.GetRelativePath(ctx.UploadDir, partDest).Replace('\\', '/');
            ctx.DebugLog?.Invoke(uploadId ?? "?", relPartDest);

            try
            {
                await using var fs = new FileStream(partDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await file.CopyToAsync(fs, httpCtx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client cancelled mid-upload — remove the incomplete .part file.
                if (File.Exists(partDest)) File.Delete(partDest);
                throw;
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                // Client disconnected mid-upload (network drop, timeout, browser closed).
                // Clean up the incomplete .part file and re-throw so the logging middleware
                // can report it as a warning rather than an unhandled 500 error.
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
        if (ctx.MaxUploadBytesPerSender > 0)
            _senderQuotas.AddOrUpdate(senderKey, totalBytes, (_, existing) => existing + totalBytes);

        httpCtx.Items["fb.bytes"] = totalBytes;
        httpCtx.Items["fb.count"] = fileCount;
        if (fileCount == 1 && firstName != null)
            httpCtx.Items["fb.file"] = firstName;

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

    // ── Resumable stream upload (application/octet-stream + Content-Range) ─────

    /// <summary>
    /// Handles raw octet-stream uploads with optional <c>Content-Range</c> for resumable chunked transfers.
    /// <para>
    /// Without <c>Content-Range</c>: streams the entire body to a <c>.part</c> file, then renames to final name.
    /// </para>
    /// <para>
    /// With <c>Content-Range: bytes START-END/TOTAL</c>: appends the chunk to the <c>.part</c> file at the
    /// given offset. When all bytes have been received (<c>END+1 == TOTAL</c>), the file is finalized.
    /// </para>
    /// </summary>
    private async Task<IResult> UploadStream(HttpContext httpCtx, string? subpath, string redirectBase)
    {
        if (ctx.IsReadOnly)
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        if (HandlerContext.GetRole(httpCtx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // Require X-Upload-Filename header — the filename to write to
        var rawFileName = httpCtx.Request.Headers["X-Upload-Filename"].ToString();
        if (string.IsNullOrWhiteSpace(rawFileName))
            return Results.BadRequest("X-Upload-Filename header is required for stream uploads.");

        var safeName = Path.GetFileName(rawFileName);
        if (string.IsNullOrWhiteSpace(safeName))
            return Results.BadRequest("Invalid filename.");

        var dropRoot = ctx.PerSender
            ? Path.Combine(ctx.UploadDir, ctx.ResolveSenderKey(httpCtx))
            : ctx.UploadDir;

        string resolved;
        try { resolved = ctx.SafeResolveUploadPath(subpath, dropRoot); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        var depth = resolved.Split(Path.DirectorySeparatorChar).Length
                  - ctx.UploadDir.Split(Path.DirectorySeparatorChar).Length;
        if (depth > ctx.MaxDirDepth)
            return Results.BadRequest("Upload path exceeds maximum directory depth.");

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

        // Parse Content-Range header if present
        var rangeHeader = httpCtx.Request.Headers.ContentRange.ToString();
        long rangeStart = 0, rangeEnd = -1, rangeTotal = -1;
        bool hasRange = false;

        if (!string.IsNullOrEmpty(rangeHeader))
        {
            var m = ContentRangeRegex().Match(rangeHeader);
            if (!m.Success)
                return Results.BadRequest("Invalid Content-Range header. Expected: bytes START-END/TOTAL");

            rangeStart = long.Parse(m.Groups[1].Value);
            rangeEnd   = long.Parse(m.Groups[2].Value);
            rangeTotal = long.Parse(m.Groups[3].Value);
            hasRange   = true;

            if (rangeStart > rangeEnd || rangeEnd >= rangeTotal)
                return Results.BadRequest("Invalid Content-Range values.");

            // Enforce per-file size limit against total size
            if (ctx.MaxFileSize > 0 && rangeTotal > ctx.MaxFileSize)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // For non-chunked stream uploads, enforce size limit against Content-Length
        if (!hasRange && ctx.MaxFileSize > 0 && httpCtx.Request.ContentLength > ctx.MaxFileSize)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // Determine the .part file path. For chunked uploads, use the original name
        // (not a unique-resolved name) so subsequent chunks find the same .part file.
        // Uniqueness is resolved at finalization time.
        var partDest = Path.Combine(resolved, safeName + ".part");

        // Path traversal check on the computed .part path
        if (!partDest.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var uploadId = httpCtx.Items.TryGetValue("fb.request.id", out var idObj) ? idObj as string : null;
        var uploadDirName = Path.GetFileName(ctx.UploadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var relPartDest   = "/" + uploadDirName + "/" + Path.GetRelativePath(ctx.UploadDir, partDest).Replace('\\', '/');
        ctx.DebugLog?.Invoke(uploadId ?? "?", relPartDest);

        // Enforce quota checks for the first chunk (rangeStart == 0) or non-chunked uploads
        if (!hasRange || rangeStart == 0)
        {
            var totalSize = hasRange ? rangeTotal : (httpCtx.Request.ContentLength ?? 0);
            var senderKey = ctx.ResolveSenderKey(httpCtx);

            if (ctx.MaxUploadBytesTotal > 0)
            {
                var dirBytes = HandlerContext.GetDirectorySize(ctx.UploadDir);
                if (dirBytes + totalSize > ctx.MaxUploadBytesTotal)
                    return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
            }

            if (ctx.MaxUploadBytesPerSender > 0)
            {
                var already = _senderQuotas.GetOrAdd(senderKey, 0L);
                if (already + totalSize > ctx.MaxUploadBytesPerSender)
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
        }

        // Validate that the .part file offset matches the expected range start
        if (hasRange && rangeStart > 0)
        {
            if (!File.Exists(partDest))
                return Results.Json(
                    new { error = "No partial upload found. Start from offset 0.", bytesReceived = 0L },
                    statusCode: StatusCodes.Status409Conflict);

            var existingSize = new FileInfo(partDest).Length;
            if (existingSize != rangeStart)
                return Results.Json(
                    new { error = "Offset mismatch.", bytesReceived = existingSize },
                    statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            // Append when resuming (rangeStart > 0), create otherwise
            var fileMode = hasRange && rangeStart > 0 ? FileMode.Open : FileMode.Create;
            await using var fs = new FileStream(partDest, fileMode, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            if (hasRange && rangeStart > 0)
                fs.Seek(0, SeekOrigin.End);

            await httpCtx.Request.Body.CopyToAsync(fs, httpCtx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Client cancelled — keep the .part file for resume (don't delete it)
            throw;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            // Client disconnected — keep the .part file for resume (don't delete it)
            throw;
        }

        // Check if the upload is complete
        var partSize = new FileInfo(partDest).Length;
        bool isComplete = hasRange ? (rangeEnd + 1 >= rangeTotal) : true;

        if (isComplete)
        {
            // Resolve a unique final name and promote the .part file
            var finalName = ResolveUniqueFileName(resolved, safeName);
            var dest      = Path.Combine(resolved, finalName);
            File.Move(partDest, dest);

            // Update per-sender quota
            var senderKey = ctx.ResolveSenderKey(httpCtx);
            if (ctx.MaxUploadBytesPerSender > 0)
                _senderQuotas.AddOrUpdate(senderKey, partSize, (_, existing) => existing + partSize);

            httpCtx.Items["fb.bytes"] = partSize;
            httpCtx.Items["fb.count"] = 1;
            httpCtx.Items["fb.file"]  = finalName;

            // For API clients (non-browser), return JSON. For browser clients, redirect.
            if (httpCtx.Request.Headers.Accept.ToString().Contains("application/json"))
            {
                return Results.Json(new { fileName = finalName, totalSize = partSize }, statusCode: StatusCodes.Status201Created);
            }

            var browseUrl = string.IsNullOrEmpty(redirectBase)
                ? (string.IsNullOrEmpty(subpath) ? "/" : $"/browse/{subpath}")
                : (string.IsNullOrEmpty(subpath) ? $"/{redirectBase}" : $"/{redirectBase}/browse/{subpath}");
            return Results.Redirect(browseUrl);
        }

        // Chunk accepted but upload not yet complete — return progress
        return Results.Json(new { bytesReceived = partSize, totalSize = rangeTotal }, statusCode: StatusCodes.Status200OK);
    }

    // HEAD /upload/{**subpath}?file=<name>  — returns bytes received for a partial .part file
    internal IResult UploadStatus(HttpContext httpCtx, string? subpath)
        => GetUploadStatus(httpCtx, subpath);

    // HEAD /upload-area/upload/{**subpath}?file=<name>
    internal IResult UploadAreaStatus(HttpContext httpCtx, string? subpath)
        => GetUploadStatus(httpCtx, subpath);

    // HEAD /my-uploads/upload/{**subpath}?file=<name>
    internal IResult MyUploadsStatus(HttpContext httpCtx, string? subpath)
        => GetUploadStatus(httpCtx, subpath);

    private IResult GetUploadStatus(HttpContext httpCtx, string? subpath)
    {
        var fileName = httpCtx.Request.Query["file"].ToString();
        if (string.IsNullOrWhiteSpace(fileName))
            return Results.BadRequest("Query parameter 'file' is required.");

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            return Results.BadRequest("Invalid filename.");

        var dropRoot = ctx.PerSender
            ? Path.Combine(ctx.UploadDir, ctx.ResolveSenderKey(httpCtx))
            : ctx.UploadDir;

        string resolved;
        try { resolved = ctx.SafeResolveUploadPath(subpath, dropRoot); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        var partPath = Path.Combine(resolved, safeName + ".part");

        // Path traversal check
        if (!partPath.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!File.Exists(partPath))
        {
            httpCtx.Response.Headers["X-Bytes-Received"] = "0";
            return Results.NotFound();
        }

        var size = new FileInfo(partPath).Length;
        httpCtx.Response.Headers["X-Bytes-Received"] = size.ToString();
        return Results.Ok();
    }

    // GET /disk-space  — JSON with available/total bytes for the upload drive,
    // capped by --max-upload-total and/or --max-upload-bytes-per-sender when set.
    internal IResult DiskSpace(HttpContext httpCtx)
    {
        long? driveAvailable = null;
        long? driveTotal     = null;

        try
        {
            var root  = Path.GetPathRoot(ctx.UploadDir) ?? ctx.UploadDir;
            var drive = new DriveInfo(root);
            driveAvailable = drive.AvailableFreeSpace;
            driveTotal     = drive.TotalSize;
        }
        catch { /* network or virtual drive — skip */ }

        long? available = driveAvailable;
        long? total     = driveTotal;

        if (ctx.MaxUploadBytesTotal > 0)
        {
            var now = Environment.TickCount64;
            if (now - ctx.DirSizeTicks > 10_000)
            {
                ctx.DirSizeCached = HandlerContext.GetDirectorySize(ctx.UploadDir);
                ctx.DirSizeTicks  = now;
            }
            var remaining = Math.Max(0L, ctx.MaxUploadBytesTotal - ctx.DirSizeCached);
            available = available.HasValue ? Math.Min(available.Value, remaining) : remaining;
            total     = total.HasValue    ? Math.Min(total.Value, ctx.MaxUploadBytesTotal) : ctx.MaxUploadBytesTotal;
        }

        if (ctx.MaxUploadBytesPerSender > 0)
        {
            var senderKey = ctx.ResolveSenderKey(httpCtx);
            var already   = _senderQuotas.GetOrAdd(senderKey, 0L);
            var remaining = Math.Max(0L, ctx.MaxUploadBytesPerSender - already);
            available = available.HasValue ? Math.Min(available.Value, remaining) : remaining;
            total     = total.HasValue    ? Math.Min(total.Value, ctx.MaxUploadBytesPerSender) : ctx.MaxUploadBytesPerSender;
        }

        if (!available.HasValue)
            return Results.NoContent();

        return Results.Json(new { availableBytes = available.Value, totalBytes = total!.Value });
    }
}
