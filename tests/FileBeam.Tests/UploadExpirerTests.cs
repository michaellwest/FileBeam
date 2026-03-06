namespace FileBeam.Tests;

public sealed class UploadExpirerTests : IDisposable
{
    private readonly string _uploadDir;

    public UploadExpirerTests()
    {
        _uploadDir = Directory.CreateTempSubdirectory("fb_expirer_").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_uploadDir))
            Directory.Delete(_uploadDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a file and back-dates its LastWriteTime by the given age.</summary>
    private string CreateOldFile(string relativePath, TimeSpan age)
    {
        var path = Path.Combine(_uploadDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    /// <summary>Creates a fresh file with LastWriteTime = now.</summary>
    private string CreateFreshFile(string relativePath)
    {
        var path = Path.Combine(_uploadDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        return path;
    }

    private UploadExpirer MakeExpirer(TimeSpan ttl, bool perSender = false, string adminUsername = "admin",
        Action<string>? log = null)
        => new UploadExpirer(_uploadDir, ttl, perSender, adminUsername, log);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ScanAndDelete_DeletesExpiredFile()
    {
        var file    = CreateOldFile("old.txt", TimeSpan.FromHours(2));
        var expirer = MakeExpirer(TimeSpan.FromHours(1));

        expirer.ScanAndDelete();

        Assert.False(File.Exists(file), "Expired file should be deleted.");
    }

    [Fact]
    public void ScanAndDelete_PreservesNonExpiredFile()
    {
        var file    = CreateFreshFile("fresh.txt");
        var expirer = MakeExpirer(TimeSpan.FromHours(1));

        expirer.ScanAndDelete();

        Assert.True(File.Exists(file), "Fresh file should not be deleted.");
    }

    [Fact]
    public void ScanAndDelete_PrunesEmptyDirectoryAfterDeletion()
    {
        var file    = CreateOldFile("subdir/old.txt", TimeSpan.FromHours(2));
        var subdir  = Path.GetDirectoryName(file)!;
        var expirer = MakeExpirer(TimeSpan.FromHours(1));

        expirer.ScanAndDelete();

        Assert.False(File.Exists(file),        "Expired file should be deleted.");
        Assert.False(Directory.Exists(subdir), "Empty directory should be pruned.");
    }

    [Fact]
    public void ScanAndDelete_DoesNotPruneNonEmptyDirectory()
    {
        var oldFile   = CreateOldFile("subdir/old.txt",   TimeSpan.FromHours(2));
        var freshFile = CreateFreshFile("subdir/fresh.txt");
        var subdir    = Path.GetDirectoryName(oldFile)!;
        var expirer   = MakeExpirer(TimeSpan.FromHours(1));

        expirer.ScanAndDelete();

        Assert.False(File.Exists(oldFile),    "Expired file should be deleted.");
        Assert.True(File.Exists(freshFile),   "Fresh file should survive.");
        Assert.True(Directory.Exists(subdir), "Non-empty directory should not be pruned.");
    }

    [Fact]
    public void ScanAndDelete_SkipsAdminSubfolder_WhenPerSenderTrue()
    {
        // Admin's subfolder: uploadDir/admin/old.txt (would expire if not skipped)
        var adminFile = CreateOldFile("admin/old.txt", TimeSpan.FromHours(2));
        var expirer   = MakeExpirer(TimeSpan.FromHours(1), perSender: true, adminUsername: "admin");

        expirer.ScanAndDelete();

        Assert.True(File.Exists(adminFile), "Admin subfolder should be skipped when perSender=true.");
    }

    [Fact]
    public void ScanAndDelete_DoesNotSkipAdminSubfolder_WhenPerSenderFalse()
    {
        // No per-sender — all files expire equally
        var adminFile = CreateOldFile("admin/old.txt", TimeSpan.FromHours(2));
        var expirer   = MakeExpirer(TimeSpan.FromHours(1), perSender: false, adminUsername: "admin");

        expirer.ScanAndDelete();

        Assert.False(File.Exists(adminFile), "Admin file should expire when perSender=false.");
    }

    [Fact]
    public void ScanAndDelete_NeverDeletesUploadDirItself()
    {
        // Even with all files expired and deleted, the root uploadDir must survive
        CreateOldFile("old.txt", TimeSpan.FromHours(2));
        var expirer = MakeExpirer(TimeSpan.FromHours(1));

        expirer.ScanAndDelete();

        Assert.True(Directory.Exists(_uploadDir), "Upload root directory must never be deleted.");
    }

    [Fact]
    public void ScanAndDelete_SwallowsLockedFileException()
    {
        var path    = CreateOldFile("locked.txt", TimeSpan.FromHours(2));
        var expirer = MakeExpirer(TimeSpan.FromHours(1));

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Should not throw — locked file error is swallowed
            var ex = Record.Exception(() => expirer.ScanAndDelete());
            Assert.Null(ex);
        }
    }

    [Fact]
    public void ScanAndDelete_OnlyDeletesExpiredFile_WhenMixedFreshAndOld()
    {
        var oldFile   = CreateOldFile("old.txt",   TimeSpan.FromHours(2));
        var freshFile = CreateFreshFile("fresh.txt");
        var expirer   = MakeExpirer(TimeSpan.FromHours(1));

        expirer.ScanAndDelete();

        Assert.False(File.Exists(oldFile),        "Expired file should be deleted.");
        Assert.True(File.Exists(freshFile),       "Fresh file should survive.");
        Assert.True(Directory.Exists(_uploadDir), "Upload root should survive.");
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutHanging()
    {
        var expirer = MakeExpirer(TimeSpan.FromHours(1));
        var cts     = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Dispose should complete promptly
        await expirer.DisposeAsync().AsTask().WaitAsync(cts.Token);
    }

    // ── Logging tests ─────────────────────────────────────────────────────────

    [Fact]
    public void ScanAndDelete_LogsExpir_WhenFileDeleted()
    {
        var logs    = new List<string>();
        CreateOldFile("old.txt", TimeSpan.FromHours(2));
        var expirer = MakeExpirer(TimeSpan.FromHours(1), log: logs.Add);

        expirer.ScanAndDelete();

        Assert.Single(logs);
        Assert.StartsWith("[EXPIR]", logs[0]);
        Assert.Contains("old.txt", logs[0]);
    }

    [Fact]
    public void ScanAndDelete_NoLog_WhenAdminExemptFilePastTtl()
    {
        var logs = new List<string>();
        CreateOldFile("admin/old.txt", TimeSpan.FromHours(2));
        var expirer = MakeExpirer(TimeSpan.FromHours(1), perSender: true, adminUsername: "admin", log: logs.Add);

        expirer.ScanAndDelete();

        Assert.Empty(logs);
    }

    [Fact]
    public void ScanAndDelete_NoLog_WhenFreshFile()
    {
        var logs    = new List<string>();
        CreateFreshFile("fresh.txt");
        var expirer = MakeExpirer(TimeSpan.FromHours(1), log: logs.Add);

        expirer.ScanAndDelete();

        Assert.Empty(logs);
    }

    [Fact]
    public void ScanAndDelete_NoLog_WhenAdminExemptFileFresh()
    {
        // Admin-exempt file that is NOT past TTL — should NOT log [SKIP]
        var logs = new List<string>();
        CreateFreshFile("admin/fresh.txt");
        var expirer = MakeExpirer(TimeSpan.FromHours(1), perSender: true, adminUsername: "admin", log: logs.Add);

        expirer.ScanAndDelete();

        Assert.Empty(logs);
    }

    [Fact]
    public void AdminSubfolder_ReturnsCorrectPath_WhenPerSenderTrue()
    {
        var expirer  = MakeExpirer(TimeSpan.FromHours(1), perSender: true, adminUsername: "admin");
        var expected = Path.GetFullPath(Path.Combine(_uploadDir, "admin"));

        Assert.Equal(expected, expirer.AdminSubfolder);
    }

    [Fact]
    public void AdminSubfolder_ReturnsNull_WhenPerSenderFalse()
    {
        var expirer = MakeExpirer(TimeSpan.FromHours(1), perSender: false);

        Assert.Null(expirer.AdminSubfolder);
    }
}

// ── HtmlRenderer expiry column tests ─────────────────────────────────────────

public class HtmlRendererExpiryTests : IDisposable
{
    private readonly string _tmpDir;

    public HtmlRendererExpiryTests()
    {
        _tmpDir = Directory.CreateTempSubdirectory("fb_expiry_html_").FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
    }

    private List<FileInfo> MakeFiles(params (string name, DateTime lastWriteUtc)[] files)
    {
        var infos = new List<FileInfo>();
        foreach (var (name, lastWriteUtc) in files)
        {
            var path = Path.Combine(_tmpDir, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            infos.Add(new FileInfo(path));
        }
        return infos;
    }

    [Fact]
    public void RenderDirectory_WithUploadTtl_ShowsExpiresColumn()
    {
        var files = MakeFiles(("test.txt", DateTime.UtcNow - TimeSpan.FromMinutes(30)));

        var html = HtmlRenderer.RenderDirectory("", [], files, uploadTtl: TimeSpan.FromHours(1));

        Assert.Contains("Expires", html);
        Assert.Contains("data-expires", html);
        Assert.Contains("expires in", html);
    }

    [Fact]
    public void RenderDirectory_WithoutUploadTtl_NoExpiresColumn()
    {
        var files = MakeFiles(("test.txt", DateTime.UtcNow));

        var html = HtmlRenderer.RenderDirectory("", [], files);

        Assert.DoesNotContain("Expires", html);
        Assert.DoesNotContain("data-expires", html);
    }

    [Fact]
    public void RenderDirectory_WithUploadTtl_ShowsExpiredText_ForOldFiles()
    {
        // File is 2h old with 1h TTL → should show "expired"
        var files = MakeFiles(("old.txt", DateTime.UtcNow - TimeSpan.FromHours(2)));

        var html = HtmlRenderer.RenderDirectory("", [], files, uploadTtl: TimeSpan.FromHours(1));

        Assert.Contains("expired", html);
    }

    [Fact]
    public void RenderDirectory_WithUploadTtl_InjectsCountdownScript()
    {
        var files = MakeFiles(("test.txt", DateTime.UtcNow));

        var html = HtmlRenderer.RenderDirectory("", [], files, uploadTtl: TimeSpan.FromHours(1));

        Assert.Contains("initFileExpiryCountdowns", html);
        Assert.Contains("_fmtExpiry", html);
    }

    [Fact]
    public void FormatExpiryText_FutureTime_ReturnsExpiresIn()
    {
        var text = HtmlRenderer.FormatExpiryText(3661); // 1h 1m 1s

        Assert.StartsWith("expires in", text);
        Assert.Contains("1h", text);
    }

    [Fact]
    public void FormatExpiryText_PastTime_ReturnsExpiredAgo()
    {
        var text = HtmlRenderer.FormatExpiryText(-120); // 2 minutes ago

        Assert.StartsWith("expired", text);
        Assert.Contains("ago", text);
    }

    [Fact]
    public void RenderDirectory_ShowsNeverExpires_ForAdminExemptFile()
    {
        // File is 2h old with 1h TTL — would normally show "expired X ago"
        // but it's inside the admin-exempt path, so should show "never expires"
        var files = MakeFiles(("test.txt", DateTime.UtcNow - TimeSpan.FromHours(2)));
        var adminExemptPath = _tmpDir; // all files are "admin-exempt" in this test

        var html = HtmlRenderer.RenderDirectory("", [], files,
            uploadTtl: TimeSpan.FromHours(1),
            adminExemptPath: adminExemptPath);

        Assert.Contains("never expires", html);
        // The file row should not have a countdown td (no data-expires attribute on its td)
        Assert.DoesNotContain("class=\"expires\" style=\"color:#e53e3e", html);
    }
}
