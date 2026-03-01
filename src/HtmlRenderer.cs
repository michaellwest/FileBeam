using System.Text;
using System.Web;

namespace FileBeam;

public static class HtmlRenderer
{
    public static string RenderDirectory(
        string relPath,
        List<DirectoryInfo> dirs,
        List<FileInfo> files,
        bool isReadOnly = false,
        string csrfToken = "",
        string sort = "name",
        string order = "asc",
        string role = "rw",
        bool separateUploadDir = false,
        string urlBase = "",
        string navLinks = "")
    {
        var segments = relPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        // wo = upload-only: show drop zone at root, no listing.
        // Uploads always target root regardless of which URL was visited.
        var isWo      = role == "wo";
        var isRo      = role == "ro";
        var canUpload = !isReadOnly && !isRo;
        var isAdmin   = role == "admin";
        var uploadSegs = isWo ? [] : segments;   // wo users always upload to root

        string uploadSection;
        if (canUpload)
            uploadSection = BuildUploadSection(uploadSegs, isAdmin, isWo, separateUploadDir);
        else if (isRo)
            uploadSection = BuildRoNotice();
        else
            uploadSection = "";  // global --readonly

        var bodyClass = isWo ? "role-wo" : "";

        return ResourceLoader.Template
            .Replace("{{PAGE_TITLE}}",      HttpUtility.HtmlEncode(relPath))
            .Replace("{{BODY_CLASS}}",      bodyClass)
            .Replace("{{NAV_LINKS}}",       navLinks)
            .Replace("{{BREADCRUMB}}",      BuildBreadcrumb(segments, urlBase))
            .Replace("{{THEAD}}",           BuildTHead(sort, order, urlBase))
            .Replace("{{ROWS}}",            BuildRows(segments, dirs, files, isAdmin, urlBase))
            .Replace("{{UPLOAD_SECTION}}", uploadSection)
            .Replace("{{CSRF_TOKEN}}",      HttpUtility.HtmlAttributeEncode(csrfToken))
            .Replace("{{APP_JS}}",          ResourceLoader.AppJs);
    }

