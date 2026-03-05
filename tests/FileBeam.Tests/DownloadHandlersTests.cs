using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FileBeam.Tests;

/// <summary>Tests for ZIP download safeguards (size guard + concurrency cap).</summary>
public sealed class DownloadHandlersTests : IDisposable
{
    private readonly string      _rootDir;
    private readonly string      _uploadDir;
    private readonly IServiceProvider _services;

    public DownloadHandlersTests()
    {
        _rootDir   = Directory.CreateTempSubdirectory("fb_dl_root_").FullName;
        _uploadDir = Directory.CreateTempSubdirectory("fb_dl_up_").FullName;
        _services  = new ServiceCollection().AddLogging().BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))   Directory.Delete(_rootDir,   recursive: true);
        if (Directory.Exists(_uploadDir)) Directory.Delete(_uploadDir, recursive: true);
    }

    private DefaultHttpContext MakeContext(string role = "admin")
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body    = new MemoryStream();
        ctx.RequestServices  = _services;
        ctx.Items["fb.role"] = role;
        return ctx;
    }

    private static int StatusCode(IResult result) =>
        result is IStatusCodeHttpResult sc ? sc.StatusCode ?? 200 : 200;

    // ── Size guard ────────────────────────────────────────────────────────────

    [Fact]
    public void DownloadZip_DirectoryOverMaxZipBytes_Returns413()
    {
        File.WriteAllText(Path.Combine(_rootDir, "big.txt"), "0123456789"); // 10 bytes

        using var watcher = new FileWatcher(_rootDir);
        var hCtx = new HandlerContext(_rootDir, _uploadDir, watcher, maxZipBytes: 5);
        var dl   = new DownloadHandlers(hCtx);

        var result = dl.DownloadZip(MakeContext(), subpath: null);

        Assert.Equal(StatusCodes.Status413RequestEntityTooLarge, StatusCode(result));
    }

    [Fact]
    public void DownloadZip_DirectoryUnderMaxZipBytes_DoesNotReturn413Or503()
    {
        File.WriteAllText(Path.Combine(_rootDir, "small.txt"), "hello"); // 5 bytes

        using var watcher = new FileWatcher(_rootDir);
        var hCtx = new HandlerContext(_rootDir, _uploadDir, watcher, maxZipBytes: 1024);
        var dl   = new DownloadHandlers(hCtx);

        var result = dl.DownloadZip(MakeContext(), subpath: null);

        Assert.NotEqual(StatusCodes.Status413RequestEntityTooLarge, StatusCode(result));
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable,    StatusCode(result));
    }

    // ── Concurrency cap ───────────────────────────────────────────────────────

    [Fact]
    public void DownloadZip_SemaphoreExhausted_Returns503()
    {
        using var watcher = new FileWatcher(_rootDir);
        var hCtx = new HandlerContext(_rootDir, _uploadDir, watcher, maxConcurrentZips: 1);
        var dl   = new DownloadHandlers(hCtx);

        // Consume the one available slot before the handler tries to acquire it
        Assert.True(hCtx.ZipSemaphore!.Wait(0), "Semaphore should have one slot available");

        var result = dl.DownloadZip(MakeContext(), subpath: null);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusCode(result));
    }

    // ── Semaphore creation ────────────────────────────────────────────────────

    [Fact]
    public void HandlerContext_MaxConcurrentZipsZero_ZipSemaphoreIsNull()
    {
        using var watcher = new FileWatcher(_rootDir);
        var ctx = new HandlerContext(_rootDir, _uploadDir, watcher, maxConcurrentZips: 0);

        Assert.Null(ctx.ZipSemaphore);
    }

    [Fact]
    public void HandlerContext_MaxConcurrentZipsPositive_ZipSemaphoreNotNull()
    {
        using var watcher = new FileWatcher(_rootDir);
        var ctx = new HandlerContext(_rootDir, _uploadDir, watcher, maxConcurrentZips: 3);

        Assert.NotNull(ctx.ZipSemaphore);
        Assert.Equal(3, ctx.ZipSemaphore.CurrentCount);
    }
}
