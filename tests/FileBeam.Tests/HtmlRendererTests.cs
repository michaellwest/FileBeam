namespace FileBeam.Tests;

public class HtmlRendererTests
{
    private static readonly List<DirectoryInfo> NoDirs  = [];
    private static readonly List<FileInfo>      NoFiles = [];

    // ── Breadcrumb ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_RootPath_BreadcrumbIsJustRootLink()
    {
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles);

        Assert.Contains("<a href=\"/\">root</a>", html);
        // No extra breadcrumb segments
        Assert.DoesNotContain("/browse/", html.Split("root</a>")[1].Split("</div>")[0]);
    }

    [Fact]
    public void Render_SingleSegment_BreadcrumbShowsSegmentAsSpan()
    {
        var html = HtmlRenderer.RenderDirectory("docs", NoDirs, NoFiles);

        Assert.Contains("<a href=\"/\">root</a>", html);
        Assert.Contains("<span>docs</span>", html);
    }

    [Fact]
    public void Render_MultiSegment_BreadcrumbShowsIntermediateLinks()
    {
        var html = HtmlRenderer.RenderDirectory("docs/api", NoDirs, NoFiles);

        Assert.Contains("<a href=\"/\">root</a>", html);
        Assert.Contains("/browse/docs", html);
        Assert.Contains("<span>api</span>", html);
    }

    // ── Upload section ────────────────────────────────────────────────────────

    [Fact]
    public void Render_ReadOnlyFalse_ContainsUploadSection()
    {
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles, isReadOnly: false);

        Assert.Contains("upload-section", html);
        Assert.Contains("multipart/form-data", html);
    }

    [Fact]
    public void Render_MyUploads_UploadFormAction_PrefixedWithMyUploads()
    {
        // When rendering inside the my-uploads view the form should post to
        // /my-uploads/upload/... so the handler can redirect back to /my-uploads after success.
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles,
            separateUploadDir: false, urlBase: "my-uploads");

        Assert.Contains("action=\"/my-uploads/upload/\"", html);
        Assert.DoesNotContain("action=\"/upload/\"", html);
    }

    [Fact]
    public void Render_MyUploads_SubfolderUploadFormAction_IncludesSubfolder()
    {
        var html = HtmlRenderer.RenderDirectory("photos", NoDirs, NoFiles,
            separateUploadDir: false, urlBase: "my-uploads");

        Assert.Contains("action=\"/my-uploads/upload/photos\"", html);
    }

    [Fact]
    public void Render_ReadOnlyTrue_OmitsUploadSection()
    {
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles, isReadOnly: true);

        // The CSS class name appears in the stylesheet; check for the actual form element instead
        Assert.DoesNotContain("<form id=\"upload-form\"", html);
        Assert.DoesNotContain("multipart/form-data", html);
    }

    // ── Empty folder ──────────────────────────────────────────────────────────

    [Fact]
    public void Render_EmptyDirectory_ShowsEmptyMessage()
    {
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles);

        Assert.Contains("This folder is empty.", html);
    }

    // ── File rows ─────────────────────────────────────────────────────────────

    [Fact]
    public void Render_WithFiles_ContainsFileNamesAndDownloadLinks()
    {
        var tmpDir  = Directory.CreateTempSubdirectory();
        var tmpFile = new FileInfo(Path.Combine(tmpDir.FullName, "report.pdf"));
        tmpFile.WriteAllText("dummy");

        try
        {
            var html = HtmlRenderer.RenderDirectory("", NoDirs, [tmpFile]);

            Assert.Contains("report.pdf", html);
            Assert.Contains("/download/report.pdf", html);
            Assert.DoesNotContain("This folder is empty.", html);
        }
        finally
        {
            tmpFile.Delete();
            tmpDir.Delete();
        }
    }

    [Fact]
    public void Render_WithSubdirectories_ContainsBrowseLinks()
    {
        var tmpDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "subdir_" + Guid.NewGuid().ToString("N")));
        tmpDir.Create();

        try
        {
            var html = HtmlRenderer.RenderDirectory("", [tmpDir], NoFiles);

            // href does not include a trailing slash; the "/" appears only in display text
            Assert.Contains($"/browse/{tmpDir.Name}", html);
            Assert.DoesNotContain("This folder is empty.", html);
        }
        finally
        {
            tmpDir.Delete();
        }
    }

    // ── Page title ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_PathAppearsInPageTitle()
    {
        var html = HtmlRenderer.RenderDirectory("my/path", NoDirs, NoFiles);

        Assert.Contains("my/path", html);
    }

    [Fact]
    public void Render_XssInPath_IsHtmlEncoded()
    {
        var html = HtmlRenderer.RenderDirectory("<script>alert(1)</script>", NoDirs, NoFiles);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    // ── Separate upload dir — upload redirect notice ──────────────────────────

    [Fact]
    public void Render_SeparateUploadDir_RwRole_ShowsRedirectNoticeInsteadOfForm()
    {
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles,
            separateUploadDir: true, role: "rw");

        Assert.DoesNotContain("multipart/form-data", html);
        Assert.Contains("upload-section", html);
        Assert.Contains("To upload files", html);
    }

    [Fact]
    public void Render_SeparateUploadDir_PerSender_NoticeLinkIsMyUploads()
    {
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles,
            separateUploadDir: true, role: "rw", perSender: true);

        Assert.Contains("href=\"/my-uploads\"", html);
        Assert.DoesNotContain("href=\"/upload-area\"", html);
    }

    [Fact]
    public void Render_SeparateUploadDir_NoPerSender_NoticeLinkIsUploadArea()
    {
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles,
            separateUploadDir: true, role: "rw", perSender: false);

        Assert.Contains("href=\"/upload-area\"", html);
        Assert.DoesNotContain("href=\"/my-uploads\"", html);
    }

    [Fact]
    public void Render_SeparateUploadDir_SameDir_StillShowsUploadForm()
    {
        // When separateUploadDir=false (same dir), upload form should still appear
        var html = HtmlRenderer.RenderDirectory("", NoDirs, NoFiles,
            separateUploadDir: false, role: "rw");

        Assert.Contains("multipart/form-data", html);
    }

    // ── my-uploads view rows ──────────────────────────────────────────────────

    [Fact]
    public void Render_MyUploads_FileRow_ContainsFbInfoAndDeleteButton()
    {
        var tmpDir  = Directory.CreateTempSubdirectory();
        var tmpFile = new FileInfo(Path.Combine(tmpDir.FullName, "photo.jpg"));
        tmpFile.WriteAllText("data");

        try
        {
            var html = HtmlRenderer.RenderDirectory("", NoDirs, [tmpFile], urlBase: "my-uploads");

            Assert.Contains("/my-uploads/info/photo.jpg", html);
            Assert.Contains("fbInfo(", html);
            Assert.Contains("/my-uploads/delete/photo.jpg", html);
            // Should NOT contain a download anchor for the file
            Assert.DoesNotContain("/my-uploads/download/photo.jpg", html.Split("fbInfo")[0]);
        }
        finally
        {
            tmpFile.Delete();
            tmpDir.Delete();
        }
    }

    [Fact]
    public void Render_MyUploads_FileRow_NoDownloadAnchor()
    {
        var tmpDir  = Directory.CreateTempSubdirectory();
        var tmpFile = new FileInfo(Path.Combine(tmpDir.FullName, "data.csv"));
        tmpFile.WriteAllText("a,b,c");

        try
        {
            var html = HtmlRenderer.RenderDirectory("", NoDirs, [tmpFile], urlBase: "my-uploads");

            // File name should not be wrapped in an <a href="/my-uploads/download/...">
            Assert.DoesNotContain($"href=\"/my-uploads/download/data.csv\"", html);
        }
        finally
        {
            tmpFile.Delete();
            tmpDir.Delete();
        }
    }

    // ── admin/uploads view rows ───────────────────────────────────────────────

    [Fact]
    public void Render_AdminUploads_FileRow_ContainsDeleteButtonOnly()
    {
        var tmpDir  = Directory.CreateTempSubdirectory();
        var tmpFile = new FileInfo(Path.Combine(tmpDir.FullName, "report.pdf"));
        tmpFile.WriteAllText("pdf");

        try
        {
            var html = HtmlRenderer.RenderDirectory("", NoDirs, [tmpFile], role: "admin", urlBase: "admin/uploads");

            Assert.Contains("/admin/uploads/delete/report.pdf", html);
            // No preview button and no fbInfo in the row (function definitions exist in embedded JS but not as onclick attributes)
            Assert.DoesNotContain("onclick=\"fbPreview(", html);
            Assert.DoesNotContain("onclick=\"fbInfo(", html);
        }
        finally
        {
            tmpFile.Delete();
            tmpDir.Delete();
        }
    }

    [Fact]
    public void Render_AdminUploads_FileRow_NoDownloadAnchor()
    {
        var tmpDir  = Directory.CreateTempSubdirectory();
        var tmpFile = new FileInfo(Path.Combine(tmpDir.FullName, "dump.zip"));
        tmpFile.WriteAllText("zip");

        try
        {
            var html = HtmlRenderer.RenderDirectory("", NoDirs, [tmpFile], role: "admin", urlBase: "admin/uploads");

            Assert.DoesNotContain($"href=\"/admin/uploads/download/dump.zip\"", html);
        }
        finally
        {
            tmpFile.Delete();
            tmpDir.Delete();
        }
    }
}