    /// <summary>
    /// Builds nav link HTML for "Home", "My Uploads", and/or "All Uploads" based on role and config.
    /// Pass <paramref name="showHome"/> = true when rendering an alternate view (my-uploads, admin/uploads)
    /// so users can navigate back to the main file listing.
    /// </summary>
    public static string BuildNavLinks(string role, bool perSender, bool separateUploadDir, bool showHome = false)
    {
        const string sep  = """<span style="color:#444">·</span>""";
        const string style = "font-size:0.82rem;color:#aaa;white-space:nowrap";
        var sb = new StringBuilder();

        if (showHome)
            sb.Append($"""<a href="/" style="{style}">Home</a>""");

        if (separateUploadDir && perSender && role != "ro")
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/my-uploads" style="{style}">My&nbsp;Uploads</a>""");
        }
        if (role == "admin" && separateUploadDir)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/admin/uploads" style="{style}">All&nbsp;Uploads</a>""");
        }
        return sb.ToString();
    }

    private static string BuildTHead(string sort, string order, string urlBase = "")
    {
        string SortLink(string col, string label)
        {
            var isCurrent = sort == col;
            var nextOrder = isCurrent && order == "asc" ? "desc" : "asc";
            var indicator = isCurrent ? (order == "asc" ? " ▲" : " ▼") : "";
            return $"""<a href="?sort={col}&amp;order={nextOrder}" style="color:inherit;text-decoration:none">{label}{indicator}</a>""";
        }
        return $"""
        <tr>
          <th>{SortLink("name", "Name")}</th>
          <th>{SortLink("size", "Size")}</th>
          <th>{SortLink("date", "Modified")}</th>
          <th></th>
        </tr>
        """;
    }

    private static string BuildUploadSection(
        string[] segments,
        bool isAdmin = false,
        bool isWo = false,
        bool separateUploadDir = false)
    {
        var uploadAction = segments.Length == 0
            ? "/upload/"
            : "/upload/" + string.Join("/", segments.Select(Uri.EscapeDataString));

        var mkdirBase = System.Web.HttpUtility.JavaScriptStringEncode(
            segments.Length == 0 ? "" : string.Join("/", segments.Select(Uri.EscapeDataString)));

        var mkdirButton = isAdmin
            ? $"""
              <div style="margin-top:0.75rem">
                <button class="btn btn-secondary" onclick="fbMkDir('{mkdirBase}')">📁 New Folder</button>
              </div>
              """
            : "";

        var heading = isWo ? "Upload files" : "Upload files to this folder";

        // Contextual notice shown above the form (for wo) or below the button (separate upload dir)
        var topNotice = isWo
            ? BuildInfoNotice("You have upload-only access. File browsing is not available for your account.")
            : "";

        var bottomNotice = !isWo && separateUploadDir
            ? BuildInfoNotice("Uploaded files go to a private storage area and will not appear in this listing. Once uploaded, they cannot be managed from here.")
            : "";

        return $"""
            <div class="upload-section">
              <h2>{heading}</h2>
              {topNotice}
              <form id="upload-form" method="post" action="{uploadAction}" enctype="multipart/form-data">
                <div class="drop-zone" id="drop-zone">
                  <div>Click or drag &amp; drop files here</div>
                  <div class="hint">Multiple files supported &middot; Ctrl+V to paste an image</div>
                  <input type="file" name="files" multiple id="file-input">
                </div>
                <div id="upload-queue" hidden></div>
                <button type="submit" class="btn">Upload</button>
              </form>
              {bottomNotice}
              {mkdirButton}
            </div>
            """;
    }

    private static string BuildInfoNotice(string text) =>
        $"""<div style="margin-top:0.75rem;padding:0.65rem 0.9rem;background:#1a2535;border-left:3px solid #5ba4f5;border-radius:4px;font-size:0.82rem;color:#aaa">{text}</div>""";

    private static string BuildRoNotice() =>
        """
            <div class="upload-section">
              <p style="font-size:0.85rem;color:#888;margin:0">Read-only access — uploads are disabled for your account.</p>
            </div>
        """;

    // ── Row builders ──────────────────────────────────────────────────────────

    private static string BuildRows(
        string[] segments,
        List<DirectoryInfo> dirs,
        List<FileInfo> files,
        bool isAdmin = false,
        string urlBase = "")
    {
        // Determine URL prefixes based on the view root
        var browsePrefix   = string.IsNullOrEmpty(urlBase) ? "/browse/"       : $"/{urlBase}/browse/";
        var downloadPrefix = string.IsNullOrEmpty(urlBase) ? "/download/"     : $"/{urlBase}/download/";
        var zipPrefix      = string.IsNullOrEmpty(urlBase) ? "/download-zip/" : "/download-zip/";
        var rootHref       = string.IsNullOrEmpty(urlBase) ? "/"              : $"/{urlBase}";

        var isMyUploads    = urlBase == "my-uploads";
        var isAdminUploads = urlBase == "admin/uploads";

        var sb = new StringBuilder();

        // Parent directory link
        if (segments.Length > 0)
        {
            var parentPath = segments.Length == 1
                ? rootHref
                : browsePrefix + string.Join("/", segments[..^1].Select(Uri.EscapeDataString));
            sb.AppendLine($"""
                    <tr>
                      <td colspan="4"><a href="{parentPath}" class="name"><span class="icon">📁</span> ..</a></td>
                    </tr>
                """);
        }

        // Subdirectories
        foreach (var dir in dirs)
        {
            var href      = browsePrefix + UrlPath(segments, dir.Name);
            var zipUrl    = zipPrefix    + UrlPath(segments, dir.Name);
            var delDirUrl    = "/delete-dir/" + UrlPath(segments, dir.Name);
            var renameDirUrl = "/rename-dir/" + UrlPath(segments, dir.Name);
            var name      = HttpUtility.HtmlEncode(dir.Name);
            var nameJs    = HttpUtility.JavaScriptStringEncode(dir.Name);
            var modif     = dir.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var adminDirBtns = isAdmin && !isMyUploads && !isAdminUploads
                ? $"""
                    <button class="act-btn" title="Rename folder" onclick="fbRename('{renameDirUrl}','{nameJs}')">✏️</button>
                    <button class="act-btn" title="Delete folder" onclick="fbDelete('{delDirUrl}','{nameJs}/')">🗑️</button>
                  """
                : "";
            sb.AppendLine($"""
                    <tr>
                      <td><a href="{href}" class="name"><span class="icon">📁</span>{name}/</a></td>
                      <td class="size">—</td>
                      <td class="modified">{modif}</td>
                      <td class="actions"><a href="{zipUrl}" class="act-btn" title="Download as ZIP">⬇️</a>{adminDirBtns}</td>
                    </tr>
                """);
        }

        // Files
        foreach (var file in files)
        {
            var filePath  = UrlPath(segments, file.Name);
            var href      = downloadPrefix + filePath;
            var deleteUrl = "/delete/"     + filePath;
            var renameUrl = "/rename/"     + filePath;
            var shareUrl  = "/share/"      + filePath;
            var infoUrl   = "/my-uploads/info/" + filePath;
            var myDelUrl  = "/my-uploads/delete/" + filePath;
            var admDelUrl = "/admin/uploads/delete/" + filePath;
            var name      = HttpUtility.HtmlEncode(file.Name);
            var nameJs    = HttpUtility.JavaScriptStringEncode(file.Name);
            var extJs     = HttpUtility.JavaScriptStringEncode(file.Extension.ToLowerInvariant());
            var size      = FormatSize(file.Length);
            var modif     = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var icon      = FileIcon(file.Extension);

            string nameCell;
            string actionsCell;

            if (isMyUploads)
            {
                nameCell = $"""<span class="name" style="cursor:pointer;color:#5ba4f5" onclick="fbInfo('{infoUrl}','{nameJs}')"><span class="icon">{icon}</span>{name}</span>""";
                actionsCell = $"""
                        <button class="act-btn" title="File info" onclick="fbInfo('{infoUrl}','{nameJs}')">ℹ️</button>
                        <button class="act-btn" title="Delete" onclick="fbDelete('{myDelUrl}','{name}')">🗑️</button>
                  """;
            }
            else if (isAdminUploads)
            {
                nameCell = $"""<span class="name"><span class="icon">{icon}</span>{name}</span>""";
                actionsCell = $"""
                        <button class="act-btn" title="Delete" onclick="fbDelete('{admDelUrl}','{name}')">🗑️</button>
                  """;
            }
            else
            {
                nameCell = $"""<a href="{href}" class="name"><span class="icon">{icon}</span>{name}</a>""";
                // Admin mutation buttons only shown in the main browse view
                var adminButtons = isAdmin
                    ? $"""
                        <button class="act-btn" title="Share link" onclick="fbShare('{shareUrl}')">🔗</button>
                        <button class="act-btn" title="Rename" onclick="fbRename('{renameUrl}','{nameJs}')">✏️</button>
                        <button class="act-btn" title="Delete" onclick="fbDelete('{deleteUrl}','{name}')">🗑️</button>
                      """
                    : "";
                actionsCell = $"""
                        <button class="act-btn" title="Preview" onclick="fbPreview('{href}','{nameJs}','{extJs}')">👁️</button>
                        {adminButtons}
                  """;
            }

            sb.AppendLine($"""
                    <tr>
                      <td>{nameCell}</td>
                      <td class="size">{size}</td>
                      <td class="modified">{modif}</td>
                      <td class="actions">
                        {actionsCell}
                      </td>
                    </tr>
                """);
        }

        if (dirs.Count == 0 && files.Count == 0)
            sb.AppendLine("""    <tr><td colspan="4" class="empty">This folder is empty.</td></tr>""");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildBreadcrumb(string[] segments, string urlBase = "")
    {
        var rootHref     = string.IsNullOrEmpty(urlBase) ? "/"         : $"/{urlBase}";
        var rootLabel    = string.IsNullOrEmpty(urlBase) ? "root"      : urlBase.Split('/')[^1].Replace("-", " ");
        var browsePrefix = string.IsNullOrEmpty(urlBase) ? "/browse/"  : $"/{urlBase}/browse/";

        var sb = new StringBuilder();
        sb.Append($"<a href=\"{rootHref}\">{rootLabel}</a>");
        for (int i = 0; i < segments.Length; i++)
        {
            sb.Append(" / ");
            if (i == segments.Length - 1)
                sb.Append($"<span>{HttpUtility.HtmlEncode(segments[i])}</span>");
            else
            {
                var href = browsePrefix + string.Join("/", segments[..(i + 1)].Select(Uri.EscapeDataString));
                sb.Append($"<a href=\"{href}\">{HttpUtility.HtmlEncode(segments[i])}</a>");
            }
        }
        return sb.ToString();
    }

    private static string UrlPath(string[] segments, string name)
    {
        var parts = segments.Length == 0
            ? new[] { name }
            : segments.Append(name).ToArray();
        return string.Join("/", parts.Select(Uri.EscapeDataString));
    }

    internal static string FormatSizePublic(long bytes) => FormatSize(bytes);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _                    => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };

    private static string FileIcon(string ext) => ext.ToLowerInvariant() switch
    {
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz"      => "🗜️",
        ".pdf"                                              => "📄",
        ".doc" or ".docx"                                  => "📝",
        ".xls" or ".xlsx"                                  => "📊",
        ".ppt" or ".pptx"                                  => "📑",
        ".mp4" or ".mkv" or ".avi" or ".mov"               => "🎬",
        ".mp3" or ".wav" or ".flac" or ".aac"              => "🎵",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp"   => "🖼️",
        ".exe" or ".msi"                                   => "⚙️",
        ".txt" or ".md" or ".log"                          => "📃",
        ".cs" or ".py" or ".js" or ".ts" or ".json"        => "💻",
        _                                                  => "📦"
    };
}
