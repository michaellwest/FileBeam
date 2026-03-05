using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FileBeam.Tests;

/// <summary>
/// Tests for bulk-download and bulk-delete endpoints.
/// </summary>
public sealed class BulkHandlersTests : IDisposable
{
    private readonly string          _rootDir;
    private readonly string          _uploadDir;
    private readonly FileWatcher     _watcher;
    private readonly HandlerContext  _ctx;
    private readonly DownloadHandlers _download;
    private readonly ModifyHandlers   _modify;
    private readonly IServiceProvider _services;

    public BulkHandlersTests()
    {
        _rootDir   = Directory.CreateTempSubdirectory("fb_bulk_root_").FullName;
        _uploadDir = Directory.CreateTempSubdirectory("fb_bulk_upload_").FullName;
        _watcher   = new FileWatcher(_rootDir);
        _ctx       = new HandlerContext(
            _rootDir, _uploadDir, _watcher,
            csrfToken: "test-csrf");
        _download = new DownloadHandlers(_ctx);
        _modify   = new ModifyHandlers(_ctx);
        _services = new ServiceCollection().AddLogging().BuildServiceProvider();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { if (Directory.Exists(_rootDir))   Directory.Delete(_rootDir,   recursive: true); } catch { }
        try { if (Directory.Exists(_uploadDir)) Directory.Delete(_uploadDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DefaultHttpContext MakeJsonBodyContext(string role, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var ctx   = new DefaultHttpContext();
        ctx.Response.Body          = new MemoryStream();
        ctx.RequestServices        = _services;
        ctx.Request.Body           = new MemoryStream(bytes);
        ctx.Request.ContentType    = "application/json";
        ctx.Request.ContentLength  = bytes.Length;
        if (!string.IsNullOrEmpty(role))
            ctx.Items["fb.role"] = role;
        return ctx;
    }

    private static int StatusCode(IResult result) =>
        result is IStatusCodeHttpResult sc ? sc.StatusCode ?? 200 : 200;

    private static string CreateFile(string dir, string name, string content = "data")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── BulkDeleteFiles (serveDir) ────────────────────────────────────────────

    [Fact]
    public async Task BulkDeleteFiles_NonAdmin_Returns403()
    {
        var ctx = MakeJsonBodyContext("rw", """{"paths":["a.txt"]}""");
        var result = await _modify.BulkDeleteFiles(ctx);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BulkDeleteFiles_EmptyPaths_Returns400()
    {
        var ctx = MakeJsonBodyContext("admin", """{"paths":[]}""");
        var result = await _modify.BulkDeleteFiles(ctx);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public async Task BulkDeleteFiles_PathTraversal_Returns403()
    {
        var ctx = MakeJsonBodyContext("admin", """{"paths":["../escape.txt"]}""");
        var result = await _modify.BulkDeleteFiles(ctx);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BulkDeleteFiles_AllFilesFound_DeletesAllAndReturnsJson()
    {
        CreateFile(_rootDir, "one.txt");
        CreateFile(_rootDir, "two.txt");
        var ctx = MakeJsonBodyContext("admin", """{"paths":["one.txt","two.txt"]}""");

        var result = await _modify.BulkDeleteFiles(ctx);

        // Execute to get JSON body
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal(2, json.RootElement.GetProperty("deleted").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("failed").GetInt32());
        Assert.False(File.Exists(Path.Combine(_rootDir, "one.txt")));
        Assert.False(File.Exists(Path.Combine(_rootDir, "two.txt")));
    }

    [Fact]
    public async Task BulkDeleteFiles_MixedExists_ReportsPartialFailures()
    {
        CreateFile(_rootDir, "real.txt");
        var ctx = MakeJsonBodyContext("admin", """{"paths":["real.txt","missing.txt"]}""");

        var result = await _modify.BulkDeleteFiles(ctx);

        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal(1, json.RootElement.GetProperty("deleted").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("failed").GetInt32());
        Assert.False(File.Exists(Path.Combine(_rootDir, "real.txt")));
    }

    // ── BulkDownloadFiles (serveDir) ──────────────────────────────────────────

    [Fact]
    public async Task BulkDownloadFiles_WoRole_Returns403()
    {
        var ctx = MakeJsonBodyContext("wo", """{"paths":["a.txt"]}""");
        var result = await _download.BulkDownloadFiles(ctx);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BulkDownloadFiles_PathTraversal_Returns403()
    {
        var ctx = MakeJsonBodyContext("rw", """{"paths":["../escape.txt"]}""");
        var result = await _download.BulkDownloadFiles(ctx);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BulkDownloadFiles_AllMissing_Returns404()
    {
        var ctx = MakeJsonBodyContext("rw", """{"paths":["nonexistent.txt"]}""");
        var result = await _download.BulkDownloadFiles(ctx);
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task BulkDownloadFiles_ValidFiles_ReturnsZipStream()
    {
        CreateFile(_rootDir, "alpha.txt", "hello");
        CreateFile(_rootDir, "beta.txt",  "world");
        var ctx = MakeJsonBodyContext("rw", """{"paths":["alpha.txt","beta.txt"]}""");

        var result = await _download.BulkDownloadFiles(ctx);

        // Result should be a stream (zip); check it's not an error code
        Assert.False(result is IStatusCodeHttpResult sc && sc.StatusCode >= 400,
            $"Expected streaming result, got status {(result is IStatusCodeHttpResult s ? s.StatusCode : "?")}");
    }

    // ── BulkDeleteAdminUploads (uploadDir) ────────────────────────────────────

    [Fact]
    public async Task BulkDeleteAdminUploads_NonAdmin_Returns403()
    {
        var ctx = MakeJsonBodyContext("rw", """{"paths":["file.txt"]}""");
        var result = await _modify.BulkDeleteAdminUploads(ctx);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BulkDeleteAdminUploads_PathTraversal_Returns403()
    {
        var ctx = MakeJsonBodyContext("admin", """{"paths":["../escape.txt"]}""");
        var result = await _modify.BulkDeleteAdminUploads(ctx);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BulkDeleteAdminUploads_ValidFiles_DeletesAndReturnsJson()
    {
        CreateFile(_uploadDir, "upload1.txt");
        CreateFile(_uploadDir, "upload2.txt");
        var ctx = MakeJsonBodyContext("admin", """{"paths":["upload1.txt","upload2.txt"]}""");

        var result = await _modify.BulkDeleteAdminUploads(ctx);

        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal(2, json.RootElement.GetProperty("deleted").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("failed").GetInt32());
    }

    // ── BulkDownloadAdminUploads (uploadDir) ──────────────────────────────────

    [Fact]
    public async Task BulkDownloadAdminUploads_NonAdmin_Returns403()
    {
        var ctx = MakeJsonBodyContext("rw", """{"paths":["file.txt"]}""");
        var result = await _download.BulkDownloadAdminUploads(ctx);
        Assert.Equal(403, StatusCode(result));
    }
}
