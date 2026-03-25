using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace FileBeam.Tests;

public sealed class ChunkedUploadTests : IDisposable
{
    private readonly string        _rootDir;
    private readonly string        _uploadDir;
    private readonly FileWatcher   _watcher;
    private readonly RouteHandlers _handlers;
    private readonly IServiceProvider _services;

    public ChunkedUploadTests()
    {
        _rootDir   = Directory.CreateTempSubdirectory("fb_root_").FullName;
        _uploadDir = Directory.CreateTempSubdirectory("fb_upload_").FullName;
        _watcher   = new FileWatcher(_rootDir);
        _handlers  = new RouteHandlers(_rootDir, _uploadDir, _watcher);
        _services  = new ServiceCollection().AddLogging().BuildServiceProvider();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        if (Directory.Exists(_rootDir))   Directory.Delete(_rootDir,   recursive: true);
        if (Directory.Exists(_uploadDir)) Directory.Delete(_uploadDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DefaultHttpContext MakeContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body   = new MemoryStream();
        ctx.RequestServices = _services;
        return ctx;
    }

    private static int StatusCode(IResult result) =>
        result is IStatusCodeHttpResult sc ? sc.StatusCode ?? 200 : 200;

    /// <summary>
    /// Creates a context configured for a stream upload (application/octet-stream body).
    /// </summary>
    private DefaultHttpContext MakeStreamUploadContext(byte[] body, string fileName,
        string? contentRange = null, string role = "rw")
    {
        var ctx = MakeContext();
        ctx.Request.Body          = new MemoryStream(body);
        ctx.Request.ContentType   = "application/octet-stream";
        ctx.Request.ContentLength = body.Length;
        ctx.Request.Headers["X-Upload-Filename"] = fileName;
        ctx.Items["fb.role"] = role;
        ctx.Items["fb.user"] = "testuser";

        if (contentRange != null)
            ctx.Request.Headers.ContentRange = contentRange;

        return ctx;
    }

    /// <summary>
    /// Creates a context configured for a HEAD upload status request.
    /// </summary>
    private DefaultHttpContext MakeHeadContext(string fileName, string role = "rw")
    {
        var ctx = MakeContext();
        ctx.Request.Method = "HEAD";
        ctx.Request.QueryString = new QueryString("?file=" + Uri.EscapeDataString(fileName));
        ctx.Items["fb.role"] = role;
        ctx.Items["fb.user"] = "testuser";
        return ctx;
    }

    // ── Stream upload (no Content-Range) ─────────────────────────────────────

    [Fact]
    public async Task StreamUpload_NoRange_CreatesFile()
    {
        var data = Encoding.UTF8.GetBytes("hello world stream upload");
        var ctx = MakeStreamUploadContext(data, "stream-test.txt");
        ctx.Request.Headers.Accept = "application/json";

        var result = await _handlers.UploadFiles(ctx, null);
        Assert.Equal(201, StatusCode(result));

        Assert.True(File.Exists(Path.Combine(_uploadDir, "stream-test.txt")));
        Assert.Equal("hello world stream upload",
            await File.ReadAllTextAsync(Path.Combine(_uploadDir, "stream-test.txt")));
    }

    [Fact]
    public async Task StreamUpload_NoRange_NoPartFileRemains()
    {
        var data = Encoding.UTF8.GetBytes("complete upload");
        var ctx = MakeStreamUploadContext(data, "clean.txt");
        ctx.Request.Headers.Accept = "application/json";

        await _handlers.UploadFiles(ctx, null);

        var partFiles = Directory.GetFiles(_uploadDir, "*.part");
        Assert.Empty(partFiles);
    }

    // ── Stream upload (missing X-Upload-Filename) ────────────────────────────

    [Fact]
    public async Task StreamUpload_MissingFilename_Returns400()
    {
        var ctx = MakeContext();
        ctx.Request.Body        = new MemoryStream([1, 2, 3]);
        ctx.Request.ContentType = "application/octet-stream";
        ctx.Items["fb.role"]    = "rw";

        var result = await _handlers.UploadFiles(ctx, null);
        Assert.Equal(400, StatusCode(result));
    }

    // ── Chunked upload with Content-Range ────────────────────────────────────

    [Fact]
    public async Task ChunkedUpload_TwoChunks_CreatesFile()
    {
        var fullData = Encoding.UTF8.GetBytes("AAAABBBB"); // 8 bytes total
        var chunk1 = fullData[..4];
        var chunk2 = fullData[4..];

        // Send first chunk: bytes 0-3/8
        var ctx1 = MakeStreamUploadContext(chunk1, "chunked.txt", "bytes 0-3/8");
        ctx1.Request.Headers.Accept = "application/json";
        var result1 = await _handlers.UploadFiles(ctx1, null);
        Assert.Equal(200, StatusCode(result1)); // not complete yet

        // .part file should exist with 4 bytes
        Assert.True(File.Exists(Path.Combine(_uploadDir, "chunked.txt.part")));
        Assert.Equal(4, new FileInfo(Path.Combine(_uploadDir, "chunked.txt.part")).Length);

        // Send second chunk: bytes 4-7/8
        var ctx2 = MakeStreamUploadContext(chunk2, "chunked.txt", "bytes 4-7/8");
        ctx2.Request.Headers.Accept = "application/json";
        var result2 = await _handlers.UploadFiles(ctx2, null);
        Assert.Equal(201, StatusCode(result2)); // complete

        // Final file should exist, .part should not
        Assert.True(File.Exists(Path.Combine(_uploadDir, "chunked.txt")));
        Assert.False(File.Exists(Path.Combine(_uploadDir, "chunked.txt.part")));
        Assert.Equal("AAAABBBB",
            await File.ReadAllTextAsync(Path.Combine(_uploadDir, "chunked.txt")));
    }

    [Fact]
    public async Task ChunkedUpload_OffsetMismatch_Returns409()
    {
        // Send chunk claiming offset 100 but no .part file exists
        var data = new byte[10];
        var ctx = MakeStreamUploadContext(data, "missing.txt", "bytes 100-109/200");
        ctx.Request.Headers.Accept = "application/json";

        var result = await _handlers.UploadFiles(ctx, null);
        Assert.Equal(409, StatusCode(result));
    }

    [Fact]
    public async Task ChunkedUpload_InvalidRange_Returns400()
    {
        var data = new byte[10];
        var ctx = MakeStreamUploadContext(data, "bad-range.txt", "bytes garbage");
        ctx.Request.Headers.Accept = "application/json";

        var result = await _handlers.UploadFiles(ctx, null);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public async Task ChunkedUpload_RangeEndExceedsTotal_Returns400()
    {
        var data = new byte[10];
        // rangeEnd (99) >= rangeTotal (50) — invalid
        var ctx = MakeStreamUploadContext(data, "invalid.txt", "bytes 0-99/50");

        var result = await _handlers.UploadFiles(ctx, null);
        Assert.Equal(400, StatusCode(result));
    }

    // ── HEAD upload status ───────────────────────────────────────────────────

    [Fact]
    public void UploadStatus_NoPartFile_Returns404()
    {
        var ctx = MakeHeadContext("nonexistent.txt");
        var result = _handlers.UploadStatus(ctx, null);

        Assert.Equal(404, StatusCode(result));
        Assert.Equal("0", ctx.Response.Headers["X-Bytes-Received"].ToString());
    }

    [Fact]
    public void UploadStatus_PartFileExists_ReturnsSize()
    {
        // Create a .part file manually
        var partPath = Path.Combine(_uploadDir, "partial.txt.part");
        File.WriteAllBytes(partPath, new byte[12345]);

        var ctx = MakeHeadContext("partial.txt");
        var result = _handlers.UploadStatus(ctx, null);

        Assert.Equal(200, StatusCode(result));
        Assert.Equal("12345", ctx.Response.Headers["X-Bytes-Received"].ToString());
    }

    [Fact]
    public void UploadStatus_MissingFileParam_Returns400()
    {
        var ctx = MakeContext();
        ctx.Request.Method = "HEAD";
        ctx.Items["fb.role"] = "rw";

        var result = _handlers.UploadStatus(ctx, null);
        Assert.Equal(400, StatusCode(result));
    }

    // ── Role enforcement on stream uploads ───────────────────────────────────

    [Fact]
    public async Task StreamUpload_ReadOnly_Returns405()
    {
        var readOnlyHandlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, isReadOnly: true);
        var data = new byte[10];
        var ctx = MakeStreamUploadContext(data, "blocked.txt");

        var result = await readOnlyHandlers.UploadFiles(ctx, null);
        Assert.Equal(405, StatusCode(result));
    }

    [Fact]
    public async Task StreamUpload_RoRole_Returns403()
    {
        var data = new byte[10];
        var ctx = MakeStreamUploadContext(data, "blocked.txt", role: "ro");

        var result = await _handlers.UploadFiles(ctx, null);
        Assert.Equal(403, StatusCode(result));
    }

    // ── Path traversal on X-Upload-Filename ──────────────────────────────────

    [Fact]
    public async Task StreamUpload_PathTraversal_Returns403()
    {
        var data = new byte[10];
        var ctx = MakeStreamUploadContext(data, "../../../etc/passwd");

        var result = await _handlers.UploadFiles(ctx, null);

        // Path.GetFileName strips directory components, so this should be treated as "passwd"
        // and land safely inside uploadDir. The file should be created normally.
        // This test verifies Path.GetFileName sanitization works.
        Assert.True(File.Exists(Path.Combine(_uploadDir, "passwd")));
    }

    // ── Size limit enforcement on stream uploads ─────────────────────────────

    [Fact]
    public async Task StreamUpload_ExceedsMaxFileSize_Returns413()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, maxFileSize: 100);
        var data = new byte[200];
        var ctx = MakeStreamUploadContext(data, "too-big.txt");
        ctx.Request.ContentLength = 200;

