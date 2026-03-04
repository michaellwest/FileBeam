namespace FileBeam;

/// <summary>
/// Background periodic task that deletes files from the upload directory whose
/// last-write time is older than the configured TTL. Empty directories are pruned
/// bottom-up after each sweep.
///
/// When <paramref name="perSender"/> is true, the admin user's named subfolder
/// (e.g. "admin") is skipped so admin-uploaded files are never auto-deleted.
/// When <paramref name="perSender"/> is false, all files expire equally.
/// </summary>
public sealed class UploadExpirer : IAsyncDisposable
{
    private readonly string  _uploadDir;
    private readonly TimeSpan _ttl;
    private readonly string? _adminSubfolder; // set only when perSender=true
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    /// <summary>
    /// The fully-qualified path of the admin user's subfolder inside <see cref="_uploadDir"/>.
    /// <c>null</c> when <paramref name="perSender"/> was <c>false</c>.
    /// </summary>
    public string? AdminSubfolder => _adminSubfolder;

    public UploadExpirer(string uploadDir, TimeSpan ttl, bool perSender, string adminUsername,
        Action<string>? log = null)
    {
        _uploadDir = uploadDir;
        _ttl       = ttl;
        _adminSubfolder = perSender
            ? Path.GetFullPath(Path.Combine(uploadDir, SanitizeName(adminUsername)))
            : null;
        _log   = log;
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            // Scan immediately on startup, then every 60 seconds
            while (true)
            {
                ScanAndDelete();
                await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    internal void ScanAndDelete()
    {
        var cutoff = DateTime.UtcNow - _ttl;

        // Delete expired files
        try
        {
            foreach (var path in Directory.EnumerateFiles(_uploadDir, "*", SearchOption.AllDirectories))
            {
                // Skip the admin's subfolder when perSender=true
                if (_adminSubfolder is not null &&
                    path.StartsWith(_adminSubfolder, StringComparison.OrdinalIgnoreCase))
                {
                    if (_log is not null)
                    {
                        try
                        {
                            if (File.GetLastWriteTimeUtc(path) < cutoff)
                            {
                                var rel = Path.GetRelativePath(_uploadDir, path).Replace('\\', '/');
                                _log($"[SKIP]  {rel} — never expires");
                            }
                        }
                        catch { /* ignore stat errors */ }
                    }
                    continue;
                }

                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(path);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(path);
                        if (_log is not null)
                        {
                            var rel = Path.GetRelativePath(_uploadDir, path).Replace('\\', '/');
                            var age = DateTime.UtcNow - lastWrite;
                            _log($"[EXPIR] {rel} — expired after {FormatAge(age)}");
                        }
                    }
                }
                catch (IOException) { /* file locked / already deleted — skip */ }
                catch (UnauthorizedAccessException) { /* no permission — skip */ }
            }
        }
        catch (DirectoryNotFoundException) { /* upload dir removed — nothing to do */ }

        // Prune empty directories bottom-up (deepest first via descending length)
        try
        {
            var dirs = Directory.GetDirectories(_uploadDir, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length);

            foreach (var dir in dirs)
            {
                // Never delete the upload root itself
                if (string.Equals(dir, _uploadDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (IOException) { /* dir in use — skip */ }
                catch (UnauthorizedAccessException) { /* no permission — skip */ }
            }
        }
        catch (DirectoryNotFoundException) { /* upload dir removed — nothing to do */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _worker; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)  return $"{(int)age.TotalSeconds}s";
        if (age.TotalHours   < 1)   return $"{(int)age.TotalMinutes}m";
        if (age.TotalDays    < 1)   return $"{(int)age.TotalHours}h {age.Minutes}m";
        return $"{(int)age.TotalDays}d {age.Hours}h";
    }

    private static string SanitizeName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
