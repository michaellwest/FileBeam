namespace FileBeam.Tests;

/// <summary>
/// Tests for CredentialFileWatcher: initial load, hot-reload, deletion, and
/// file appearing after missing.  Each test uses a dedicated temp directory to
/// avoid cross-test interference.
/// </summary>
public sealed class CredentialFileWatcherTests : IDisposable
{
    private readonly string _dir;

    public CredentialFileWatcherTests()
    {
        _dir = Directory.CreateTempSubdirectory("fb_watcher_").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string CredPath(string name = "creds.txt") => Path.Combine(_dir, name);

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> ms for the <see cref="CredentialFileWatcher.Reloaded"/>
    /// event to fire, then returns the new credential snapshot.
    /// </summary>
    private static Task<IReadOnlyDictionary<string, UserCredential>> WaitForReload(
        CredentialFileWatcher watcher, int timeoutMs = 2000)
    {
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, UserCredential>>();

        void Handler(IReadOnlyDictionary<string, UserCredential> creds)
        {
            watcher.Reloaded -= Handler;
            tcs.TrySetResult(creds);
        }

        watcher.Reloaded += Handler;

        // Timeout safety
        _ = Task.Delay(timeoutMs).ContinueWith(_ =>
            tcs.TrySetException(new TimeoutException($"Reloaded event not raised within {timeoutMs} ms")));

        return tcs.Task;
    }

    // ── Initial load ─────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_FileExists_LoadsCredentials()
    {
        File.WriteAllText(CredPath(), "alice:secret\nbob:hunter2\n");

        using var watcher = new CredentialFileWatcher(CredPath());

        Assert.Equal(2, watcher.Current.Count);
        Assert.Equal("secret",  watcher.Current["alice"].Password);
        Assert.Equal("hunter2", watcher.Current["bob"].Password);
    }

    [Fact]
    public void Constructor_FileMissing_StartsEmpty()
    {
        using var watcher = new CredentialFileWatcher(CredPath());
        Assert.Empty(watcher.Current);
    }

    // ── Hot-reload on change ──────────────────────────────────────────────────

    [Fact]
    public async Task FileChanged_CurrentUpdates()
    {
        File.WriteAllText(CredPath(), "alice:pass1\n");
        using var watcher = new CredentialFileWatcher(CredPath());

        var reloadTask = WaitForReload(watcher);

        File.WriteAllText(CredPath(), "alice:pass2\nbob:newpass\n");

        var creds = await reloadTask;

        Assert.Equal(2,         creds.Count);
        Assert.Equal("pass2",   creds["alice"].Password);
        Assert.Equal("newpass", creds["bob"].Password);
    }

    [Fact]
    public async Task FileChanged_CurrentPropertyReflectsNewCreds()
    {
        File.WriteAllText(CredPath(), "alice:old\n");
        using var watcher = new CredentialFileWatcher(CredPath());

        var reloadTask = WaitForReload(watcher);
        File.WriteAllText(CredPath(), "alice:new\n");
        await reloadTask;

        Assert.Equal("new", watcher.Current["alice"].Password);
    }

    // ── File deletion ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FileDeleted_CurrentBecomesEmpty()
    {
        File.WriteAllText(CredPath(), "alice:secret\n");
        using var watcher = new CredentialFileWatcher(CredPath());
        Assert.NotEmpty(watcher.Current);

        var reloadTask = WaitForReload(watcher);
        File.Delete(CredPath());
        var creds = await reloadTask;

        Assert.Empty(creds);
        Assert.Empty(watcher.Current);
    }

    // ── File appears after missing ────────────────────────────────────────────

    [Fact]
    public async Task FileMissingAtStart_ThenCreated_LoadsCredentials()
    {
        using var watcher = new CredentialFileWatcher(CredPath());
        Assert.Empty(watcher.Current);

        var reloadTask = WaitForReload(watcher);
        File.WriteAllText(CredPath(), "carol:mypass\n");
        var creds = await reloadTask;

        Assert.Single(creds);
        Assert.Equal("mypass", creds["carol"].Password);
    }

    // ── Thread safety ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentReads_DoNotThrow()
    {
        File.WriteAllText(CredPath(), "alice:secret\n");
        using var watcher = new CredentialFileWatcher(CredPath());

        // Hammer Current from many threads while a reload is in flight
        var reloadTask = WaitForReload(watcher);
        var readers = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                _ = watcher.Current.Count;
        })).ToArray();

        File.WriteAllText(CredPath(), "alice:new\nbob:other\n");
        await reloadTask;
        await Task.WhenAll(readers); // must not throw
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        File.WriteAllText(CredPath(), "alice:secret\n");
        var watcher = new CredentialFileWatcher(CredPath());
        watcher.Dispose();
        watcher.Dispose(); // must not throw
    }
}