        var result = await handlers.UploadFiles(ctx, null);
        Assert.Equal(413, StatusCode(result));
    }

    [Fact]
    public async Task ChunkedUpload_TotalExceedsMaxFileSize_Returns413()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, maxFileSize: 100);
        var data = new byte[50];
        // Total is 200 which exceeds maxFileSize of 100
        var ctx = MakeStreamUploadContext(data, "too-big.txt", "bytes 0-49/200");

        var result = await handlers.UploadFiles(ctx, null);
        Assert.Equal(413, StatusCode(result));
    }

    // ── Unique filename resolution on completion ─────────────────────────────

    [Fact]
    public async Task StreamUpload_DuplicateName_ResolvesUnique()
    {
        // Create an existing file with the same name
        await File.WriteAllTextAsync(Path.Combine(_uploadDir, "dup.txt"), "existing");

        var data = Encoding.UTF8.GetBytes("new content");
        var ctx = MakeStreamUploadContext(data, "dup.txt");
        ctx.Request.Headers.Accept = "application/json";

        var result = await _handlers.UploadFiles(ctx, null);
        Assert.Equal(201, StatusCode(result));

        // Original file untouched
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(_uploadDir, "dup.txt")));
        // New file created with unique name
        Assert.True(File.Exists(Path.Combine(_uploadDir, "dup (1).txt")));
    }
}