// Convenience extension to avoid creating real files on disk for simple size tests
file static class FileInfoExtensions
{
    public static void WriteAllText(this FileInfo fi, string content)
        => File.WriteAllText(fi.FullName, content);
}

public sealed class InvitesAdminPageTests
{
    // ── BuildNavLinks — hasInvites ────────────────────────────────────────────

    [Fact]
    public void BuildNavLinks_AdminWithInvites_ContainsInvitesLink()
    {
        var nav = HtmlRenderer.BuildNavLinks("admin", false, false, hasInvites: true);
        Assert.Contains("/admin/invites", nav);
        Assert.Contains("Invites", nav);
    }

    [Fact]
    public void BuildNavLinks_NonAdminWithInvites_NoInvitesLink()
    {
        var nav = HtmlRenderer.BuildNavLinks("rw", false, false, hasInvites: true);
        Assert.DoesNotContain("/admin/invites", nav);
    }

    [Fact]
    public void BuildNavLinks_AdminWithoutInvites_NoInvitesLink()
    {
        var nav = HtmlRenderer.BuildNavLinks("admin", false, false, hasInvites: false);
        Assert.DoesNotContain("/admin/invites", nav);
    }

    // ── RenderInvitesAdmin — structure ───────────────────────────────────────

    [Fact]
    public void RenderInvitesAdmin_EmptyStore_ContainsDarkThemeAndNewButton()
    {
        var html = HtmlRenderer.RenderInvitesAdmin([], "tok", "http://localhost:8080");

        Assert.Contains("#0f1117", html);           // dark background CSS
        Assert.Contains("New Invite", html);        // create button
        Assert.Contains("No active invites", html); // empty state message
    }

