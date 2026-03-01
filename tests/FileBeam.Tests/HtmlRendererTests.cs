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
