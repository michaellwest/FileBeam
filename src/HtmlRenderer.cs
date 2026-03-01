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
        string order = "asc")
    {
        var segments      = relPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var uploadSection = isReadOnly ? "" : BuildUploadSection(segments);

        return ResourceLoader.Template
            .Replace("{{PAGE_TITLE}}",      HttpUtility.HtmlEncode(relPath))
            .Replace("{{BREADCRUMB}}",      BuildBreadcrumb(segments))
            .Replace("{{THEAD}}",           BuildTHead(sort, order))
            .Replace("{{ROWS}}",            BuildRows(segments, dirs, files))
            .Replace("{{UPLOAD_SECTION}}", uploadSection)
            .Replace("{{CSRF_TOKEN}}",      HttpUtility.HtmlAttributeEncode(csrfToken))
            .Replace("{{APP_JS}}",          ResourceLoader.AppJs);
    }

    private static string BuildTHead(string sort, string order)
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

    private static string BuildUploadSection(string[] segments)
    {
        var uploadAction = segments.Length == 0
            ? "/upload/"
            : "/upload/" + string.Join("/", segments.Select(Uri.EscapeDataString));

        var mkdirBase = System.Web.HttpUtility.JavaScriptStringEncode(
            segments.Length == 0 ? "" : string.Join("/", segments.Select(Uri.EscapeDataString)));

        return $"""
            <div class="upload-section">
              <h2>Upload files to this folder</h2>
              <form id="upload-form" method="post" action="{uploadAction}" enctype="multipart/form-data">
                <div class="drop-zone" id="drop-zone">
                  <div>Click or drag &amp; drop files here</div>
                  <div class="hint">Multiple files supported</div>
                  <input type="file" name="files" multiple id="file-input">
                </div>
                <div id="upload-queue" hidden></div>
                <button type="submit" class="btn">Upload</button>
              </form>
              <div style="margin-top:0.75rem">
                <button class="btn btn-secondary" onclick="fbMkDir('{mkdirBase}')">📁 New Folder</button>
              </div>
            </div>
            """;
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    private static string BuildRows(
        string[] segments,
        List<DirectoryInfo> dirs,
        List<FileInfo> files)
    {
        var sb = new StringBuilder();

        // Parent directory link
        if (segments.Length > 0)
        {
            var parentPath = segments.Length == 1
                ? "/"
                : "/browse/" + string.Join("/", segments[..^1].Select(Uri.EscapeDataString));
            sb.AppendLine($"""
                    <tr>
                      <td colspan="4"><a href="{parentPath}" class="name"><span class="icon">📁</span> ..</a></td>
                    </tr>
                """);
        }

        // Subdirectories
        foreach (var dir in dirs)
        {
            var href   = "/browse/"       + UrlPath(segments, dir.Name);
            var zipUrl = "/download-zip/" + UrlPath(segments, dir.Name);
            var name   = HttpUtility.HtmlEncode(dir.Name);
            var modif  = dir.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            sb.AppendLine($"""
                    <tr>
                      <td><a href="{href}" class="name"><span class="icon">📁</span>{name}/</a></td>
                      <td class="size">—</td>
                      <td class="modified">{modif}</td>
                      <td class="actions"><a href="{zipUrl}" class="act-btn" title="Download as ZIP">⬇️</a></td>
                    </tr>
                """);
        }

        // Files
        foreach (var file in files)
        {
            var href      = "/download/" + UrlPath(segments, file.Name);
            var deleteUrl = "/delete/"   + UrlPath(segments, file.Name);
            var renameUrl = "/rename/"   + UrlPath(segments, file.Name);
            var shareUrl  = "/share/"    + UrlPath(segments, file.Name);
            var name      = HttpUtility.HtmlEncode(file.Name);
            var nameJs    = HttpUtility.JavaScriptStringEncode(file.Name);
            var size      = FormatSize(file.Length);
            var modif     = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var icon      = FileIcon(file.Extension);
            sb.AppendLine($"""
                    <tr>
                      <td><a href="{href}" class="name"><span class="icon">{icon}</span>{name}</a></td>
                      <td class="size">{size}</td>
                      <td class="modified">{modif}</td>
                      <td class="actions">
                        <button class="act-btn" title="Share link" onclick="fbShare('{shareUrl}')">🔗</button>
                        <button class="act-btn" title="Rename" onclick="fbRename('{renameUrl}','{nameJs}')">✏️</button>
                        <button class="act-btn" title="Delete" onclick="fbDelete('{deleteUrl}','{name}')">🗑️</button>
                      </td>
                    </tr>
                """);
        }

        if (dirs.Count == 0 && files.Count == 0)
            sb.AppendLine("""    <tr><td colspan="4" class="empty">This folder is empty.</td></tr>""");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildBreadcrumb(string[] segments)
    {
        var sb = new StringBuilder();
        sb.Append("<a href=\"/\">root</a>");
        for (int i = 0; i < segments.Length; i++)
        {
            sb.Append(" / ");
            if (i == segments.Length - 1)
                sb.Append($"<span>{HttpUtility.HtmlEncode(segments[i])}</span>");
            else
            {
                var href = "/browse/" + string.Join("/", segments[..(i + 1)].Select(Uri.EscapeDataString));
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