    [Fact]
    public void RenderInvitesAdmin_ContainsCsrfToken()
    {
        var html = HtmlRenderer.RenderInvitesAdmin([], "mycsrf", "http://localhost:8080");
        Assert.Contains("mycsrf", html);
    }

    [Fact]
    public void RenderInvitesAdmin_ContainsNavLinks()
    {
        var html = HtmlRenderer.RenderInvitesAdmin([], "tok", "http://localhost:8080", navLinks: "<a href=\"/\">Home</a>");
        Assert.Contains("<a href=\"/\">Home</a>", html);
    }

    // ── RenderInvitesAdmin — active invite rows ───────────────────────────────

    [Fact]
    public void RenderInvitesAdmin_ActiveInvite_ShowsNameAndRole()
    {
        var store = new InviteStore();
        var token = store.Create("Alice", "rw", null, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.Contains("Alice", html);
        Assert.Contains("role-rw", html);
        Assert.DoesNotContain("No active invites", html);
    }

    [Fact]
    public void RenderInvitesAdmin_ActiveInvite_ContainsJoinLink()
    {
        var store = new InviteStore();
        var token = store.Create("Bob", "ro", null, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://myhost");

        Assert.Contains($"http://myhost/join/{token.Id}", html);
    }

    [Fact]
    public void RenderInvitesAdmin_ActiveInvite_ContainsRevokeButton()
    {
        var store = new InviteStore();
        var token = store.Create("Carol", "rw", null, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.Contains("revokeInvite", html);
        Assert.Contains(token.Id, html);
    }

    [Fact]
    public void RenderInvitesAdmin_InviteWithExpiry_ShowsFormattedExpiry()
    {
        var store  = new InviteStore();
        var expiry = new DateTimeOffset(2027, 6, 1, 12, 0, 0, TimeSpan.Zero);
        store.Create("Dave", "ro", expiry, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.Contains("2027-06-01", html);
    }

    [Fact]
    public void RenderInvitesAdmin_InviteWithExpiry_HasDataExpiresAttribute()
    {
        var store  = new InviteStore();
        var expiry = new DateTimeOffset(2027, 6, 1, 12, 0, 0, TimeSpan.Zero);
        store.Create("Dave", "ro", expiry, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        // data-expires must be ISO 8601 UTC so client-side JS can parse it
        Assert.Contains("data-expires=\"2027-06-01T12:00:00Z\"", html);
        // absolute time preserved as tooltip
        Assert.Contains("title=\"2027-06-01 12:00 UTC\"", html);
    }

    [Fact]
    public void RenderInvitesAdmin_InviteWithoutExpiry_ShowsNever()
    {
        var store = new InviteStore();
        store.Create("Eve", "admin", null, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.Contains("Never", html);
        Assert.DoesNotContain("data-expires=\"", html);
    }

    // ── RenderInvitesAdmin — inactive / expired ───────────────────────────────

    [Fact]
    public void RenderInvitesAdmin_RevokedInvite_InInactiveSection()
    {
        var store = new InviteStore();
        var token = store.Create("Frank", "rw", null, "admin");
        store.Revoke(token.Id);

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.Contains("No active invites", html);
        Assert.Contains("Inactive", html);
        Assert.Contains("Frank", html);
    }

    [Fact]
    public void RenderInvitesAdmin_ExpiredInvite_InInactiveSection()
    {
        var store  = new InviteStore();
        var expiry = DateTimeOffset.UtcNow.AddSeconds(-10); // already expired
        store.Create("Grace", "rw", expiry, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.Contains("Inactive", html);
        Assert.Contains("Grace", html);
    }

    [Fact]
    public void RenderInvitesAdmin_ExpiredInvite_HasDataExpiresAttribute()
    {
        var store  = new InviteStore();
        var expiry = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero); // past
        store.Create("OldUser", "rw", expiry, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        // Even expired invites carry data-expires so JS can show "expired Xd ago"
        Assert.Contains("data-expires=\"2020-01-01T00:00:00Z\"", html);
    }

    [Fact]
    public void RenderInvitesAdmin_MixedActiveAndInactive_BothSectionsPresent()
    {
        var store = new InviteStore();
        store.Create("Active", "rw", null, "admin");
        var inactive = store.Create("Inactive", "ro", null, "admin");
        store.Revoke(inactive.Id);

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.DoesNotContain("No active invites", html);
        Assert.Contains("Inactive", html);
        Assert.Contains("Active", html);
    }

    // ── RenderInvitesAdmin — XSS escaping ────────────────────────────────────

    [Fact]
    public void RenderInvitesAdmin_HtmlInFriendlyName_IsEscaped()
    {
        var store = new InviteStore();
        store.Create("<script>alert(1)</script>", "rw", null, "admin");

        var html = HtmlRenderer.RenderInvitesAdmin(store.GetAll(), "tok", "http://host");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    // ── BuildAdminConfigModal ─────────────────────────────────────────────────

    [Fact]
    public void BuildAdminConfigModal_ContainsBothTabs()
    {
        var html = HtmlRenderer.BuildAdminConfigModal("{\"port\":8080}", "filebeam.exe --download /srv");

        Assert.Contains("Config File", html);
        Assert.Contains("CLI Command", html);
    }

    [Fact]
    public void BuildAdminConfigModal_ContainsConfigJson()
    {
        var html = HtmlRenderer.BuildAdminConfigModal("{\"port\":8080}", "filebeam.exe");

        Assert.Contains("&quot;port&quot;", html);  // HTML-encoded JSON
        Assert.Contains("8080", html);
    }

    [Fact]
    public void BuildAdminConfigModal_ContainsCliCommand()
    {
        var html = HtmlRenderer.BuildAdminConfigModal("{}", "filebeam.exe --download /srv --port 9090");

        Assert.Contains("--download", html);
        Assert.Contains("9090", html);
    }

    [Fact]
    public void BuildAdminConfigModal_XssInJsonEncoded()
    {
        var html = HtmlRenderer.BuildAdminConfigModal("<script>bad</script>", "cmd");

        Assert.DoesNotContain("<script>bad</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void BuildAdminConfigModal_ContainsDownloadLink()
    {
        var html = HtmlRenderer.BuildAdminConfigModal("{}", "cmd");

        Assert.Contains("href=\"/admin/config\"", html);
        Assert.Contains("download=\"filebeam.json\"", html);
    }

    [Fact]
    public void BuildAdminConfigModal_ContainsOpenConfigModalFunction()
    {
        var html = HtmlRenderer.BuildAdminConfigModal("{}", "cmd");

        Assert.Contains("openConfigModal", html);
        Assert.Contains("closeConfigModal", html);
    }

    // ── BuildNavLinks hasConfig ───────────────────────────────────────────────

    [Fact]
    public void BuildNavLinks_AdminWithHasConfig_ContainsConfigLink()
    {
        var nav = HtmlRenderer.BuildNavLinks("admin", false, false, hasConfig: true);

        Assert.Contains("openConfigModal", nav);
        Assert.Contains("Config", nav);
    }

    [Fact]
    public void BuildNavLinks_NonAdminWithHasConfig_DoesNotContainConfigLink()
    {
        var nav = HtmlRenderer.BuildNavLinks("rw", false, false, hasConfig: true);

        Assert.DoesNotContain("openConfigModal", nav);
        Assert.DoesNotContain("Config", nav);
    }

    [Fact]
    public void RenderDirectory_AdminConfigModal_IsInjected()
    {
        var modal = HtmlRenderer.BuildAdminConfigModal("{\"port\":8080}", "filebeam.exe");
        var html  = HtmlRenderer.RenderDirectory("", new List<DirectoryInfo>(), new List<FileInfo>(),
            role: "admin", adminConfigModal: modal);

        Assert.Contains("cfg-modal", html);
        Assert.Contains("openConfigModal", html);
    }

    [Fact]
    public void RenderDirectory_NoAdminConfigModal_HasNoConfigModal()
    {
        var html = HtmlRenderer.RenderDirectory("", new List<DirectoryInfo>(), new List<FileInfo>(), role: "rw");

        Assert.DoesNotContain("cfg-modal", html);
        Assert.DoesNotContain("openConfigModal", html);
    }
}
