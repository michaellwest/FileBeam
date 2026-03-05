using System.Text;
using System.Web;

namespace FileBeam;

public record AuditEntry(
    string  Timestamp,
    string? Username,
    string  RemoteIp,
    string  Action,
    string  Path,
    long    Bytes,
    int     StatusCode);

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
        string navLinks = "",
        bool perSender = false,
        string adminConfigModal = "",
        TimeSpan? uploadTtl = null,
        string? adminExemptPath = null,
        string? autoBearerToken = null)
    {
        var segments = relPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        // wo = upload-only: show drop zone at root, no listing.
        // Uploads always target root regardless of which URL was visited.
        var isWo      = role == "wo";
        var isRo      = role == "ro";
        var canUpload = !isReadOnly && !isRo;
        var isAdmin   = role == "admin";
        var uploadSegs = isWo ? [] : segments;   // wo users always upload to root

        // When upload dir is separate, the upload form belongs on /my-uploads (perSender)
        // or /upload-area (!perSender), not on the browse screen.
        // Exception: wo users always see the drop zone here (their only interface).
        var showUploadHere = canUpload && (!separateUploadDir || isWo);

        string uploadSection;
        if (showUploadHere)
            uploadSection = BuildUploadSection(uploadSegs, isAdmin, isWo, urlBase);
        else if (canUpload && separateUploadDir)
            uploadSection = BuildUploadRedirectNotice(perSender);
        else if (isRo)
            uploadSection = BuildRoNotice();
        else
            uploadSection = "";  // global --readonly

        var bodyClass = isWo ? "role-wo" : "";

        var showExpiry = uploadTtl.HasValue;
        return ResourceLoader.Template
            .Replace("{{PAGE_TITLE}}",      HttpUtility.HtmlEncode(relPath))
            .Replace("{{BODY_CLASS}}",      bodyClass)
            .Replace("{{NAV_LINKS}}",       navLinks)
            .Replace("{{BREADCRUMB}}",      BuildBreadcrumb(segments, urlBase))
            .Replace("{{THEAD}}",           BuildTHead(sort, order, urlBase, showExpiry))
            .Replace("{{ROWS}}",            BuildRows(segments, dirs, files, isAdmin, urlBase, uploadTtl, adminExemptPath))
            .Replace("{{UPLOAD_SECTION}}", uploadSection)
            .Replace("{{CSRF_TOKEN}}",      HttpUtility.HtmlAttributeEncode(csrfToken))
            .Replace("{{ADMIN_CONFIG_MODAL}}", adminConfigModal)
            .Replace("{{APP_JS}}",          ResourceLoader.AppJs)
            .Replace("{{EXPIRY_JS}}",       showExpiry ? BuildExpiryJs() : "")
            .Replace("{{FB_BEARER_META}}",  BuildBearerMeta(autoBearerToken));
    }

    /// <summary>
    /// Builds nav link HTML for "Home", "My Uploads", and/or "All Uploads" based on role and config.
    /// Pass <paramref name="showHome"/> = true when rendering an alternate view (my-uploads, admin/uploads)
    /// so users can navigate back to the main file listing.
    /// </summary>
    public static string BuildNavLinks(string role, bool perSender, bool separateUploadDir, bool showHome = false, bool isReadOnly = false, bool hasInvites = false, bool hasConfig = false, bool hasAuditLog = false, bool hasSessions = false, bool hasQr = false)
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
        // When upload dir is separate but no per-sender bucketing, expose a shared upload area
        // for users who can upload (rw, admin — not ro or wo).
        if (separateUploadDir && !perSender && !isReadOnly && role != "ro" && role != "wo")
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/upload-area" style="{style}">Upload</a>""");
        }
        if (role == "admin" && separateUploadDir)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/admin/uploads" style="{style}">All&nbsp;Uploads</a>""");
        }
        if (role == "admin" && hasInvites)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/admin/invites" style="{style}">Invites</a>""");
        }
        if (role == "admin" && hasConfig)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="#" style="{style}" onclick="openConfigModal();return false;">Config</a>""");
        }
        if (role == "admin" && hasAuditLog)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/admin/audit" style="{style}">Audit&nbsp;Log</a>""");
        }
        if (role == "admin" && hasSessions)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/admin/sessions" style="{style}">Sessions</a>""");
        }
        if (role == "admin" && hasQr)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append($"""<a href="/admin/qr" style="{style}">Admin&nbsp;QR</a>""");
        }
        return sb.ToString();
    }

    private static string BuildTHead(string sort, string order, string urlBase = "", bool showExpiry = false)
    {
        string SortLink(string col, string label)
        {
            var isCurrent = sort == col;
            var nextOrder = isCurrent && order == "asc" ? "desc" : "asc";
            var indicator = isCurrent ? (order == "asc" ? " ▲" : " ▼") : "";
            return $"""<a href="?sort={col}&amp;order={nextOrder}" style="color:inherit;text-decoration:none">{label}{indicator}</a>""";
        }
        var expiryTh = showExpiry ? "<th>Expires</th>" : "";
        return $"""
        <tr>
          <th>{SortLink("name", "Name")}</th>
          <th>{SortLink("size", "Size")}</th>
          <th>{SortLink("date", "Modified")}</th>
          {expiryTh}
          <th></th>
        </tr>
        """;
    }

    private static string BuildUploadSection(
        string[] segments,
        bool isAdmin = false,
        bool isWo = false,
        string urlBase = "")
    {
        // Use /{urlBase}/upload/{path} when rendering inside a scoped view (e.g. my-uploads)
        // so the handler can redirect back to the correct listing after upload.
        var uploadPrefix = string.IsNullOrEmpty(urlBase) ? "/upload/" : $"/{urlBase}/upload/";
        var uploadAction = segments.Length == 0
            ? uploadPrefix
            : uploadPrefix + string.Join("/", segments.Select(Uri.EscapeDataString));

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

        var topNotice = isWo
            ? BuildInfoNotice("You have upload-only access. File browsing is not available for your account.")
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
              {mkdirButton}
            </div>
            """;
    }

    private static string BuildUploadRedirectNotice(bool perSender)
    {
        var (href, label) = perSender
            ? ("/my-uploads", "My&nbsp;Uploads")
            : ("/upload-area", "Upload&nbsp;Area");
        return $"""
            <div class="upload-section">
              <p style="font-size:0.85rem;color:#888;margin:0">
                To upload files, visit <a href="{href}" style="color:#5ba4f5">{label}</a>.
              </p>
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
        string urlBase = "",
        TimeSpan? uploadTtl = null,
        string? adminExemptPath = null)
    {
        // Determine URL prefixes based on the view root
        var browsePrefix   = string.IsNullOrEmpty(urlBase) ? "/browse/"       : $"/{urlBase}/browse/";
        var downloadPrefix = string.IsNullOrEmpty(urlBase) ? "/download/"     : $"/{urlBase}/download/";
        var zipPrefix      = string.IsNullOrEmpty(urlBase) ? "/download-zip/" : $"/{urlBase}/download-zip/";
        var rootHref       = string.IsNullOrEmpty(urlBase) ? "/"              : $"/{urlBase}";

        var isMyUploads    = urlBase == "my-uploads";
        var isAdminUploads = urlBase == "admin/uploads";
        var isUploadArea   = urlBase == "upload-area";
        var showExpiry     = uploadTtl.HasValue;
        var colSpan        = showExpiry ? "5" : "4";

        var sb = new StringBuilder();

        // Parent directory link
        if (segments.Length > 0)
        {
            var parentPath = segments.Length == 1
                ? rootHref
                : browsePrefix + string.Join("/", segments[..^1].Select(Uri.EscapeDataString));
            sb.AppendLine($"""
                    <tr>
                      <td colspan="{colSpan}"><a href="{parentPath}" class="name"><span class="icon">📁</span> ..</a></td>
                    </tr>
                """);
        }

        // Subdirectories
        foreach (var dir in dirs)
        {
            var href      = browsePrefix + UrlPath(segments, dir.Name);
            var zipUrl    = zipPrefix    + UrlPath(segments, dir.Name);
            var delDirUrl       = "/delete-dir/"                    + UrlPath(segments, dir.Name);
            var renameDirUrl    = "/rename-dir/"                    + UrlPath(segments, dir.Name);
            var myRenameDirUrl  = "/my-uploads/rename-dir/"         + UrlPath(segments, dir.Name);
            var admRenameDirUrl = "/admin/uploads/rename-dir/"      + UrlPath(segments, dir.Name);
            var name      = HttpUtility.HtmlEncode(dir.Name);
            var nameJs    = HttpUtility.JavaScriptStringEncode(dir.Name);
            var modif     = dir.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var dirActionBtns = isMyUploads
                ? $"""<button class="act-btn" title="Rename folder" onclick="fbRename('{myRenameDirUrl}','{nameJs}')">✏️</button>"""
                : isAdminUploads
                    ? $"""<button class="act-btn" title="Rename folder" onclick="fbRename('{admRenameDirUrl}','{nameJs}')">✏️</button>"""
                    : isAdmin
                        ? $"""
                            <button class="act-btn" title="Rename folder" onclick="fbRename('{renameDirUrl}','{nameJs}')">✏️</button>
                            <button class="act-btn" title="Delete folder" onclick="fbDelete('{delDirUrl}','{nameJs}/')">🗑️</button>
                          """
                        : "";
            var zipBtn = isMyUploads ? "" : $"""<a href="{zipUrl}" class="act-btn" title="Download as ZIP">⬇️</a>""";
            var dirExpiryTd = showExpiry ? "<td></td>" : "";
            sb.AppendLine($"""
                    <tr>
                      <td><a href="{href}" class="name"><span class="icon">📁</span>{name}/</a></td>
                      <td class="size">—</td>
                      <td class="modified">{modif}</td>
                      {dirExpiryTd}
                      <td class="actions">{zipBtn}{dirActionBtns}</td>
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
            var browseInfoUrl = "/info/"                    + filePath;
            var infoUrl      = "/my-uploads/info/"         + filePath;
            var myDelUrl     = "/my-uploads/delete/"       + filePath;
            var myRenameUrl  = "/my-uploads/rename/"       + filePath;
            var admDelUrl    = "/admin/uploads/delete/"    + filePath;
            var admRenameUrl = "/admin/uploads/rename/"    + filePath;
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
                        <button class="act-btn" title="Rename" onclick="fbRename('{myRenameUrl}','{nameJs}')">✏️</button>
                        <button class="act-btn" title="Delete" onclick="fbDelete('{myDelUrl}','{name}')">🗑️</button>
                  """;
            }
            else if (isAdminUploads)
            {
                nameCell = $"""<span class="name"><span class="icon">{icon}</span>{name}</span>""";
                actionsCell = $"""
                        <button class="act-btn" title="Rename" onclick="fbRename('{admRenameUrl}','{nameJs}')">✏️</button>
                        <button class="act-btn" title="Delete" onclick="fbDelete('{admDelUrl}','{name}')">🗑️</button>
                  """;
            }
            else
            {
                nameCell = $"""<a href="{href}" class="name"><span class="icon">{icon}</span>{name}</a>""";
                if (isUploadArea)
                {
                    // Upload-area resolves files from uploadDir; /info/ and admin mutation endpoints
                    // use rootDir so they must not be shown here.
                    actionsCell = "";
                }
                else
                {
                    // Admin mutation buttons only shown in the main browse view
                    var adminButtons = isAdmin
                        ? $"""
                            <button class="act-btn" title="Share link" onclick="fbShare('{shareUrl}')">🔗</button>
                            <button class="act-btn" title="Rename" onclick="fbRename('{renameUrl}','{nameJs}')">✏️</button>
                            <button class="act-btn" title="Delete" onclick="fbDelete('{deleteUrl}','{name}')">🗑️</button>
                          """
                        : "";
                    actionsCell = $"""
                            <button class="act-btn" title="File info" onclick="fbInfo('{browseInfoUrl}','{nameJs}')">ℹ️</button>
                            {adminButtons}
                      """;
                }
            }

            // Expiry column for files (when uploadTtl is set)
            string fileExpiryTd = "";
            if (uploadTtl.HasValue)
            {
                bool isAdminExempt = adminExemptPath is not null &&
                    file.FullName.StartsWith(adminExemptPath, StringComparison.OrdinalIgnoreCase);

                if (isAdminExempt)
                {
                    fileExpiryTd = "<td class=\"expires\" style=\"color:#888;font-size:.82rem\">never expires</td>";
                }
                else
                {
                    var expiresAt  = file.LastWriteTimeUtc + uploadTtl.Value;
                    var isoExpiry  = HttpUtility.HtmlAttributeEncode(expiresAt.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    var diffSec    = (expiresAt - DateTime.UtcNow).TotalSeconds;
                    var expiryText = FormatExpiryText(diffSec);
                    var expiryColor = diffSec <= 0 ? "#e53e3e" : "#888";
                    fileExpiryTd = $"<td class=\"expires\" style=\"color:{expiryColor};font-size:.82rem\" data-expires=\"{isoExpiry}\">{HttpUtility.HtmlEncode(expiryText)}</td>";
                }
            }

            sb.AppendLine($"""
                    <tr>
                      <td>{nameCell}</td>
                      <td class="size">{size}</td>
                      <td class="modified">{modif}</td>
                      {fileExpiryTd}
                      <td class="actions">
                        {actionsCell}
                      </td>
                    </tr>
                """);
        }

        if (dirs.Count == 0 && files.Count == 0)
            sb.AppendLine($"""    <tr><td colspan="{colSpan}" class="empty">This folder is empty.</td></tr>""");

        return sb.ToString();
    }

    // ── Expiry helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a human-readable expiry string like "expires in 6h 30m" or "expired 5m ago".
    /// </summary>
    internal static string FormatExpiryText(double diffSec)
    {
        if (diffSec <= 0)
        {
            var ago = -diffSec;
            if (ago < 60)    return $"expired {(int)ago}s ago";
            if (ago < 3600)  return $"expired {(int)(ago / 60)}m ago";
            if (ago < 86400) return $"expired {(int)(ago / 3600)}h {(int)(ago % 3600 / 60)}m ago";
            return $"expired {(int)(ago / 86400)}d ago";
        }
        if (diffSec < 60)    return $"expires in {(int)diffSec}s";
        if (diffSec < 3600)  return $"expires in {(int)(diffSec / 60)}m";
        if (diffSec < 86400) return $"expires in {(int)(diffSec / 3600)}h {(int)(diffSec % 3600 / 60)}m";
        return $"expires in {(int)(diffSec / 86400)}d {(int)(diffSec % 86400 / 3600)}h";
    }

    /// <summary>
    /// Builds the inline script block for file expiry countdown updates.
    /// Selects all td[data-expires] cells and refreshes every 10 seconds.
    /// </summary>
    private static string BuildExpiryJs() => """
        <script>
        (function(){
        function _fmtExpiry(isoStr){
          var diffSec=Math.round((new Date(isoStr)-Date.now())/1000);
          if(diffSec<=0){var ago=-diffSec;
            if(ago<60)   return{text:'expired '+ago+'s ago',expired:true};
            if(ago<3600) return{text:'expired '+Math.floor(ago/60)+'m ago',expired:true};
            if(ago<86400)return{text:'expired '+Math.floor(ago/3600)+'h '+Math.floor((ago%3600)/60)+'m ago',expired:true};
            return{text:'expired '+Math.floor(ago/86400)+'d ago',expired:true};
          }
          if(diffSec<60)   return{text:'expires in '+diffSec+'s',expired:false};
          if(diffSec<3600) return{text:'expires in '+Math.floor(diffSec/60)+'m',expired:false};
          if(diffSec<86400)return{text:'expires in '+Math.floor(diffSec/3600)+'h '+Math.floor((diffSec%3600)/60)+'m',expired:false};
          return{text:'expires in '+Math.floor(diffSec/86400)+'d '+Math.floor((diffSec%86400)/3600)+'h',expired:false};
        }
        function initFileExpiryCountdowns(){
          var cells=Array.from(document.querySelectorAll('td[data-expires]'));
          if(!cells.length)return;
          function refresh(){cells.forEach(function(td){
            var r=_fmtExpiry(td.dataset.expires);
            td.textContent=r.text;
            td.style.color=r.expired?'#e53e3e':'#888';
          });}
          refresh();
          setInterval(refresh,10000);
        }
        initFileExpiryCountdowns();
        })();
        </script>
        """;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the meta tag and inline sessionStorage script for the auto-login session bearer token.
    /// Returns an empty string when no bearer token is present (normal auth flow).
    /// The token is a lowercase hex string so no HTML encoding is required, but we encode anyway for safety.
    /// </summary>
    private static string BuildBearerMeta(string? token)
    {
        if (token is null) return "";
        var safe = HttpUtility.HtmlAttributeEncode(token);
        // Hex string — no JS injection risk, but escape in the script for defence in depth.
        return $"<meta name=\"fb-bearer\" content=\"{safe}\">" +
               $"<script>try{{sessionStorage.setItem('fb-bearer','{safe}')}}catch{{}}</script>";
    }

    /// <summary>
    /// Returns an inline script that intercepts internal link-clicks and form-submits and
    /// appends <c>?_bearer=…</c> to the URL when a session bearer token is present in
    /// <c>sessionStorage</c>. Used on admin pages that do not embed <c>app.js</c> so that
    /// navigation from those pages remains authenticated in cookie-hostile environments
    /// (e.g. mobile QR-scanner webviews).
    /// </summary>
    internal static string BuildAuthNavScript() => """
        <script>
        (function(){
          var b=(function(){try{return sessionStorage.getItem('fb-bearer')}catch{return null}})();
          if(!b)return;
          function addBearer(h){
            if(!h||!h.startsWith('/')||h.includes('_bearer='))return h;
            return h+(h.includes('?')?'&':'?')+'_bearer='+encodeURIComponent(b);
          }
          document.addEventListener('click',function(e){
            var a=e.target.closest('a[href]');
            if(!a)return;
            var h=a.getAttribute('href');
            if(!h||!h.startsWith('/')||/^\/(s|join|auto-login)\//.test(h)||h.includes('_bearer='))return;
            e.preventDefault();
            window.location.assign(addBearer(h));
          });
          document.addEventListener('submit',function(e){
            var f=e.target;
            if(!f||f.tagName!=='FORM')return;
            var a=f.getAttribute('action')||'';
            if(a.startsWith('/')&&!a.includes('_bearer='))f.setAttribute('action',addBearer(a));
          });
        })();
        </script>
        """;

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

    // ── Admin Config modal (injected into main browse page for admin users) ───

    /// <summary>
    /// Builds the HTML for the config export modal: a "Config File" tab (JSON download)
    /// and a "CLI Command" tab (copyable shell command). Rendered as a hidden dialog injected
    /// into the main browse page via the {{ADMIN_CONFIG_MODAL}} placeholder.
    /// </summary>
    public static string BuildAdminConfigModal(string configJson, string cliCommand)
    {
        var jsonEncoded = HttpUtility.HtmlEncode(configJson);
        var cliEncoded  = HttpUtility.HtmlEncode(cliCommand);

        var sb = new StringBuilder();
        sb.Append("""
<style>
.cfg-modal-backdrop{position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:200}
.cfg-modal-box{position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);z-index:201;background:#1a1d27;border-radius:10px;width:min(96vw,640px);overflow:hidden}
.cfg-modal-hdr{display:flex;align-items:center;justify-content:space-between;padding:.9rem 1.25rem;background:#222536}
.cfg-modal-hdr h3{font-size:1rem;font-weight:600}
.cfg-close-btn{background:none;border:none;color:#aaa;font-size:1.1rem;cursor:pointer;padding:.1rem .4rem;border-radius:3px}
.cfg-close-btn:hover{background:#333;color:#fff}
.cfg-tabs{display:flex;gap:0;border-bottom:1px solid #252836;background:#1a1d27;padding:0 1.25rem}
.cfg-tab{background:none;border:none;border-bottom:2px solid transparent;color:#888;font-size:.85rem;padding:.65rem 1rem;cursor:pointer;font-weight:500}
.cfg-tab.active{color:#5ba4f5;border-bottom-color:#5ba4f5}
.cfg-panel{padding:1.25rem;display:none}
.cfg-panel.active{display:block}
.cfg-pre{background:#0f1117;border:1px solid #252836;border-radius:6px;padding:.85rem 1rem;font-size:.78rem;font-family:monospace;color:#b0c4de;white-space:pre-wrap;word-break:break-all;max-height:320px;overflow:auto;margin-bottom:.85rem}
.cfg-cli{background:#0f1117;border:1px solid #252836;border-radius:6px;padding:.85rem 1rem;font-size:.82rem;font-family:monospace;color:#b0c4de;white-space:pre-wrap;word-break:break-all;margin-bottom:.85rem}
.cfg-modal-ftr{display:flex;gap:.5rem;padding:.9rem 1.25rem;background:#141620;justify-content:flex-end}
.cfg-btn{display:inline-block;padding:.45rem 1rem;background:#5ba4f5;color:#fff;border:none;border-radius:5px;font-size:.85rem;cursor:pointer;font-weight:600;text-decoration:none}
.cfg-btn:hover{background:#3d8fe0}
.cfg-btn-sec{background:transparent;border:1px solid #444;color:#aaa}
.cfg-btn-sec:hover{background:#252836;border-color:#5ba4f5;color:#e0e0e0}
</style>
<div id="cfg-modal" role="dialog" aria-modal="true" hidden>
  <div class="cfg-modal-backdrop" onclick="closeConfigModal()"></div>
  <div class="cfg-modal-box">
    <div class="cfg-modal-hdr">
      <h3>Server Configuration</h3>
      <button class="cfg-close-btn" onclick="closeConfigModal()">&#x2715;</button>
    </div>
    <div class="cfg-tabs">
      <button class="cfg-tab active" id="cfg-tab-json" onclick="switchCfgTab('json')">Config File</button>
      <button class="cfg-tab" id="cfg-tab-cli" onclick="switchCfgTab('cli')">CLI Command</button>
    </div>
""");
        sb.Append($"""
    <div id="cfg-panel-json" class="cfg-panel active">
      <pre class="cfg-pre">{jsonEncoded}</pre>
      <div style="font-size:.78rem;color:#666;margin-bottom:.85rem">Passwords are omitted. Save as <code>filebeam.json</code> in the working directory or pass with <code>--config</code>.</div>
    </div>
    <div id="cfg-panel-cli" class="cfg-panel">
      <pre class="cfg-cli">{cliEncoded}</pre>
      <div style="font-size:.78rem;color:#666;margin-bottom:.85rem">Password flags are omitted. Add <code>--password</code> or <code>--credentials-file</code> as needed.</div>
    </div>
""");
        sb.Append("""
    <div class="cfg-modal-ftr">
      <button class="cfg-btn cfg-btn-sec" onclick="closeConfigModal()">Close</button>
      <a id="cfg-download-btn" class="cfg-btn" href="/admin/config" download="filebeam.json">Download JSON</a>
      <button id="cfg-copy-btn" class="cfg-btn" style="display:none" onclick="copyCfgCli()">Copy</button>
    </div>
  </div>
</div>
<script>
function openConfigModal() {
  document.getElementById('cfg-modal').hidden = false;
  switchCfgTab('json');
}
function closeConfigModal() {
  document.getElementById('cfg-modal').hidden = true;
}
function switchCfgTab(tab) {
  document.getElementById('cfg-tab-json').classList.toggle('active', tab === 'json');
  document.getElementById('cfg-tab-cli').classList.toggle('active', tab === 'cli');
  document.getElementById('cfg-panel-json').classList.toggle('active', tab === 'json');
  document.getElementById('cfg-panel-cli').classList.toggle('active', tab === 'cli');
  document.getElementById('cfg-download-btn').style.display = tab === 'json' ? '' : 'none';
  document.getElementById('cfg-copy-btn').style.display     = tab === 'cli'  ? '' : 'none';
}
function cfgCopy(text) {
  if (navigator.clipboard && window.isSecureContext)
    return navigator.clipboard.writeText(text);
  const el = document.createElement('textarea');
  el.value = text;
  el.style.cssText = 'position:fixed;top:0;left:0;opacity:0';
  document.body.appendChild(el);
  el.select();
  try { document.execCommand('copy'); } catch {}
  document.body.removeChild(el);
  return Promise.resolve();
}
function copyCfgCli() {
  const text = document.querySelector('#cfg-panel-cli pre').textContent;
  cfgCopy(text).then(() => {
    const btn = document.getElementById('cfg-copy-btn');
    btn.textContent = 'Copied!';
    setTimeout(() => btn.textContent = 'Copy', 1600);
  });
}
</script>
""");
        return sb.ToString();
    }

    // ── Admin Invites page ────────────────────────────────────────────────────

    /// <summary>
    /// Renders the full admin invites management page as a self-contained HTML document.
    /// </summary>
    public static string RenderInvitesAdmin(
        IReadOnlyList<InviteToken> tokens,
        string csrfToken,
        string baseUrl,
        string navLinks = "")
    {
        var now      = DateTimeOffset.UtcNow;
        var active   = tokens.Where(t => t.IsActive && !(t.ExpiresAt.HasValue && t.ExpiresAt <= now)).ToList();
        var inactive = tokens.Except(active).ToList();

        var sb = new StringBuilder();

        // ── Head + CSS ──────────────────────────────────────────────────────
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head>\n");
        sb.Append("<meta charset=\"UTF-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        sb.Append($"<meta name=\"csrf-token\" content=\"{HttpUtility.HtmlAttributeEncode(csrfToken)}\">\n");
        sb.Append("<title>FileBeam \u2014 Invites</title>\n");
        sb.Append("""
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#0f1117;color:#e0e0e0;min-height:100vh;padding:2rem}
a{color:#5ba4f5;text-decoration:none}a:hover{text-decoration:underline}
header{display:flex;align-items:center;gap:1rem;margin-bottom:1.75rem}
header h1{font-size:1.4rem;font-weight:700;color:#5ba4f5}
.section{background:#1a1d27;border-radius:8px;padding:1.25rem 1.5rem;margin-bottom:1.5rem}
.section-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:1rem}
.section-hdr h2{font-size:1rem;font-weight:600;color:#aaa;text-transform:uppercase;letter-spacing:.05em}
table{width:100%;border-collapse:collapse;margin-top:.5rem}
thead tr{background:#222536}
th{text-align:left;padding:.65rem 1rem;font-size:.73rem;text-transform:uppercase;letter-spacing:.05em;color:#888;font-weight:600}
td{padding:.6rem 1rem;font-size:.9rem;border-top:1px solid #252836}
tr:hover td{background:#20233a}
.actions{text-align:right;white-space:nowrap}
.act-btn{background:none;border:none;cursor:pointer;font-size:.9rem;padding:.1rem .3rem;border-radius:3px;opacity:.4;transition:opacity .15s}
tr:hover .act-btn{opacity:1}
.act-btn:hover{background:#252836}
.empty-msg{color:#555;padding:1.5rem 0;text-align:center;font-size:.9rem}
.role-badge{display:inline-block;padding:.15rem .55rem;border-radius:4px;font-size:.78rem;font-weight:600;background:#252836;color:#aaa}
.role-admin{background:#2a1a1a;color:#f87171}
.role-rw{background:#1a2a1a;color:#4ade80}
.role-ro{background:#1a1f2a;color:#60a5fa}
.role-wo{background:#2a2a1a;color:#facc15}
.status-inactive td,.status-expired td{opacity:.5}
details summary{cursor:pointer;color:#888;font-size:.9rem;padding:.5rem 0;user-select:none}
details summary:hover{color:#aaa}
details[open] summary{margin-bottom:.5rem}
.btn{display:inline-block;padding:.5rem 1.1rem;background:#5ba4f5;color:#fff;border:none;border-radius:5px;font-size:.88rem;cursor:pointer;font-weight:600}
.btn:hover{background:#3d8fe0}.btn:disabled{opacity:.5;cursor:default}
.btn-secondary{background:transparent;border:1px solid #444;color:#aaa}
.btn-secondary:hover{background:#252836;border-color:#5ba4f5;color:#e0e0e0}
.btn-sm{padding:.3rem .75rem;font-size:.8rem}
.modal-backdrop{position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:100}
.modal-box{position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);z-index:101;background:#1a1d27;border-radius:10px;width:min(92vw,440px);overflow:hidden}
.modal-hdr{display:flex;align-items:center;justify-content:space-between;padding:.9rem 1.25rem;background:#222536}
.modal-hdr h3{font-size:1rem;font-weight:600}
.close-btn{background:none;border:none;color:#aaa;font-size:1.1rem;cursor:pointer;padding:.1rem .4rem;border-radius:3px}
.close-btn:hover{background:#333;color:#fff}
.modal-body{padding:1.25rem}
.modal-ftr{display:flex;gap:.5rem;padding:.9rem 1.25rem;background:#141620;justify-content:flex-end}
.form-lbl{display:flex;flex-direction:column;gap:.3rem;font-size:.82rem;color:#888;margin-bottom:.85rem}
.form-inp{background:#0f1117;border:1px solid #333;border-radius:5px;color:#e0e0e0;font-size:.9rem;padding:.45rem .75rem}
.form-inp:focus{outline:none;border-color:#5ba4f5}
.link-row{display:flex;gap:.5rem;margin-top:.4rem}
.link-row .form-inp{flex:1}
nav a{font-size:.82rem;color:#aaa;white-space:nowrap}
</style>
</head>
""");

        // ── Body + header ───────────────────────────────────────────────────
        sb.Append("<body>\n");
        sb.Append("<header>\n");
        sb.Append("  <h1>\u26a1 FileBeam</h1>\n");
        sb.Append($"  <nav style=\"display:flex;align-items:center;gap:.75rem;margin-left:1rem\">{navLinks}</nav>\n");
        sb.Append("</header>\n");

        // ── Active invites ──────────────────────────────────────────────────
        sb.Append("<div class=\"section\">\n");
        sb.Append($"<div class=\"section-hdr\"><h2>Active Invites ({active.Count})</h2>");
        sb.Append("<button class=\"btn btn-sm\" onclick=\"openModal()\">+ New Invite</button></div>\n");
        if (active.Count == 0)
            sb.Append("<p class=\"empty-msg\">No active invites. Create one to get started.</p>\n");
        else
        {
            sb.Append(BuildInviteRows(active, baseUrl, active: true));
            sb.Append("<p style=\"font-size:.78rem;color:#555;margin-top:.75rem\">&#x26a0; The Bearer token and join link share the same invite ID — sharing the link grants both browser and API access.</p>\n");
        }
        sb.Append("</div>\n");

        // ── Inactive / expired invites ──────────────────────────────────────
        if (inactive.Count > 0)
        {
            sb.Append($"<details><summary>Inactive / Expired ({inactive.Count})</summary>\n");
            sb.Append(BuildInviteRows(inactive, baseUrl, active: false));
            sb.Append("</details>\n");
        }

        // ── New Invite modal ────────────────────────────────────────────────
        sb.Append("""
<div id="modal" role="dialog" aria-modal="true" hidden>
  <div class="modal-backdrop" onclick="closeModal()"></div>
  <div class="modal-box">
    <div class="modal-hdr">
      <h3>New Invite</h3>
      <button class="close-btn" onclick="closeModal()">&#x2715;</button>
    </div>
    <div class="modal-body">
      <label class="form-lbl">Friendly Name
        <input id="inp-name" class="form-inp" placeholder="e.g. Alice" maxlength="80" autocomplete="off">
      </label>
      <label class="form-lbl">Role
        <select id="sel-role" class="form-inp">
          <option value="rw" selected>rw &#x2014; read / write</option>
          <option value="ro">ro &#x2014; read-only</option>
          <option value="wo">wo &#x2014; upload-only</option>
          <option value="admin">admin &#x2014; administrator</option>
        </select>
      </label>
      <label class="form-lbl">Expires
        <select id="sel-expiry" class="form-inp">
          <option value="">Never</option>
          <option value="3600">1 hour</option>
          <option value="86400" selected>24 hours</option>
          <option value="604800">7 days</option>
          <option value="2592000">30 days</option>
        </select>
      </label>
      <label class="form-lbl">Max join uses (browser link)
        <select id="sel-join-max" class="form-inp">
          <option value="" selected>Unlimited</option>
          <option value="1">1</option>
          <option value="5">5</option>
          <option value="10">10</option>
        </select>
      </label>
      <label class="form-lbl">Max API uses (Bearer token)
        <select id="sel-bearer-max" class="form-inp">
          <option value="" selected>Unlimited</option>
          <option value="1">1</option>
          <option value="10">10</option>
          <option value="100">100</option>
        </select>
      </label>
      <div id="create-err" style="color:#f66;font-size:.82rem;min-height:1.2em"></div>
      <div id="link-result" hidden style="margin-top:.75rem">
        <div style="font-size:.82rem;color:#888;margin-bottom:.35rem">Browser join link:</div>
        <div class="link-row">
          <input id="link-url" class="form-inp" readonly>
          <button class="btn btn-sm" id="copy-btn" onclick="copyGeneratedLink()">Copy</button>
        </div>
        <div style="font-size:.82rem;color:#888;margin:.5rem 0 .35rem">CLI / API Bearer token:</div>
        <div class="link-row">
          <input id="link-bearer" class="form-inp" readonly>
          <button class="btn btn-sm" id="copy-bearer-btn" onclick="copyGeneratedBearer()">Copy</button>
        </div>
      </div>
    </div>
    <div class="modal-ftr">
      <button class="btn btn-secondary" onclick="closeModal()">Close</button>
      <button class="btn" id="btn-create" onclick="createInvite()">Create</button>
    </div>
  </div>
</div>
""");

        // ── Inline JavaScript (no C# interpolation — contains JS {} freely) ─
        sb.Append("""
<script>
const CSRF = document.querySelector('meta[name=csrf-token]').content;
let _reloadOnClose = false;

function openModal() {
  _reloadOnClose = false;
  document.getElementById('modal').hidden = false;
  document.getElementById('link-result').hidden = true;
  document.getElementById('create-err').textContent = '';
  document.getElementById('inp-name').value = '';
  document.getElementById('sel-join-max').value = '';
  document.getElementById('sel-bearer-max').value = '';
  const btn = document.getElementById('btn-create');
  btn.disabled = false; btn.textContent = 'Create';
  document.getElementById('inp-name').focus();
}

function closeModal() {
  document.getElementById('modal').hidden = true;
  if (_reloadOnClose) location.reload();
}

async function createInvite() {
  const name      = document.getElementById('inp-name').value.trim();
  const role      = document.getElementById('sel-role').value;
  const expiry    = document.getElementById('sel-expiry').value;
  const joinMax   = document.getElementById('sel-join-max').value;
  const bearerMax = document.getElementById('sel-bearer-max').value;
  document.getElementById('create-err').textContent = '';
  if (!name) { document.getElementById('create-err').textContent = 'Name is required.'; return; }
  const body = { friendlyName: name, role };
  if (expiry)    body.expiresAt      = new Date(Date.now() + parseInt(expiry) * 1000).toISOString();
  if (joinMax)   body.joinMaxUses    = parseInt(joinMax);
  if (bearerMax) body.bearerMaxUses  = parseInt(bearerMax);
  const btn = document.getElementById('btn-create');
  btn.disabled = true; btn.textContent = 'Creating\u2026';
  try {
    const res = await fetch('/admin/invites', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': CSRF, 'Accept': 'application/json' },
      body: JSON.stringify(body)
    });
    if (!res.ok) throw new Error(await res.text());
    const token = await res.json();
    document.getElementById('link-url').value = location.origin + '/join/' + token.id;
    document.getElementById('link-bearer').value = 'Bearer ' + token.id;
    document.getElementById('link-result').hidden = false;
    btn.textContent = 'Created \u2713';
    _reloadOnClose = true;
  } catch (e) {
    document.getElementById('create-err').textContent = 'Error: ' + e.message;
    btn.disabled = false; btn.textContent = 'Create';
  }
}

function fbCopy(text) {
  if (navigator.clipboard && window.isSecureContext)
    return navigator.clipboard.writeText(text);
  // Fallback for plain HTTP (LAN) where clipboard API is unavailable
  const el = document.createElement('textarea');
  el.value = text;
  el.style.cssText = 'position:fixed;top:0;left:0;opacity:0';
  document.body.appendChild(el);
  el.select();
  try { document.execCommand('copy'); } catch {}
  document.body.removeChild(el);
  return Promise.resolve();
}

function copyGeneratedLink() {
  const url = document.getElementById('link-url').value;
  fbCopy(url).then(() => {
    const btn = document.getElementById('copy-btn');
    btn.textContent = 'Copied!';
    setTimeout(() => btn.textContent = 'Copy', 1600);
  });
}

function copyGeneratedBearer() {
  const val = document.getElementById('link-bearer').value;
  fbCopy(val).then(() => {
    const btn = document.getElementById('copy-bearer-btn');
    btn.textContent = 'Copied!';
    setTimeout(() => btn.textContent = 'Copy', 1600);
  });
}

function copyInviteLink(id, url) {
  fbCopy(url).then(() => {
    const btn = document.getElementById('cp-' + id);
    if (btn) { const orig = btn.textContent; btn.textContent = '\u2713'; setTimeout(() => btn.textContent = orig, 1600); }
  });
}

function copyBearerToken(id) {
  fbCopy('Bearer ' + id).then(() => {
    const btn = document.getElementById('cb-' + id);
    if (btn) { const orig = btn.textContent; btn.textContent = '\u2713'; setTimeout(() => btn.textContent = orig, 1600); }
  });
}

async function revokeInvite(id) {
  if (!confirm('Revoke this invite? All sessions issued with it will be invalidated immediately.')) return;
  const res = await fetch('/admin/invites/' + id, { method: 'DELETE', headers: { 'X-CSRF-Token': CSRF } });
  if (res.ok) location.reload(); else alert('Failed to revoke invite.');
}

// ── Expiry countdowns ──────────────────────────────────────────────────────
function _fmtExpiry(isoStr) {
  const diffSec = Math.round((new Date(isoStr) - Date.now()) / 1000);
  if (diffSec <= 0) {
    const ago = -diffSec;
    if (ago < 60)    return { text: 'expired ' + ago + 's ago',                          expired: true };
    if (ago < 3600)  return { text: 'expired ' + Math.floor(ago / 60) + 'm ago',         expired: true };
    if (ago < 86400) return { text: 'expired ' + Math.floor(ago / 3600) + 'h ago',       expired: true };
                     return { text: 'expired ' + Math.floor(ago / 86400) + 'd ago',      expired: true };
  }
  if (diffSec < 60)    return { text: 'expires in ' + diffSec + 's',                                                                        expired: false };
  if (diffSec < 3600)  return { text: 'expires in ' + Math.floor(diffSec / 60) + 'm',                                                      expired: false };
  if (diffSec < 86400) return { text: 'expires in ' + Math.floor(diffSec / 3600) + 'h ' + Math.floor((diffSec % 3600) / 60) + 'm',        expired: false };
                       return { text: 'expires in ' + Math.floor(diffSec / 86400) + 'd ' + Math.floor((diffSec % 86400) / 3600) + 'h',    expired: false };
}

function initExpiryCountdowns() {
  const cells = Array.from(document.querySelectorAll('td[data-expires]'));
  if (!cells.length) return;
  function refresh() {
    cells.forEach(function(td) {
      const r = _fmtExpiry(td.dataset.expires);
      td.textContent = r.text;
      td.style.color = r.expired ? '#e53e3e' : '#888';
    });
  }
  refresh();
  setInterval(refresh, 10000);
}
initExpiryCountdowns();
</script>
""");
        sb.Append(BuildAuthNavScript());
        sb.Append("</body></html>\n");

        return sb.ToString();
    }

    private static string BuildInviteRows(IReadOnlyList<InviteToken> tokens, string baseUrl, bool active)
    {
        var sb = new StringBuilder();
        sb.Append("<table>\n<thead><tr><th>Name</th><th>Role</th><th>Expires</th><th>Uses</th><th></th></tr></thead>\n<tbody>\n");

        foreach (var t in tokens)
        {
            var name     = HttpUtility.HtmlEncode(t.FriendlyName);
            var idAttr   = HttpUtility.HtmlAttributeEncode(t.Id);
            var link     = $"{baseUrl}/join/{t.Id}";
            var linkJs   = HttpUtility.JavaScriptStringEncode(link);
            var isExpired = t.ExpiresAt.HasValue && t.ExpiresAt.Value < DateTimeOffset.UtcNow;
            string expiryTd;
            if (t.ExpiresAt.HasValue)
            {
                var iso   = HttpUtility.HtmlAttributeEncode(t.ExpiresAt.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                var title = HttpUtility.HtmlAttributeEncode(t.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm") + " UTC");
                var text  = HttpUtility.HtmlEncode(t.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm") + " UTC");
                expiryTd = $"<td style=\"color:#888;font-size:.82rem\" data-expires=\"{iso}\" title=\"{title}\">{text}</td>";
            }
            else
            {
                expiryTd = "<td style=\"color:#555;font-size:.82rem\">Never</td>";
            }
            var rowClass   = (!t.IsActive || isExpired) ? " class=\"status-inactive\"" : "";
            var roleClass  = $"role-badge role-{HttpUtility.HtmlAttributeEncode(t.Role)}";
            var copyBtn    = active
                ? $"<button class=\"act-btn\" id=\"cp-{idAttr}\" title=\"Copy invite link\" onclick=\"copyInviteLink('{idAttr}','{linkJs}')\">🔗</button>"
                : "";
            var bearerBtn  = active
                ? $"<button class=\"act-btn\" id=\"cb-{idAttr}\" title=\"Copy Bearer token\" onclick=\"copyBearerToken('{idAttr}')\">⌨</button>"
                : "";
            var revokeBtn  = t.IsActive
                ? $"<button class=\"act-btn\" title=\"Revoke invite\" onclick=\"revokeInvite('{idAttr}')\">🚫</button>"
                : "<span style=\"font-size:.78rem;color:#555\">revoked</span>";

            // Uses cell: show "X / N" when a cap is set, plain "X" otherwise.
            // Bearer line only shown when a cap is set or the counter is non-zero.
            var joinLine = t.JoinMaxUses.HasValue
                ? $"join: {t.UseCount}&thinsp;/&thinsp;{t.JoinMaxUses}"
                : $"{t.UseCount}";
            var showBearer = t.BearerMaxUses.HasValue || t.BearerUseCount > 0;
            var bearerLine = showBearer
                ? (t.BearerMaxUses.HasValue
                    ? $"api: {t.BearerUseCount}&thinsp;/&thinsp;{t.BearerMaxUses}"
                    : $"api: {t.BearerUseCount}")
                : "";
            var usesTd = showBearer
                ? $"<td style=\"text-align:right;color:#888;font-size:.82rem\"><div>{joinLine}</div><div style=\"color:#666\">{bearerLine}</div></td>"
                : $"<td style=\"text-align:right;color:#888\">{joinLine}</td>";

            sb.AppendLine($"""
    <tr{rowClass}>
      <td>{name}</td>
      <td><span class="{roleClass}">{HttpUtility.HtmlEncode(t.Role)}</span></td>
      {expiryTd}
      {usesTd}
      <td class="actions">{copyBtn}{bearerBtn}{revokeBtn}</td>
    </tr>
""");
        }

        sb.Append("</tbody></table>\n");
        return sb.ToString();
    }

    // ── Admin Audit Log page ──────────────────────────────────────────────────

    /// <summary>
    /// Renders the full admin audit log page. Entries are shown most-recent-first.
    /// The page includes a 30-second auto-refresh meta tag.
    /// </summary>
    public static string RenderAuditLog(IReadOnlyList<AuditEntry> entries, string navLinks = "")
    {
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head>\n");
        sb.Append("<meta charset=\"UTF-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        sb.Append("<meta http-equiv=\"refresh\" content=\"30\">\n");
        sb.Append("<title>FileBeam \u2014 Audit Log</title>\n");
        sb.Append("""
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#0f1117;color:#e0e0e0;min-height:100vh;padding:2rem}
a{color:#5ba4f5;text-decoration:none}a:hover{text-decoration:underline}
header{display:flex;align-items:center;gap:1rem;margin-bottom:1.75rem}
header h1{font-size:1.4rem;font-weight:700;color:#5ba4f5}
.section{background:#1a1d27;border-radius:8px;padding:1.25rem 1.5rem;margin-bottom:1.5rem}
.section-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:1rem}
.section-hdr h2{font-size:1rem;font-weight:600;color:#aaa;text-transform:uppercase;letter-spacing:.05em}
.section-meta{font-size:.78rem;color:#555}
.tbl-wrap{overflow-x:auto}
table{width:100%;border-collapse:collapse;min-width:700px}
thead tr{background:#222536}
th{text-align:left;padding:.6rem .85rem;font-size:.72rem;text-transform:uppercase;letter-spacing:.05em;color:#888;font-weight:600;white-space:nowrap}
td{padding:.55rem .85rem;font-size:.85rem;border-top:1px solid #252836;vertical-align:top}
tr:hover td{background:#20233a}
.empty-msg{color:#555;padding:1.5rem 0;text-align:center;font-size:.9rem}
.action-badge{display:inline-block;padding:.1rem .45rem;border-radius:3px;font-size:.78rem;font-weight:600;font-family:monospace}
.action-download{background:#1a2535;color:#5ba4f5}
.action-upload{background:#1a2a1a;color:#4ade80}
.action-delete{background:#2a1a1a;color:#f87171}
.action-other{background:#252836;color:#888}
nav a{font-size:.82rem;color:#aaa;white-space:nowrap}
</style>
</head>
""");

        sb.Append("<body>\n");
        sb.Append("<header>\n");
        sb.Append("  <h1>\u26a1 FileBeam</h1>\n");
        sb.Append($"  <nav style=\"display:flex;align-items:center;gap:.75rem;margin-left:1rem\">{navLinks}</nav>\n");
        sb.Append("</header>\n");

        sb.Append("<div class=\"section\">\n");
        sb.Append("<div class=\"section-hdr\">");
        sb.Append($"<h2>Audit Log</h2>");
        sb.Append($"<span class=\"section-meta\">{entries.Count} entries shown &middot; refreshes every 30s</span>");
        sb.Append("</div>\n");

        if (entries.Count == 0)
        {
            sb.Append("<p class=\"empty-msg\">No audit entries found.</p>\n");
        }
        else
        {
            sb.Append("<div class=\"tbl-wrap\">\n");
            sb.Append("<table>\n");
            sb.Append("<thead><tr><th>Timestamp</th><th>Action</th><th>User</th><th>File</th><th>Bytes</th><th>IP</th><th>Status</th></tr></thead>\n");
            sb.Append("<tbody>\n");

            foreach (var e in entries.Reverse())
            {
                var ts         = HttpUtility.HtmlEncode(e.Timestamp);
                var action     = HttpUtility.HtmlEncode(e.Action);
                var actionCls  = e.Action switch
                {
                    "download" => "action-download",
                    "upload"   => "action-upload",
                    "delete"   => "action-delete",
                    _          => "action-other"
                };
                var user       = HttpUtility.HtmlEncode(e.Username ?? "\u2014");
                var path       = HttpUtility.HtmlEncode(e.Path);
                var bytes      = e.Bytes > 0 ? FormatSize(e.Bytes) : "\u2014";
                var ip         = HttpUtility.HtmlEncode(e.RemoteIp);
                var statusColor = e.StatusCode >= 500 ? "#f87171"
                                : e.StatusCode >= 400 ? "#fbbf24"
                                : e.StatusCode >= 200 ? "#4ade80"
                                : "#888";

                sb.AppendLine($"""
    <tr>
      <td style="color:#888;font-size:.78rem;white-space:nowrap">{ts}</td>
      <td><span class="action-badge {actionCls}">{action}</span></td>
      <td style="color:#aaa">{user}</td>
      <td style="font-family:monospace;font-size:.78rem;word-break:break-all">{path}</td>
      <td style="text-align:right;color:#888;white-space:nowrap">{bytes}</td>
      <td style="color:#888;font-size:.82rem;white-space:nowrap">{ip}</td>
      <td style="color:{statusColor};text-align:center">{e.StatusCode}</td>
    </tr>
""");
            }

            sb.Append("</tbody>\n</table>\n</div>\n");
        }

        sb.Append("</div>\n");
        sb.Append(BuildAuthNavScript());
        sb.Append("</body></html>\n");

        return sb.ToString();
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

    /// <summary>
    /// Renders the admin active sessions dashboard as a self-contained HTML document.
    /// Each row shows the invite name, role, IP, auth method, and last-seen time with a Revoke button.
    /// The page auto-refreshes every 30 seconds.
    /// </summary>
    public static string RenderSessionsAdmin(
        IReadOnlyList<SessionInfo> sessions,
        string navLinks  = "",
        string csrfToken = "",
        IReadOnlyList<(string Prefix, BearerSession Session)>? bearers = null)
    {
        var sb  = new StringBuilder();
        var now = DateTimeOffset.UtcNow;

        sb.Append("<!DOCTYPE html><html lang=\"en\"><head>\n");
        sb.Append("<meta charset=\"UTF-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        sb.Append("<meta http-equiv=\"refresh\" content=\"30\">\n");
        sb.Append("<title>FileBeam \u2014 Sessions</title>\n");
        sb.Append("""
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#0f1117;color:#e0e0e0;min-height:100vh;padding:2rem}
a{color:#5ba4f5;text-decoration:none}a:hover{text-decoration:underline}
header{display:flex;align-items:center;gap:1rem;margin-bottom:1.75rem}
header h1{font-size:1.4rem;font-weight:700;color:#5ba4f5}
.section{background:#1a1d27;border-radius:8px;padding:1.25rem 1.5rem;margin-bottom:1.5rem}
.section-hdr{display:flex;align-items:center;justify-content:space-between;margin-bottom:1rem}
.section-hdr h2{font-size:1rem;font-weight:600;color:#aaa;text-transform:uppercase;letter-spacing:.05em}
.section-meta{font-size:.78rem;color:#555}
.tbl-wrap{overflow-x:auto}
table{width:100%;border-collapse:collapse;min-width:600px}
thead tr{background:#222536}
th{text-align:left;padding:.6rem .85rem;font-size:.72rem;text-transform:uppercase;letter-spacing:.05em;color:#888;font-weight:600;white-space:nowrap}
td{padding:.55rem .85rem;font-size:.85rem;border-top:1px solid #252836;vertical-align:middle}
tr:hover td{background:#20233a}
.empty-msg{color:#555;padding:1.5rem 0;text-align:center;font-size:.9rem}
.role-badge{display:inline-block;padding:.15rem .55rem;border-radius:4px;font-size:.78rem;font-weight:600;background:#252836;color:#aaa}
.role-rw{background:#1a2a1a;color:#4ade80}
.role-ro{background:#1a1f2a;color:#60a5fa}
.role-wo{background:#2a2a1a;color:#facc15}
.method-badge{display:inline-block;padding:.1rem .45rem;border-radius:3px;font-size:.78rem;font-weight:600;font-family:monospace;background:#252836;color:#888}
.btn-revoke{background:#2a1a1a;border:1px solid #5a2020;color:#f87171;font-size:.78rem;padding:.2rem .6rem;border-radius:4px;cursor:pointer}
.btn-revoke:hover{background:#3a1a1a;border-color:#f87171}
nav a{font-size:.82rem;color:#aaa;white-space:nowrap}
</style>
</head>
""");

        sb.Append("<body>\n");
        sb.Append("<header>\n");
        sb.Append("  <h1>\u26a1 FileBeam</h1>\n");
        sb.Append($"  <nav style=\"display:flex;align-items:center;gap:.75rem;margin-left:1rem\">{navLinks}</nav>\n");
        sb.Append("</header>\n");

        sb.Append("<div class=\"section\">\n");
        sb.Append("<div class=\"section-hdr\">");
        sb.Append("<h2>Active Sessions</h2>");
        sb.Append($"<span class=\"section-meta\">{sessions.Count} active &middot; refreshes every 30s</span>");
        sb.Append("</div>\n");

        if (sessions.Count == 0)
        {
            sb.Append("<p class=\"empty-msg\">No active invite sessions.</p>\n");
        }
        else
        {
            sb.Append("<div class=\"tbl-wrap\">\n");
            sb.Append("<table>\n");
            sb.Append("<thead><tr><th>Invite</th><th>Role</th><th>IP</th><th>Method</th><th>Last Seen</th><th></th></tr></thead>\n");
            sb.Append("<tbody>\n");

            foreach (var s in sessions)
            {
                var name        = HttpUtility.HtmlEncode(s.InviteName);
                var roleCls     = s.Role switch { "rw" => "role-rw", "ro" => "role-ro", "wo" => "role-wo", _ => "" };
                var roleLabel   = HttpUtility.HtmlEncode(s.Role);
                var ip          = HttpUtility.HtmlEncode(s.Ip);
                var method      = HttpUtility.HtmlEncode(s.AuthMethod);
                var elapsed     = now - s.LastSeen;
                var lastSeen    = elapsed.TotalSeconds < 60
                    ? $"{(int)elapsed.TotalSeconds}s ago"
                    : elapsed.TotalMinutes < 60
                        ? $"{(int)elapsed.TotalMinutes}m ago"
                        : $"{(int)elapsed.TotalHours}h ago";
                var inviteIdEnc = Uri.EscapeDataString(s.InviteId);
                var csrfEnc     = HttpUtility.HtmlAttributeEncode(csrfToken);

                sb.AppendLine($"""
    <tr>
      <td style="font-family:monospace;font-size:.82rem">{name}</td>
      <td><span class="role-badge {roleCls}">{roleLabel}</span></td>
      <td style="color:#aaa;font-size:.82rem">{ip}</td>
      <td><span class="method-badge">{method}</span></td>
      <td style="color:#888;font-size:.82rem;white-space:nowrap">{lastSeen}</td>
      <td style="text-align:right">
        <form method="post" action="/admin/sessions/{inviteIdEnc}/revoke" style="display:inline">
          <input type="hidden" name="_csrf" value="{csrfEnc}">
          <button class="btn-revoke">Revoke</button>
        </form>
      </td>
    </tr>
""");
            }

            sb.Append("</tbody>\n</table>\n</div>\n");
        }

        sb.Append("</div>\n");

        // ── QR / Auto-login bearer sessions ────────────────────────────────────
        sb.Append("<div class=\"section\">\n");
        sb.Append("<div class=\"section-hdr\">");
        sb.Append("<h2>Active QR / Auto-login Sessions</h2>");
        var bearerCount = bearers?.Count ?? 0;
        sb.Append($"<span class=\"section-meta\">{bearerCount} active</span>");
        sb.Append("</div>\n");

        if (bearerCount == 0)
        {
            sb.Append("<p class=\"empty-msg\">No active QR/auto-login sessions.</p>\n");
        }
        else
        {
            sb.Append("<div class=\"tbl-wrap\">\n");
            sb.Append("<table>\n");
            sb.Append("<thead><tr><th>IP</th><th>Issued At</th><th>Last Seen</th><th>Expires At</th><th></th></tr></thead>\n");
            sb.Append("<tbody>\n");

            foreach (var (prefix, session) in bearers!)
            {
                var bIp      = HttpUtility.HtmlEncode(session.Ip);
                var bIssued  = session.IssuedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                var bElapsed = now - session.LastSeen;
                var bLastSeen = bElapsed.TotalSeconds < 60
                    ? $"{(int)bElapsed.TotalSeconds}s ago"
                    : bElapsed.TotalMinutes < 60
                        ? $"{(int)bElapsed.TotalMinutes}m ago"
                        : $"{(int)bElapsed.TotalHours}h ago";
                var bExpiry  = session.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                var prefixEnc = Uri.EscapeDataString(prefix);
                var csrfEnc   = HttpUtility.HtmlAttributeEncode(csrfToken);

                sb.AppendLine($"""
    <tr>
      <td style="color:#aaa;font-size:.82rem">{bIp}</td>
      <td style="color:#888;font-size:.82rem;white-space:nowrap">{bIssued}</td>
      <td style="color:#888;font-size:.82rem;white-space:nowrap">{bLastSeen}</td>
      <td style="color:#888;font-size:.82rem;white-space:nowrap">{bExpiry}</td>
      <td style="text-align:right">
        <form method="post" action="/admin/sessions/autologin/{prefixEnc}/revoke" style="display:inline">
          <input type="hidden" name="_csrf" value="{csrfEnc}">
          <button class="btn-revoke">Revoke</button>
        </form>
      </td>
    </tr>
""");
            }

            sb.Append("</tbody>\n</table>\n</div>\n");
        }

        sb.Append("</div>\n");
        sb.Append(BuildAuthNavScript());
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the admin QR code page with a freshly generated auto-login QR image.
    /// </summary>
    public static string RenderAdminQr(string base64Png, DateTimeOffset expiresAt, string navLinks = "")
    {
        var expStr = HttpUtility.HtmlEncode($"{expiresAt:HH:mm:ss} UTC (5 min)");
        return "<!DOCTYPE html><html lang=\"en\"><head>\n" +
               "<meta charset=\"UTF-8\">\n" +
               "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
               "<title>FileBeam \u2014 Admin QR</title>\n" +
               "<style>\n" +
               "*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}\n" +
               "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#0f1117;color:#e0e0e0;min-height:100vh;padding:2rem}\n" +
               "a{color:#5ba4f5;text-decoration:none}a:hover{text-decoration:underline}\n" +
               "header{display:flex;align-items:center;gap:1rem;margin-bottom:1.75rem}\n" +
               "header h1{font-size:1.4rem;font-weight:700;color:#5ba4f5}\n" +
               "nav a{font-size:.82rem;color:#aaa;white-space:nowrap}\n" +
               ".card{background:#1a1d27;border-radius:8px;padding:1.5rem 2rem;display:inline-block;text-align:center;margin-top:1rem}\n" +
               ".card img{display:block;max-width:320px;width:100%;border-radius:6px;background:#fff;padding:8px}\n" +
               ".expiry{margin-top:1rem;font-size:.85rem;color:#888}\n" +
               ".expiry strong{color:#facc15}\n" +
               ".hint{margin-top:.75rem;font-size:.78rem;color:#555}\n" +
               "</style>\n" +
               "</head>\n<body>\n" +
               "<header>\n" +
               "  <h1>\u26a1 FileBeam</h1>\n" +
               $"  <nav style=\"display:flex;align-items:center;gap:.75rem;margin-left:1rem\">{navLinks}</nav>\n" +
               "</header>\n" +
               "<h2 style=\"margin-bottom:.5rem\">Admin Auto-Login QR</h2>\n" +
               "<p style=\"font-size:.85rem;color:#888\">Scan with your phone to log in as admin instantly &mdash; single use, expires in 5 minutes.</p>\n" +
               "<div class=\"card\">\n" +
               $"  <img src=\"data:image/png;base64,{base64Png}\" alt=\"Admin QR code\">\n" +
               $"  <p class=\"expiry\">Expires at <strong>{expStr}</strong></p>\n" +
               "  <p class=\"hint\">This link is single-use. Scanning it again will show an error.</p>\n" +
               "</div>\n" +
               BuildAuthNavScript() +
               "</body></html>\n";
    }

    /// <summary>
    /// Renders a minimal centered login card for admin credential entry.
    /// </summary>
    /// <param name="csrfToken">CSRF token embedded as a hidden form field.</param>
    /// <param name="error">Error message to display, or null for no error.</param>
    /// <param name="next">URL to redirect to after a successful login, or null for /.</param>
    public static string RenderLoginPage(string csrfToken, string? error, string? next)
    {
        var errorHtml = error is not null
            ? $"<div class=\"err\">{HttpUtility.HtmlEncode(error)}</div>\n"
            : "";
        var nextHtml = !string.IsNullOrEmpty(next)
            ? $"<input type=\"hidden\" name=\"next\" value=\"{HttpUtility.HtmlAttributeEncode(next)}\">\n"
            : "";
        var csrfEsc = HttpUtility.HtmlAttributeEncode(csrfToken);

        return
            "<!DOCTYPE html><html lang=\"en\"><head>\n" +
            "<meta charset=\"UTF-8\">\n" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
            "<title>Sign In \u2014 FileBeam</title>\n" +
            "<style>\n" +
            "*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}\n" +
            "body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;" +
            "background:#0f1117;color:#e0e0e0;min-height:100vh;" +
            "display:flex;align-items:center;justify-content:center;padding:1rem}\n" +
            ".card{background:#1a1d27;border-radius:10px;padding:2rem 2.5rem;width:100%;max-width:360px}\n" +
            "h1{font-size:1.3rem;font-weight:700;color:#5ba4f5;margin-bottom:1.5rem}\n" +
            "label{display:block;font-size:.8rem;color:#888;margin-bottom:.3rem;margin-top:1rem}\n" +
            "input[type=text],input[type=password]{width:100%;padding:.6rem .75rem;" +
            "background:#0f1117;border:1px solid #2a2d3a;border-radius:6px;" +
            "color:#e0e0e0;font-size:.9rem;outline:none}\n" +
            "input:focus{border-color:#5ba4f5}\n" +
            ".btn{display:block;width:100%;margin-top:1.5rem;padding:.65rem;" +
            "background:#5ba4f5;color:#0f1117;border:none;border-radius:6px;" +
            "font-size:.95rem;font-weight:700;cursor:pointer}\n" +
            ".btn:hover{background:#7ab8f7}\n" +
            ".err{background:#2a1a1a;border:1px solid #7a3020;border-radius:6px;" +
            "padding:.65rem .9rem;color:#f87171;font-size:.85rem;margin-bottom:1rem}\n" +
            "</style>\n" +
            "</head>\n<body>\n" +
            "<div class=\"card\">\n" +
            "<h1>\u26a1 FileBeam</h1>\n" +
            errorHtml +
            "<form method=\"post\" action=\"/login\">\n" +
            $"<input type=\"hidden\" name=\"_csrf\" value=\"{csrfEsc}\">\n" +
            nextHtml +
            "<label for=\"u\">Username</label>\n" +
            "<input id=\"u\" type=\"text\" name=\"username\" autocomplete=\"username\" autofocus required>\n" +
            "<label for=\"p\">Password</label>\n" +
            "<input id=\"p\" type=\"password\" name=\"password\" autocomplete=\"current-password\" required>\n" +
            "<button class=\"btn\" type=\"submit\">Sign In</button>\n" +
            "</form>\n" +
            "</div>\n" +
            "</body></html>\n";
    }

    /// <summary>
    /// Renders a minimal error page for expired or already-used auto-login links.
    /// </summary>
    public static string RenderAutoLoginError() =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>Login Link Expired \u2014 FileBeam</title>" +
        "<style>body{font-family:sans-serif;max-width:480px;margin:4rem auto;padding:0 1rem;background:#0f1117;color:#e0e0e0}" +
        ".err{background:#2a1a1a;border:1px solid #7a3020;border-radius:6px;padding:1rem 1.5rem;color:#f87171}" +
        "a{color:#5ba4f5}</style>" +
        "</head><body>" +
        "<h2 style=\"color:#5ba4f5\">FileBeam</h2>" +
        "<div class=\"err\">This login link has expired or has already been used.</div>" +
        "<p style=\"margin-top:1.5rem\"><a href=\"/\">Go to FileBeam</a> and log in with your credentials.</p>" +
        "</body></html>";
}
