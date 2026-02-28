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
}

// Convenience extension to avoid creating real files on disk for simple size tests
file static class FileInfoExtensions
{
    public static void WriteAllText(this FileInfo fi, string content)
        => File.WriteAllText(fi.FullName, content);
}
