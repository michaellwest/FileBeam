using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileBeam.Tests;

public sealed class AuditLogViewerTests : IDisposable
{
    private readonly string        _rootDir;
    private readonly string        _uploadDir;
    private readonly FileWatcher   _watcher;
    private readonly IServiceProvider _services;

    public AuditLogViewerTests()
    {
        _rootDir   = Directory.CreateTempSubdirectory("fb_audit_root_").FullName;
        _uploadDir = _rootDir;
        _watcher   = new FileWatcher(_rootDir);
        _services  = new ServiceCollection().AddLogging().BuildServiceProvider();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RouteHandlers MakeHandlers(string? auditLogPath = null) =>
        new RouteHandlers(_rootDir, _uploadDir, _watcher, auditLogPath: auditLogPath);

    private DefaultHttpContext MakeAdminContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body   = new MemoryStream();
        ctx.RequestServices = _services;
        ctx.Items["fb.role"] = "admin";
        return ctx;
    }

    private DefaultHttpContext MakeRwContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body   = new MemoryStream();
        ctx.RequestServices = _services;
        ctx.Items["fb.role"] = "rw";
        return ctx;
    }

    private static int StatusCode(IResult result) =>
        result is IStatusCodeHttpResult sc ? sc.StatusCode ?? 200 : 200;

    private static string LogLine(string action, string path, long bytes = 0, int status = 200, string? user = null) =>
        JsonSerializer.Serialize(new
        {
            timestamp   = "2026-01-01T00:00:00Z",
            username    = user,
            remote_ip   = "127.0.0.1",
            action,
            path,
            bytes,
            status_code = status
        });

    // ── Route: 404 when no audit log configured ───────────────────────────────

    [Fact]
    public void GetAuditLog_Returns404_WhenAuditLogPathIsNull()
    {
        var handlers = MakeHandlers(auditLogPath: null);
        var result   = handlers.GetAuditLog(MakeAdminContext());

        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public void GetAuditLog_Returns404_WhenAuditLogPathIsStdout()
    {
        // "-" means stdout — no file to read
        var handlers = MakeHandlers(auditLogPath: "-");
        var result   = handlers.GetAuditLog(MakeAdminContext());

        Assert.Equal(404, StatusCode(result));
    }

    // ── Route: 403 for non-admin ──────────────────────────────────────────────

    [Fact]
    public void GetAuditLog_Returns403_WhenNotAdmin()
    {
        var logPath  = Path.Combine(_rootDir, "audit.ndjson");
        var handlers = MakeHandlers(auditLogPath: logPath);
        var result   = handlers.GetAuditLog(MakeRwContext());

        Assert.Equal(403, StatusCode(result));
    }

    // ── Route: 200 with HTML when file exists ─────────────────────────────────

    [Fact]
    public async Task GetAuditLog_Returns200Html_WhenLogExists()
    {
        var logPath = Path.Combine(_rootDir, "audit.ndjson");
        await File.WriteAllTextAsync(logPath, LogLine("download", "/file.txt") + "\n");

        var handlers = MakeHandlers(auditLogPath: logPath);
        var ctx      = MakeAdminContext();
        var result   = handlers.GetAuditLog(ctx);

        await result.ExecuteAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("Audit Log", body);
        Assert.Contains("download", body);
        Assert.Contains("/file.txt", body);
    }

    [Fact]
    public async Task GetAuditLog_Returns200Html_WhenFileDoesNotExist()
    {
        var logPath  = Path.Combine(_rootDir, "nonexistent.ndjson");
        var handlers = MakeHandlers(auditLogPath: logPath);
        var ctx      = MakeAdminContext();
        var result   = handlers.GetAuditLog(ctx);

        await result.ExecuteAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("No audit entries found", body);
    }

    // ── Route: malformed lines skipped gracefully ─────────────────────────────

    [Fact]
    public async Task GetAuditLog_SkipsMalformedLines()
    {
        var logPath = Path.Combine(_rootDir, "audit.ndjson");
        var lines = new[]
        {
            "not-json-at-all",
            LogLine("upload", "/good.txt"),
            "{\"broken\":",          // truncated JSON
            LogLine("delete", "/other.txt")
        };
        await File.WriteAllLinesAsync(logPath, lines);

        var handlers = MakeHandlers(auditLogPath: logPath);
        var ctx      = MakeAdminContext();
        var result   = handlers.GetAuditLog(ctx);

        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        Assert.Contains("/good.txt", body);
        Assert.Contains("/other.txt", body);
        Assert.DoesNotContain("not-json-at-all", body);
    }

    // ── Route: limits to last 200 lines ──────────────────────────────────────

    [Fact]
    public async Task GetAuditLog_ShowsLast200Lines_WhenFileHasMore()
    {
        var logPath = Path.Combine(_rootDir, "audit.ndjson");
        var lines = Enumerable.Range(1, 250)
            .Select(i => LogLine("download", $"/file{i:D3}.txt"))
            .ToArray();
        await File.WriteAllLinesAsync(logPath, lines);

        var handlers = MakeHandlers(auditLogPath: logPath);
        var ctx      = MakeAdminContext();
        var result   = handlers.GetAuditLog(ctx);

        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();

        // First 50 entries should be absent; last 200 should be present
        Assert.DoesNotContain("/file001.txt", body);
        Assert.Contains("/file051.txt", body);
        Assert.Contains("/file250.txt", body);
    }

    // ── HtmlRenderer: RenderAuditLog ─────────────────────────────────────────

    [Fact]
    public void RenderAuditLog_EmptyList_ShowsEmptyMessage()
    {
        var html = HtmlRenderer.RenderAuditLog([]);

        Assert.Contains("No audit entries found", html);
        Assert.DoesNotContain("<tbody>", html);
    }

    [Fact]
    public void RenderAuditLog_WithEntries_ShowsTableAndAllColumns()
    {
        var entries = new List<AuditEntry>
        {
            new("2026-01-01T12:00:00Z", "alice", "10.0.0.1", "download", "/docs/file.pdf", 1024, 200),
            new("2026-01-01T12:01:00Z", null,    "10.0.0.2", "upload",   "/uploads/img.png", 2048, 200)
        };

        var html = HtmlRenderer.RenderAuditLog(entries);

        Assert.Contains("2026-01-01T12:00:00Z", html);
        Assert.Contains("alice", html);
        Assert.Contains("/docs/file.pdf", html);
        Assert.Contains("10.0.0.1", html);
        Assert.Contains("upload", html);
        Assert.Contains("/uploads/img.png", html);
        Assert.Contains("10.0.0.2", html);
    }

    [Fact]
    public void RenderAuditLog_ContainsAutoRefreshMeta()
    {
        var html = HtmlRenderer.RenderAuditLog([]);

        Assert.Contains("http-equiv=\"refresh\"", html);
        Assert.Contains("content=\"30\"", html);
    }

    [Fact]
    public void RenderAuditLog_ShowsMostRecentFirst()
    {
        var entries = new List<AuditEntry>
        {
            new("2026-01-01T10:00:00Z", null, "127.0.0.1", "download", "/first.txt", 0, 200),
            new("2026-01-01T11:00:00Z", null, "127.0.0.1", "download", "/second.txt", 0, 200),
        };

        var html = HtmlRenderer.RenderAuditLog(entries);

        var idxFirst  = html.IndexOf("/first.txt",  StringComparison.Ordinal);
        var idxSecond = html.IndexOf("/second.txt", StringComparison.Ordinal);

        // Most-recent entry (/second.txt) should appear before /first.txt
        Assert.True(idxSecond < idxFirst);
    }

    // ── BuildNavLinks: audit log link ─────────────────────────────────────────

    [Fact]
    public void BuildNavLinks_Admin_HasAuditLog_ShowsAuditLogLink()
    {
        var html = HtmlRenderer.BuildNavLinks("admin", perSender: false, separateUploadDir: false, hasAuditLog: true);

        Assert.Contains("/admin/audit", html);
        Assert.Contains("Audit", html);
    }

    [Fact]
    public void BuildNavLinks_Admin_NoAuditLog_HidesAuditLogLink()
    {
        var html = HtmlRenderer.BuildNavLinks("admin", perSender: false, separateUploadDir: false, hasAuditLog: false);

        Assert.DoesNotContain("/admin/audit", html);
    }

    [Fact]
    public void BuildNavLinks_NonAdmin_HasAuditLog_HidesAuditLogLink()
    {
        var html = HtmlRenderer.BuildNavLinks("rw", perSender: false, separateUploadDir: false, hasAuditLog: true);

        Assert.DoesNotContain("/admin/audit", html);
    }
}
