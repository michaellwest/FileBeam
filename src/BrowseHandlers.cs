using Microsoft.AspNetCore.Http;

namespace FileBeam;

/// <summary>
/// Handles directory listing endpoints: main browse, upload-area, my-uploads, and admin-uploads.
/// </summary>
internal sealed class BrowseHandlers(HandlerContext ctx)
{
    // GET /
    internal IResult ListDirectory(HttpContext httpCtx)
        => Browse(httpCtx, null);

    // GET /browse/{**subpath}
    internal IResult BrowseDirectory(HttpContext httpCtx, string? subpath)
        => Browse(httpCtx, subpath);

    private IResult Browse(HttpContext httpCtx, string? subpath)
    {
        var role = HandlerContext.GetRole(httpCtx);

        // Write-only users: redirect to /my-uploads when per-sender is active so they
        // can see their own files; otherwise show the plain upload drop zone.
        if (role == "wo")
        {
            bool separateUploadDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
            if (ctx.PerSender && separateUploadDir)
                return Results.Redirect("/my-uploads");

            var woHtml = HtmlRenderer.RenderDirectory("", [], [], ctx.IsReadOnly, ctx.CsrfToken, "name", "asc", role,
                separateUploadDir: separateUploadDir);
            return Results.Content(woHtml, "text/html");
        }

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(ctx.RootDir, resolved);
        if (relPath == ".") relPath = "";

        var sort  = httpCtx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = httpCtx.Request.Query["order"].FirstOrDefault() ?? "asc";
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

        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        bool hasConfig = role == "admin" && ctx.ConfigJson.Length > 0;
        var navLinks = HtmlRenderer.BuildNavLinks(role, ctx.PerSender, separateDir, isReadOnly: ctx.IsReadOnly, hasInvites: ctx.InviteStore is not null, hasConfig: hasConfig, hasAuditLog: ctx.HasAuditLog, hasSessions: ctx.HasSessions);
        var adminModal = hasConfig ? HtmlRenderer.BuildAdminConfigModal(ctx.ConfigJson, ctx.CliCommand) : "";
        var html = HtmlRenderer.RenderDirectory(relPath, dirs.ToList(), files.ToList(), ctx.IsReadOnly, ctx.CsrfToken, sort, order, role,
            separateUploadDir: separateDir, navLinks: navLinks, perSender: ctx.PerSender, adminConfigModal: adminModal);
        return Results.Content(html, "text/html");
    }

    // GET /upload-area  and  GET /upload-area/browse/{**subpath}
    // Shared upload page when upload dir is separate and --per-sender is off.
    // rw and admin users land here after following the nav link or notice on the browse screen.
    internal IResult BrowseUploadArea(HttpContext httpCtx, string? subpath = null)
    {
        var role = HandlerContext.GetRole(httpCtx);

        // Redirect roles that cannot or should not use this page
        if (ctx.IsReadOnly || role == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        if (!separateDir)
            return Results.Redirect("/");  // same dir — upload is already on the browse screen

        if (ctx.PerSender)
            return Results.Redirect("/my-uploads");  // per-sender users have their own upload view

        if (role == "wo")
            return Results.Redirect("/");  // wo always shows drop zone on browse

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

        var relPath = Path.GetRelativePath(ctx.UploadDir, resolved).Replace('\\', '/');
        if (relPath == ".") relPath = "";

        var sort  = httpCtx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = httpCtx.Request.Query["order"].FirstOrDefault() ?? "asc";
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

        var navLinks = HtmlRenderer.BuildNavLinks(role, ctx.PerSender, separateDir, showHome: true, isReadOnly: ctx.IsReadOnly, hasInvites: ctx.InviteStore is not null, hasSessions: ctx.HasSessions);
        // Pass separateUploadDir:false so the upload form is rendered (files DO land in uploadDir here)
        var html = HtmlRenderer.RenderDirectory(relPath, dirs.ToList(), files.ToList(), ctx.IsReadOnly, ctx.CsrfToken, sort, order, role,
            separateUploadDir: false, urlBase: "upload-area", navLinks: navLinks, perSender: ctx.PerSender, uploadTtl: ctx.UploadTtl);
        return Results.Content(html, "text/html");
    }

    // GET /my-uploads  and  GET /my-uploads/browse/{**subpath}
    // Scoped to the authenticated sender's subfolder inside uploadDir.
    // Requires --per-sender; returns 404 otherwise (no user-specific folder exists).
    internal IResult BrowseMyUploads(HttpContext httpCtx, string? subpath = null)
    {
        if (!ctx.PerSender)
            return Results.NotFound("My Uploads requires --per-sender mode.");

        var role = HandlerContext.GetRole(httpCtx);
        if (role == "ro")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var senderKey  = ctx.ResolveSenderKey(httpCtx);
        var senderRoot = Path.Combine(ctx.UploadDir, senderKey);

        // Create the sender's subfolder on first visit so the listing renders
        if (!Directory.Exists(senderRoot))
            Directory.CreateDirectory(senderRoot);

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

        var relPath = Path.GetRelativePath(senderRoot, resolved).Replace('\\', '/');
        if (relPath == ".") relPath = "";

        var sort  = httpCtx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = httpCtx.Request.Query["order"].FirstOrDefault() ?? "asc";
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

        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks(role, ctx.PerSender, separateDir, showHome: true, hasInvites: ctx.InviteStore is not null, hasSessions: ctx.HasSessions);
        // Admin's own view: their entire subfolder is exempt from expiry
        var myAdminExemptPath = (ctx.PerSender && role == "admin") ? ctx.AdminExemptPath : null;
        var html = HtmlRenderer.RenderDirectory(
            relPath, dirs.ToList(), files.ToList(),
            ctx.IsReadOnly, ctx.CsrfToken, sort, order, role,
            separateUploadDir: false,  // files uploaded here DO appear in this view
            urlBase: "my-uploads",
            navLinks: navLinks,
            uploadTtl: ctx.UploadTtl,
            adminExemptPath: myAdminExemptPath);
        return Results.Content(html, "text/html");
    }

    // GET /admin/uploads  and  GET /admin/uploads/browse/{**subpath}
    // Read-only browse of the full upload directory. Admin only.
    internal IResult BrowseAdminUploads(HttpContext httpCtx, string? subpath = null)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
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
                ? ctx.UploadDir
                : Path.GetFullPath(Path.Combine(ctx.UploadDir, subpath));

            if (!resolved.StartsWith(ctx.UploadDir, StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!Directory.Exists(resolved))
            return Results.NotFound("Directory not found.");

        var relPath = Path.GetRelativePath(ctx.UploadDir, resolved).Replace('\\', '/');
        if (relPath == ".") relPath = "";

        var sort  = httpCtx.Request.Query["sort"].FirstOrDefault() ?? "name";
        var order = httpCtx.Request.Query["order"].FirstOrDefault() ?? "asc";
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

        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks("admin", ctx.PerSender, separateDir, showHome: true, hasInvites: ctx.InviteStore is not null, hasAuditLog: ctx.HasAuditLog, hasSessions: ctx.HasSessions);
        var html = HtmlRenderer.RenderDirectory(
            relPath, dirs.ToList(), files.ToList(),
            isReadOnly: true,
            ctx.CsrfToken, sort, order, role: "admin",
            separateUploadDir: false,
            urlBase: "admin/uploads",
            navLinks: navLinks,
            uploadTtl: ctx.UploadTtl,
            adminExemptPath: ctx.AdminExemptPath);
        return Results.Content(html, "text/html");
    }
}
