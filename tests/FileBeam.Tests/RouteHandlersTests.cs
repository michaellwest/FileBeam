using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileBeam.Tests;

public sealed class RouteHandlersTests : IDisposable
{
    private readonly string        _rootDir;
    private readonly string        _uploadDir;
    private readonly FileWatcher   _watcher;
    private readonly RouteHandlers _handlers;
    private readonly IServiceProvider _services;

    public RouteHandlersTests()
    {
        _rootDir   = Directory.CreateTempSubdirectory("fb_root_").FullName;
        _uploadDir = Directory.CreateTempSubdirectory("fb_upload_").FullName;
        _watcher   = new FileWatcher(_rootDir);
        _handlers  = new RouteHandlers(_rootDir, _uploadDir, _watcher);

        // IResult.ExecuteAsync needs ILoggerFactory from RequestServices
        _services = new ServiceCollection().AddLogging().BuildServiceProvider();
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
        ctx.Response.Body    = new MemoryStream();
        ctx.RequestServices  = _services;
        return ctx;
    }

    /// <summary>
    /// Reads the status code from the result object directly without executing it.
    /// All typed result types implement IStatusCodeHttpResult; null StatusCode means 200.
    /// </summary>
    private static int StatusCode(IResult result) =>
        result is IStatusCodeHttpResult sc ? sc.StatusCode ?? 200 : 200;

    private async Task<int> ExecuteAsync(IResult result)
    {
        var ctx = MakeContext();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private DefaultHttpContext MakeFormContext(string urlEncodedBody)
    {
        var ctx   = MakeContext();
        var bytes = Encoding.UTF8.GetBytes(urlEncodedBody);
        ctx.Request.Body          = new MemoryStream(bytes);
        ctx.Request.ContentType   = "application/x-www-form-urlencoded";
        ctx.Request.ContentLength = bytes.Length;
        return ctx;
    }

    private async Task<string> ReadBodyAsync(IResult result)
    {
        var ctx = MakeContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(ctx.Response.Body).ReadToEndAsync();
    }

    // ── DownloadFile ──────────────────────────────────────────────────────────

    [Fact]
    public void DownloadFile_NoSubpath_Returns400()
    {
        var result = _handlers.DownloadFile(MakeContext(), null);
        Assert.Equal(400, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape.txt")]
    public void DownloadFile_PathTraversal_Returns403(string subpath)
    {
        var result = _handlers.DownloadFile(MakeContext(), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public void DownloadFile_FileNotFound_Returns404()
    {
        var result = _handlers.DownloadFile(MakeContext(), "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task DownloadFile_ExistingFile_Returns200()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "readme.txt"), "hello");

        var result = _handlers.DownloadFile(MakeContext(), "readme.txt");
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public async Task DownloadFile_ExistingFile_SetsTransferMetadata()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "data.txt"), "content");

        var ctx    = MakeContext();
        var result = _handlers.DownloadFile(ctx, "data.txt");
        await result.ExecuteAsync(ctx);

        Assert.Equal("data.txt", ctx.Items["fb.file"]);
        Assert.IsType<long>(ctx.Items["fb.bytes"]);
    }

    // ── BrowseDirectory / ListDirectory ───────────────────────────────────────

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../etc")]
    public void BrowseDirectory_PathTraversal_Returns403(string subpath)
    {
        var result = _handlers.BrowseDirectory(MakeContext(), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public void BrowseDirectory_DirectoryNotFound_Returns404()
    {
        var result = _handlers.BrowseDirectory(MakeContext(), "nonexistent");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task ListDirectory_RootDirectory_Returns200()
    {
        var result = _handlers.ListDirectory(MakeContext());
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public async Task BrowseDirectory_Subdirectory_Returns200()
    {
        Directory.CreateDirectory(Path.Combine(_rootDir, "docs"));

        var result = _handlers.BrowseDirectory(MakeContext(), "docs");
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public async Task ListDirectory_ListsFilesAndSubdirectories()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "file.txt"), "data");
        Directory.CreateDirectory(Path.Combine(_rootDir, "subdir"));

        var body = await ReadBodyAsync(_handlers.ListDirectory(MakeContext()));

        Assert.Contains("file.txt", body);
        Assert.Contains("subdir",   body);
    }

    // ── DeleteFile ────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteFile_NoSubpath_Returns400()
    {
        var result = _handlers.DeleteFile(null);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public void DeleteFile_FileNotFound_Returns404()
    {
        var result = _handlers.DeleteFile("missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/shadow")]
    public void DeleteFile_PathTraversal_Returns403(string subpath)
    {
        var result = _handlers.DeleteFile(subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task DeleteFile_ExistingFile_DeletesFileAndRedirects()
    {
        var file = Path.Combine(_rootDir, "todelete.txt");
        await File.WriteAllTextAsync(file, "bye");

        var ctx = MakeContext();
        await _handlers.DeleteFile("todelete.txt").ExecuteAsync(ctx);

        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task DeleteFile_NestedFile_RedirectsToParentDirectory()
    {
        var subdir = Path.Combine(_rootDir, "sub");
        Directory.CreateDirectory(subdir);
        var file = Path.Combine(subdir, "nested.txt");
        await File.WriteAllTextAsync(file, "content");

        var ctx = MakeContext();
        await _handlers.DeleteFile("sub/nested.txt").ExecuteAsync(ctx);

        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.Contains("/browse/sub", ctx.Response.Headers.Location.ToString());
        Assert.False(File.Exists(file));
    }

    // ── UploadFiles ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadFiles_ReadOnly_Returns405()
    {
        var readOnlyHandlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, isReadOnly: true);

        var result = await readOnlyHandlers.UploadFiles(MakeContext(), null);
        Assert.Equal(405, StatusCode(result));
    }

    // ── RenameFile ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameFile_NoSubpath_Returns400()
    {
        var result = await _handlers.RenameFile(MakeContext(), null);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public async Task RenameFile_FileNotFound_Returns404()
    {
        var result = await _handlers.RenameFile(MakeFormContext("newname=newfile.txt"), "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    public async Task RenameFile_PathTraversal_Returns403(string subpath)
    {
        var result = await _handlers.RenameFile(MakeFormContext("newname=newfile.txt"), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task RenameFile_ExistingFile_RenamesAndRedirects()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "original.txt"), "data");

        var ctx    = MakeFormContext("newname=renamed.txt");
        var result = await _handlers.RenameFile(ctx, "original.txt");

        Assert.Equal(302, await ExecuteAsync(result));
        Assert.False(File.Exists(Path.Combine(_rootDir, "original.txt")));
        Assert.True(File.Exists(Path.Combine(_rootDir, "renamed.txt")));
    }

    [Fact]
    public async Task RenameFile_TargetAlreadyExists_Returns409()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "b.txt"), "b");

        var result = await _handlers.RenameFile(MakeFormContext("newname=b.txt"), "a.txt");
        Assert.Equal(409, StatusCode(result));
    }

    [Fact]
    public async Task RenameFile_EmptyNewName_Returns400()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "file.txt"), "data");

        var result = await _handlers.RenameFile(MakeFormContext("newname="), "file.txt");
        Assert.Equal(400, StatusCode(result));
    }
}
