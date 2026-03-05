using Microsoft.AspNetCore.Http;

namespace FileBeam;

/// <summary>
/// Handles file and directory modification endpoints: delete, rename, mkdir
/// for the main browse area, my-uploads, and admin-uploads.
/// </summary>
internal sealed class ModifyHandlers(HandlerContext ctx)
{
    // POST /delete/{**subpath}
    internal IResult DeleteFile(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        File.Delete(resolved);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var browseUrl = string.IsNullOrEmpty(parentSubpath) ? "/" : $"/browse/{parentSubpath}";
        return Results.Redirect(browseUrl);
    }

    // POST /delete-dir/{**subpath}
    internal IResult DeleteDir(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (string.Equals(resolved, ctx.RootDir, StringComparison.OrdinalIgnoreCase))
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

    // POST /rename/{**subpath}
    internal async Task<IResult> RenameFile(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var form    = await httpCtx.Request.ReadFormAsync();
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

    // POST /rename-dir/{**subpath}
    internal async Task<IResult> RenameDir(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (string.Equals(resolved, ctx.RootDir, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Cannot rename the root directory.");

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var form    = await httpCtx.Request.ReadFormAsync();
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

    // POST /mkdir/{**subpath}
    internal IResult MkDir(HttpContext httpCtx, string? subpath)
    {
        if (ctx.IsReadOnly)
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);

        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder name specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (Directory.Exists(resolved))
            return Results.Conflict("A directory with that name already exists.");

        var parent = Path.GetDirectoryName(resolved);
        if (parent is null || !Directory.Exists(parent))
            return Results.NotFound("Parent directory not found.");

        Directory.CreateDirectory(resolved);

        var relPath = Path.GetRelativePath(ctx.RootDir, resolved).Replace('\\', '/');
        return Results.Redirect($"/browse/{relPath}");
    }

    // POST /my-uploads/delete/{**subpath}
    // Deletes a file from the sender's own subfolder; redirects to parent in /my-uploads.
    internal IResult DeleteMyUpload(HttpContext httpCtx, string? subpath)
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

        File.Delete(resolved);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/my-uploads"
            : $"/my-uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // POST /my-uploads/rename/{**subpath}
    // Renames a file in the sender's own subfolder; redirects to parent in /my-uploads.
    internal async Task<IResult> RenameMyUpload(HttpContext httpCtx, string? subpath)
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

        var form    = await httpCtx.Request.ReadFormAsync();
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
    internal async Task<IResult> RenameMyUploadDir(HttpContext httpCtx, string? subpath)
    {
        if (!ctx.PerSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        if (HandlerContext.GetRole(httpCtx) == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

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

        if (string.Equals(resolved, senderRoot, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Cannot rename your own root folder.");

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var form    = await httpCtx.Request.ReadFormAsync();
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
    internal IResult DeleteAdminUpload(HttpContext httpCtx, string? subpath)
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

        File.Delete(resolved);

        var parentSubpath = Path.GetDirectoryName(subpath)?.Replace('\\', '/');
        var redirectUrl   = string.IsNullOrEmpty(parentSubpath)
            ? "/admin/uploads"
            : $"/admin/uploads/browse/{parentSubpath}";
        return Results.Redirect(redirectUrl);
    }

    // POST /admin/uploads/rename/{**subpath}
    // Renames a file in the upload directory; redirects to parent in /admin/uploads. Admin only.
    internal async Task<IResult> RenameAdminUpload(HttpContext httpCtx, string? subpath)
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

        var form    = await httpCtx.Request.ReadFormAsync();
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
    internal async Task<IResult> RenameAdminUploadDir(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No folder specified.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(ctx.UploadDir, subpath));
            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var form    = await httpCtx.Request.ReadFormAsync();
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
}
