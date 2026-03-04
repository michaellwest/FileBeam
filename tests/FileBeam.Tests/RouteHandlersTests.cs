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

    private DefaultHttpContext MakeAdminContext()
    {
        var ctx = MakeContext();
        ctx.Items["fb.role"] = "admin";
        return ctx;
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

    private DefaultHttpContext MakeAdminFormContext(string urlEncodedBody)
    {
        var ctx = MakeAdminContext();
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

    // ── Sort ──────────────────────────────────────────────────────────────────

    private DefaultHttpContext MakeSortContext(string sort, string order)
    {
        var ctx = MakeContext();
        ctx.Request.QueryString = new QueryString($"?sort={sort}&order={order}");
        return ctx;
    }

    [Fact]
    public async Task BrowseDirectory_SortByNameDesc_ListsFilesReversed()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "a.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "z.txt"), "");

        var body = await ReadBodyAsync(_handlers.BrowseDirectory(MakeSortContext("name", "desc"), null));

        var posA = body.IndexOf("a.txt", StringComparison.Ordinal);
        var posZ = body.IndexOf("z.txt", StringComparison.Ordinal);
        Assert.True(posZ < posA, "z.txt should appear before a.txt in descending name sort");
    }

    [Fact]
    public async Task BrowseDirectory_SortBySize_ListsFilesInSizeOrder()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "big.txt"),   new string('x', 1000));
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "small.txt"), "hi");

        var body = await ReadBodyAsync(_handlers.BrowseDirectory(MakeSortContext("size", "desc"), null));

        var posBig   = body.IndexOf("big.txt",   StringComparison.Ordinal);
        var posSmall = body.IndexOf("small.txt", StringComparison.Ordinal);
        Assert.True(posBig < posSmall, "big.txt should appear before small.txt in descending size sort");
    }

    [Fact]
    public async Task BrowseDirectory_DirsAlwaysBeforeFiles()
    {
        Directory.CreateDirectory(Path.Combine(_rootDir, "zzz-dir"));
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "aaa.txt"), "");

        var body = await ReadBodyAsync(_handlers.BrowseDirectory(MakeContext(), null));

        var posDir  = body.IndexOf("zzz-dir", StringComparison.Ordinal);
        var posFile = body.IndexOf("aaa.txt", StringComparison.Ordinal);
        Assert.True(posDir < posFile, "directories should appear before files");
    }

    [Fact]
    public async Task BrowseDirectory_SortHeaders_ContainSortLinks()
    {
        var body = await ReadBodyAsync(_handlers.BrowseDirectory(MakeSortContext("name", "asc"), null));
        Assert.Contains("?sort=name", body);
        Assert.Contains("?sort=size", body);
        Assert.Contains("?sort=date", body);
    }

    // ── DeleteFile ────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteFile_NoSubpath_Returns400()
    {
        var result = _handlers.DeleteFile(MakeAdminContext(), null);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public void DeleteFile_FileNotFound_Returns404()
    {
        var result = _handlers.DeleteFile(MakeAdminContext(), "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/shadow")]
    public void DeleteFile_PathTraversal_Returns403(string subpath)
    {
        var result = _handlers.DeleteFile(MakeAdminContext(), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task DeleteFile_ExistingFile_DeletesFileAndRedirects()
    {
        var file = Path.Combine(_rootDir, "todelete.txt");
        await File.WriteAllTextAsync(file, "bye");

        var ctx = MakeAdminContext();
        await _handlers.DeleteFile(ctx, "todelete.txt").ExecuteAsync(ctx);

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

        var ctx = MakeAdminContext();
        await _handlers.DeleteFile(ctx, "sub/nested.txt").ExecuteAsync(ctx);

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
        var result = await _handlers.RenameFile(MakeAdminContext(), null);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public async Task RenameFile_FileNotFound_Returns404()
    {
        var result = await _handlers.RenameFile(MakeAdminFormContext("newname=newfile.txt"), "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    public async Task RenameFile_PathTraversal_Returns403(string subpath)
    {
        var result = await _handlers.RenameFile(MakeAdminFormContext("newname=newfile.txt"), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task RenameFile_ExistingFile_RenamesAndRedirects()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "original.txt"), "data");

        var ctx    = MakeAdminFormContext("newname=renamed.txt");
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

        var result = await _handlers.RenameFile(MakeAdminFormContext("newname=b.txt"), "a.txt");
        Assert.Equal(409, StatusCode(result));
    }

    [Fact]
    public async Task RenameFile_EmptyNewName_Returns400()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "file.txt"), "data");

        var result = await _handlers.RenameFile(MakeAdminFormContext("newname="), "file.txt");
        Assert.Equal(400, StatusCode(result));
    }

    // ── Share links ───────────────────────────────────────────────────────────

    [Fact]
    public void CreateShareLink_NoSubpath_Returns400()
    {
        var result = _handlers.CreateShareLink(MakeAdminContext(), null);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public void CreateShareLink_FileNotFound_Returns404()
    {
        var result = _handlers.CreateShareLink(MakeAdminContext(), "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape.txt")]
    public void CreateShareLink_PathTraversal_Returns403(string subpath)
    {
        var result = _handlers.CreateShareLink(MakeAdminContext(), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task CreateShareLink_ValidFile_Returns200WithUrl()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "shared.txt"), "hello");
        var body = await ReadBodyAsync(_handlers.CreateShareLink(MakeAdminContext(), "shared.txt"));
        Assert.Contains("/s/", body);
    }

    [Fact]
    public void RedeemShareLink_InvalidToken_Returns404()
    {
        var result = _handlers.RedeemShareLink("nonexistenttoken");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task RedeemShareLink_ValidToken_ServesFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "download.txt"), "content");
        var body = await ReadBodyAsync(_handlers.CreateShareLink(MakeAdminContext(), "download.txt"));
        var token = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("url").GetString()!.Split('/')[^1];

        var result = _handlers.RedeemShareLink(token);
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public async Task CreateShareLink_IncludesExpiresIn()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "timed.txt"), "data");
        var ctx = MakeAdminContext();
        ctx.Request.QueryString = new QueryString("?ttl=120");
        var body = await ReadBodyAsync(_handlers.CreateShareLink(ctx, "timed.txt"));
        var doc  = System.Text.Json.JsonDocument.Parse(body).RootElement;
        Assert.Equal(120, doc.GetProperty("expiresIn").GetInt32());
    }

    // ── DownloadZip ───────────────────────────────────────────────────────────

    [Fact]
    public void DownloadZip_DirectoryNotFound_Returns404()
    {
        var result = _handlers.DownloadZip(MakeContext(), "nonexistent");
        Assert.Equal(404, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../etc")]
    public void DownloadZip_PathTraversal_Returns403(string subpath)
    {
        var result = _handlers.DownloadZip(MakeContext(), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task DownloadZip_EmptyDirectory_Returns200WithZip()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_rootDir, "emptydir")).FullName;

        var ctx = MakeContext();
        var result = _handlers.DownloadZip(ctx, "emptydir");
        await result.ExecuteAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("application/zip", ctx.Response.ContentType);
    }

    [Fact]
    public async Task DownloadZip_DirectoryWithFiles_ZipContainsFiles()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_rootDir, "zipme")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sub, "a.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(sub, "b.txt"), "world");

        var ctx = MakeContext();
        var result = _handlers.DownloadZip(ctx, "zipme");
        await result.ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var archive = new System.IO.Compression.ZipArchive(ctx.Response.Body, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("a.txt", entries);
        Assert.Contains("b.txt", entries);
    }

    // ── MkDir ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MkDir_NoSubpath_Returns400()
    {
        var result = _handlers.MkDir(MakeAdminContext(), null);
        Assert.Equal(400, StatusCode(result));
    }

    [Fact]
    public void MkDir_ReadOnly_Returns405()
    {
        var h = new RouteHandlers(_rootDir, _uploadDir, _watcher, isReadOnly: true);
        var result = h.MkDir(MakeContext(), "newfolder");
        Assert.Equal(405, StatusCode(result));
    }

    [Fact]
    public async Task MkDir_NewFolder_CreatesDirectoryAndRedirects()
    {
        var ctx = MakeAdminContext();
        var result = _handlers.MkDir(ctx, "newfolder");

        Assert.Equal(302, await ExecuteAsync(result));
        Assert.True(Directory.Exists(Path.Combine(_rootDir, "newfolder")));
    }

    [Fact]
    public async Task MkDir_NestedFolder_RedirectsToBrowsePath()
    {
        Directory.CreateDirectory(Path.Combine(_rootDir, "docs"));
        var ctx = MakeAdminContext();
        var result = _handlers.MkDir(ctx, "docs/reports");

        await result.ExecuteAsync(ctx);
        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.Contains("/browse/docs/reports", ctx.Response.Headers.Location.ToString());
        Assert.True(Directory.Exists(Path.Combine(_rootDir, "docs", "reports")));
    }

    [Fact]
    public void MkDir_AlreadyExists_Returns409()
    {
        Directory.CreateDirectory(Path.Combine(_rootDir, "existing"));
        var result = _handlers.MkDir(MakeAdminContext(), "existing");
        Assert.Equal(409, StatusCode(result));
    }

    [Fact]
    public void MkDir_MissingParent_Returns404()
    {
        var result = _handlers.MkDir(MakeAdminContext(), "nonexistent/child");
        Assert.Equal(404, StatusCode(result));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../etc")]
    public void MkDir_PathTraversal_Returns403(string subpath)
    {
        var result = _handlers.MkDir(MakeAdminContext(), subpath);
        Assert.Equal(403, StatusCode(result));
    }

    // ── BrowseMyUploads ───────────────────────────────────────────────────────

    [Fact]
    public void BrowseMyUploads_WithoutPerSender_Returns404()
    {
        // handlers constructed without perSender — feature unavailable
        var result = _handlers.BrowseMyUploads(MakeContext());
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task BrowseMyUploads_WithPerSender_RwUser_Returns200()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        // Simulate Basic Auth header for username "alice"
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";

        var result = handlers.BrowseMyUploads(ctx);
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public void BrowseMyUploads_WithPerSender_RoUser_Returns403()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        ctx.Items["fb.role"] = "ro";

        var result = handlers.BrowseMyUploads(ctx);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BrowseMyUploads_WithPerSender_WoUser_Returns200()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        ctx.Items["fb.role"] = "wo";

        var result = handlers.BrowseMyUploads(ctx);
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public async Task BrowseMyUploads_PathTraversal_Returns403()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";

        var result = handlers.BrowseMyUploads(ctx, subpath: "../../etc");
        Assert.Equal(403, StatusCode(result));
    }

    // ── DownloadMyUpload ──────────────────────────────────────────────────────

    [Fact]
    public void DownloadMyUpload_WithoutPerSender_Returns404()
    {
        var result = _handlers.DownloadMyUpload(MakeContext(), "file.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task DownloadMyUpload_ExistingFile_WoUser_Returns200()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        ctx.Items["fb.role"] = "wo";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";

        // Create a file in alice's sender subfolder
        var senderDir = Path.Combine(_uploadDir, "alice");
        Directory.CreateDirectory(senderDir);
        await File.WriteAllTextAsync(Path.Combine(senderDir, "doc.txt"), "data");

        var result = handlers.DownloadMyUpload(ctx, "doc.txt");
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public async Task DownloadMyUpload_FileNotFound_Returns404()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";

        var result = handlers.DownloadMyUpload(ctx, "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    // ── BrowseAdminUploads ────────────────────────────────────────────────────

    [Fact]
    public void BrowseAdminUploads_NonAdmin_Returns403()
    {
        var result = _handlers.BrowseAdminUploads(MakeContext());
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task BrowseAdminUploads_Admin_Returns200()
    {
        var result = _handlers.BrowseAdminUploads(MakeAdminContext());
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public void BrowseAdminUploads_PathTraversal_Returns403()
    {
        var result = _handlers.BrowseAdminUploads(MakeAdminContext(), "../../etc");
        Assert.Equal(403, StatusCode(result));
    }

    // ── DownloadAdminUpload ───────────────────────────────────────────────────

    [Fact]
    public void DownloadAdminUpload_NonAdmin_Returns403()
    {
        var result = _handlers.DownloadAdminUpload(MakeContext(), "file.txt");
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task DownloadAdminUpload_ExistingFile_Admin_Returns200()
    {
        await File.WriteAllTextAsync(Path.Combine(_uploadDir, "report.txt"), "data");

        var result = _handlers.DownloadAdminUpload(MakeAdminContext(), "report.txt");
        Assert.Equal(200, await ExecuteAsync(result));
    }

    [Fact]
    public void DownloadAdminUpload_FileNotFound_Returns404()
    {
        var result = _handlers.DownloadAdminUpload(MakeAdminContext(), "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public void DownloadAdminUpload_PathTraversal_Returns403()
    {
        var result = _handlers.DownloadAdminUpload(MakeAdminContext(), "../../etc/passwd");
        Assert.Equal(403, StatusCode(result));
    }

    // ── Revocation endpoints ──────────────────────────────────────────────────

    [Fact]
    public void RevokeUser_NonAdmin_Returns403()
    {
        var result = _handlers.RevokeUser(MakeContext(), "alice");
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task RevokeUser_Admin_Returns200AndAppearsInList()
    {
        var revStore = new RevocationStore();
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, revocationStore: revStore);

        var revResult = handlers.RevokeUser(MakeAdminContext(), "alice");
        Assert.Equal(200, await ExecuteAsync(revResult));
        Assert.True(revStore.IsUserRevoked("alice"));

        // ListRevocations should include "alice"
        var listResult = handlers.ListRevocations(MakeAdminContext());
        var body = await ReadBodyAsync(listResult);
        Assert.Contains("alice", body);
    }

    [Fact]
    public async Task UnrevokeUser_Admin_RemovesFromList()
    {
        var revStore = new RevocationStore();
        revStore.RevokeUser("bob");
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, revocationStore: revStore);

        await ExecuteAsync(handlers.UnrevokeUser(MakeAdminContext(), "bob"));
        Assert.False(revStore.IsUserRevoked("bob"));
    }

    [Fact]
    public void RevokeIp_NonAdmin_Returns403()
    {
        var result = _handlers.RevokeIp(MakeContext(), "1.2.3.4");
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task RevokeIp_Admin_Returns200AndAppearsInList()
    {
        var revStore = new RevocationStore();
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, revocationStore: revStore);

        await ExecuteAsync(handlers.RevokeIp(MakeAdminContext(), "1.2.3.4"));
        Assert.True(revStore.IsIpRevoked("1.2.3.4"));

        var body = await ReadBodyAsync(handlers.ListRevocations(MakeAdminContext()));
        Assert.Contains("1.2.3.4", body);
    }

    [Fact]
    public void ListRevocations_NonAdmin_Returns403()
    {
        var result = _handlers.ListRevocations(MakeContext());
        Assert.Equal(403, StatusCode(result));
    }

    // ── InfoMyUpload ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InfoMyUpload_NoPerSender_Returns404()
    {
        // _handlers constructed without perSender
        var result = await _handlers.InfoMyUpload(MakeContext(), "file.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task InfoMyUpload_RoRole_Returns403()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        ctx.Items["fb.role"] = "ro";
        var result = await handlers.InfoMyUpload(ctx, "file.txt");
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task InfoMyUpload_FileNotFound_Returns404()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";
        var result = await handlers.InfoMyUpload(ctx, "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public async Task InfoMyUpload_ExistingFile_ReturnsJson()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";

        var senderDir = Path.Combine(_uploadDir, "alice");
        Directory.CreateDirectory(senderDir);
        await File.WriteAllTextAsync(Path.Combine(senderDir, "note.txt"), "hello world");

        var body = await ReadBodyAsync(await handlers.InfoMyUpload(ctx, "note.txt"));
        Assert.Contains("note.txt", body);
        Assert.Contains("sizeBytes", body);
        Assert.Contains("mimeType", body);
        Assert.Contains("sha256", body);
    }

    // ── DeleteMyUpload ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteMyUpload_NoPerSender_Returns404()
    {
        var result = _handlers.DeleteMyUpload(MakeContext(), "file.txt");
        Assert.Equal(404, StatusCode(result));
    }

    [Fact]
    public void DeleteMyUpload_RoRole_Returns403()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        ctx.Items["fb.role"] = "ro";
        var result = handlers.DeleteMyUpload(ctx, "file.txt");
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task DeleteMyUpload_DeletesFileAndRedirects()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";

        var senderDir = Path.Combine(_uploadDir, "alice");
        Directory.CreateDirectory(senderDir);
        var file = Path.Combine(senderDir, "todelete.txt");
        await File.WriteAllTextAsync(file, "bye");

        await handlers.DeleteMyUpload(ctx, "todelete.txt").ExecuteAsync(ctx);

        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.False(File.Exists(file));
        Assert.Contains("/my-uploads", ctx.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task DeleteMyUpload_NestedFile_RedirectsToMyUploadsParent()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, perSender: true);
        var ctx = MakeContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pass"));
        ctx.Request.Headers.Authorization = $"Basic {encoded}";

        var senderDir = Path.Combine(_uploadDir, "alice", "sub");
        Directory.CreateDirectory(senderDir);
        var file = Path.Combine(senderDir, "nested.txt");
        await File.WriteAllTextAsync(file, "content");

        await handlers.DeleteMyUpload(ctx, "sub/nested.txt").ExecuteAsync(ctx);

        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.Contains("/my-uploads/browse/sub", ctx.Response.Headers.Location.ToString());
        Assert.False(File.Exists(file));
    }

    // ── DeleteAdminUpload ─────────────────────────────────────────────────────

    [Fact]
    public void DeleteAdminUpload_NonAdmin_Returns403()
    {
        var result = _handlers.DeleteAdminUpload(MakeContext(), "file.txt");
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task DeleteAdminUpload_DeletesFileAndRedirects()
    {
        var file = Path.Combine(_uploadDir, "admin-del.txt");
        await File.WriteAllTextAsync(file, "bye");

        var ctx = MakeAdminContext();
        await _handlers.DeleteAdminUpload(ctx, "admin-del.txt").ExecuteAsync(ctx);

        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.False(File.Exists(file));
        Assert.Contains("/admin/uploads", ctx.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task DeleteAdminUpload_NestedFile_RedirectsToAdminParent()
    {
        var subDir = Path.Combine(_uploadDir, "sender1");
        Directory.CreateDirectory(subDir);
        var file = Path.Combine(subDir, "report.txt");
        await File.WriteAllTextAsync(file, "data");

        var ctx = MakeAdminContext();
        await _handlers.DeleteAdminUpload(ctx, "sender1/report.txt").ExecuteAsync(ctx);

        Assert.Equal(302, ctx.Response.StatusCode);
        Assert.Contains("/admin/uploads/browse/sender1", ctx.Response.Headers.Location.ToString());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeleteAdminUpload_PathTraversal_Returns403()
    {
        var result = _handlers.DeleteAdminUpload(MakeAdminContext(), "../../etc/passwd");
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public void DeleteAdminUpload_FileNotFound_Returns404()
    {
        var result = _handlers.DeleteAdminUpload(MakeAdminContext(), "missing.txt");
        Assert.Equal(404, StatusCode(result));
    }

    // ── ListShareTokens ───────────────────────────────────────────────────────

    [Fact]
    public void ListShareTokens_NonAdmin_Returns403()
    {
        var result = _handlers.ListShareTokens(MakeContext());
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task ListShareTokens_Admin_ReturnsCreatorField()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootDir, "report.txt"), "data");

        // Create a share link as "alice"
        var shareCtx = MakeAdminContext();
        shareCtx.Items["fb.user"] = "alice";
        shareCtx.Request.QueryString = QueryString.Empty;
        _handlers.CreateShareLink(shareCtx, "report.txt");

        var body = await ReadBodyAsync(_handlers.ListShareTokens(MakeAdminContext()));
        Assert.Contains("alice", body);
        Assert.Contains("tokenPrefix", body);
        Assert.Contains("expiresIn", body);
    }

    // ── GetAdminConfig ────────────────────────────────────────────────────────

    [Fact]
    public void GetAdminConfig_NonAdmin_Returns403()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher,
            configJson: "{\"port\":8080}", cliCommand: "filebeam.exe --download /srv");

        var result = handlers.GetAdminConfig(MakeContext());
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task GetAdminConfig_Admin_ReturnsJson()
    {
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher,
            configJson: "{\"port\":8080}", cliCommand: "filebeam.exe --download /srv");

        var body = await ReadBodyAsync(handlers.GetAdminConfig(MakeAdminContext()));
        Assert.Contains("\"port\"", body);
        Assert.Contains("8080", body);
    }

    [Fact]
    public void GetAdminConfig_NoConfigJson_Returns501()
    {
        // Default handlers have empty configJson
        var result = _handlers.GetAdminConfig(MakeAdminContext());
        Assert.Equal(501, StatusCode(result));
    }

    // ── GetAdminSessions ──────────────────────────────────────────────────────

    [Fact]
    public void GetAdminSessions_NonAdmin_Returns403()
    {
        var registry = new SessionRegistry();
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, sessionRegistry: registry);

        var result = handlers.GetAdminSessions(MakeContext());
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task GetAdminSessions_Admin_ReturnsHtml()
    {
        var registry = new SessionRegistry();
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher, sessionRegistry: registry);

        var body = await ReadBodyAsync(handlers.GetAdminSessions(MakeAdminContext()));
        Assert.Contains("Sessions", body);
        Assert.Contains("No active invite sessions", body);
    }

    // ── RevokeSession ─────────────────────────────────────────────────────────

    [Fact]
    public void RevokeSession_NonAdmin_Returns403()
    {
        var store    = new InviteStore();
        var registry = new SessionRegistry();
        var invite   = store.Create("Alice", "rw", null, "admin");
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher,
            inviteStore: store, sessionRegistry: registry);

        var result = handlers.RevokeSession(MakeContext(), invite.Id);
        Assert.Equal(403, StatusCode(result));
    }

    [Fact]
    public async Task RevokeSession_Admin_RevokesAndRedirects()
    {
        var store    = new InviteStore();
        var registry = new SessionRegistry();
        var invite   = store.Create("Alice", "rw", null, "admin");
        var handlers = new RouteHandlers(_rootDir, _uploadDir, _watcher,
            inviteStore: store, sessionRegistry: registry);

        registry.Touch(invite.Id, invite.FriendlyName, invite.Role, "10.0.0.1", "bearer");

        var result = handlers.RevokeSession(MakeAdminContext(), invite.Id);

        // Invite is revoked
        Assert.True(store.TryGet(invite.Id, out var revoked));
        Assert.False(revoked!.IsActive);
        // Session registry is cleared
        Assert.Empty(registry.GetActive(store));
        // Response redirects to /admin/sessions
        Assert.Equal(302, await ExecuteAsync(result));
    }
}
