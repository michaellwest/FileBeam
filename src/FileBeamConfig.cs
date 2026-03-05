using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileBeam;

/// <summary>
/// Represents the contents of a filebeam.json config file.
/// All fields are optional; CLI flags override any values set here.
/// NOTE: adminPassword is intentionally excluded — pass via --admin-password CLI flag
///       or FILEBEAM_ADMIN_PASSWORD environment variable.
/// </summary>
internal sealed class FileBeamConfig
{
    [JsonPropertyName("download")]        public string? Download        { get; init; }
    [JsonPropertyName("upload")]          public string? Upload          { get; init; }
    [JsonPropertyName("port")]            public int?    Port            { get; init; }
    [JsonPropertyName("adminUsername")]   public string? AdminUsername   { get; init; }
    [JsonPropertyName("invitesFile")]     public string? InvitesFile     { get; init; }
    [JsonPropertyName("readonly")]        public bool?   ReadOnly        { get; init; }
    [JsonPropertyName("perSender")]       public bool?   PerSender       { get; init; }
    [JsonPropertyName("maxFileSize")]     public string? MaxFileSize     { get; init; }
    [JsonPropertyName("maxUploadBytes")]  public string? MaxUploadBytes  { get; init; }
    [JsonPropertyName("maxUploadTotal")]  public string? MaxUploadTotal  { get; init; }
    [JsonPropertyName("maxUploadSize")]   public string? MaxUploadSize   { get; init; }
    [JsonPropertyName("tlsCert")]         public string? TlsCert         { get; init; }
    [JsonPropertyName("tlsKey")]          public string? TlsKey          { get; init; }
    [JsonPropertyName("tlsPfx")]          public string? TlsPfx          { get; init; }
    [JsonPropertyName("tlsPfxPassword")]  public string? TlsPfxPassword  { get; init; }
    [JsonPropertyName("shareTtl")]        public int?    ShareTtl        { get; init; }
    [JsonPropertyName("auditLog")]        public string? AuditLog        { get; init; }
    [JsonPropertyName("auditLogMaxSize")] public string? AuditLogMaxSize { get; init; }
    [JsonPropertyName("rateLimit")]       public int?    RateLimit       { get; init; }
    [JsonPropertyName("logLevel")]        public string? LogLevel        { get; init; }
    [JsonPropertyName("uploadTtl")]        public string? UploadTtl        { get; init; }
    [JsonPropertyName("maxConcurrentZips")] public int?   MaxConcurrentZips { get; init; }
    [JsonPropertyName("maxZipSize")]        public string? MaxZipSize       { get; init; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Attempts to load a FileBeamConfig from the given path.
    /// Returns false and sets <paramref name="error"/> if the file cannot be read or parsed.
    /// </summary>
    public static bool TryLoad(string path, out FileBeamConfig? config, out string? error)
    {
        config = null;
        error  = null;
        try
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<FileBeamConfig>(json, JsonOpts);
            if (config is null) { error = "File is empty or null."; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds an equivalent CLI invocation string for the given resolved configuration.
    /// The --admin-password flag is always omitted; all other flags that differ from their
    /// defaults (or are non-null) are included.
    /// </summary>
    public static string ToCliCommand(
        string  download,
        string  upload,
        int     port,
        string? adminUsername,
        string? invitesFile,
        bool    readOnly,
        bool    perSender,
        long    maxFileSize,
        long    maxUploadBytes,
        long    maxUploadTotal,
        long?   maxUploadSize,
        string? tlsCert,
        string? tlsKey,
        string? tlsPfx,
        string? tlsPfxPassword,
        int     shareTtl,
        string? auditLog,
        long    auditLogMaxSize,
        int     rateLimit,
        string  logLevel,
        string? uploadTtl = null,
        int     maxConcurrentZips = 2,
        long    maxZipBytes = 0)
    {
        var sb = new StringBuilder("filebeam.exe");
        sb.Append($" --download \"{download}\"");
        if (!string.Equals(download, upload, StringComparison.OrdinalIgnoreCase))
            sb.Append($" --upload \"{upload}\"");
        if (port != 8080)
            sb.Append($" --port {port}");
        if (adminUsername != null && adminUsername != "admin")
            sb.Append($" --admin-username \"{adminUsername}\"");
        // --admin-password intentionally omitted
        if (invitesFile != null)
            sb.Append($" --invites-file \"{invitesFile}\"");
        if (readOnly)
            sb.Append(" --readonly");
        if (perSender)
            sb.Append(" --per-sender");
        if (maxFileSize > 0)
            sb.Append($" --max-file-size {FormatBytes(maxFileSize)}");
        if (maxUploadBytes > 0)
            sb.Append($" --max-upload-bytes {FormatBytes(maxUploadBytes)}");
        if (maxUploadTotal > 0)
            sb.Append($" --max-upload-total {FormatBytes(maxUploadTotal)}");
        if (maxUploadSize.HasValue && maxUploadSize > 0)
            sb.Append($" --max-upload-size {FormatBytes(maxUploadSize.Value)}");
        if (tlsCert != null)
            sb.Append($" --tls-cert \"{tlsCert}\"");
        if (tlsKey != null)
            sb.Append($" --tls-key \"{tlsKey}\"");
        if (tlsPfx != null)
            sb.Append($" --tls-pfx \"{tlsPfx}\"");
        // --tls-pfx-password intentionally omitted — pass via CLI flag directly
        if (shareTtl != 3600)
            sb.Append($" --share-ttl {shareTtl}");
        if (auditLog != null)
            sb.Append($" --audit-log \"{auditLog}\"");
        if (auditLogMaxSize > 0)
            sb.Append($" --audit-log-max-size {FormatBytes(auditLogMaxSize)}");
        if (rateLimit != 60)
            sb.Append($" --rate-limit {rateLimit}");
        if (logLevel != "info")
            sb.Append($" --log-level {logLevel}");
        if (uploadTtl != null)
            sb.Append($" --upload-ttl \"{uploadTtl}\"");
        if (maxConcurrentZips != 2)
            sb.Append($" --max-concurrent-zips {maxConcurrentZips}");
        if (maxZipBytes > 0)
            sb.Append($" --max-zip-size {FormatBytes(maxZipBytes)}");
        return sb.ToString();
    }

    /// <summary>
    /// Serializes the effective resolved configuration to indented JSON for display.
    /// Fields that map to the given effective values are included; adminPassword is always omitted.
    /// </summary>
    public static string ToDisplayJson(
        string  download,
        string  upload,
        int     port,
        string? adminUsername,
        string? invitesFile,
        bool    readOnly,
        bool    perSender,
        long    maxFileSize,
        long    maxUploadBytes,
        long    maxUploadTotal,
        long?   maxUploadSize,
        string? tlsCert,
        string? tlsKey,
        string? tlsPfx,
        string? tlsPfxPassword,
        int     shareTtl,
        string? auditLog,
        long    auditLogMaxSize,
        int     rateLimit,
        string  logLevel,
        string? uploadTtl = null,
        int     maxConcurrentZips = 2,
        long    maxZipBytes = 0)
    {
        var obj = new
        {
            // adminPassword is intentionally excluded
            _note          = "adminPassword is intentionally omitted — use --admin-password flag or FILEBEAM_ADMIN_PASSWORD env var",
            download,
            upload,
            port,
            adminUsername,
            invitesFile,
            @readonly      = readOnly,
            perSender,
            maxFileSize    = maxFileSize    > 0 ? FormatBytes(maxFileSize)    : "unlimited",
            maxUploadBytes = maxUploadBytes > 0 ? FormatBytes(maxUploadBytes) : "unlimited",
            maxUploadTotal = maxUploadTotal > 0 ? FormatBytes(maxUploadTotal) : "unlimited",
            maxUploadSize  = maxUploadSize.HasValue && maxUploadSize > 0
                                ? FormatBytes(maxUploadSize.Value) : "unlimited",
            tlsCert,
            tlsKey,
            tlsPfx,
            tlsPfxPasswordSet = tlsPfxPassword is not null,   // value omitted — use --tls-pfx-password flag
            shareTtl,
            auditLog,
            auditLogMaxSize = auditLogMaxSize > 0 ? FormatBytes(auditLogMaxSize) : "unlimited",
            rateLimit,
            logLevel,
            uploadTtl,
            maxConcurrentZips,
            maxZipSize = maxZipBytes > 0 ? FormatBytes(maxZipBytes) : "unlimited",
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024 * 1024) return $"{bytes / (1024L * 1024 * 1024 * 1024)}TB";
        if (bytes >= 1024L * 1024 * 1024)        return $"{bytes / (1024L * 1024 * 1024)}GB";
        if (bytes >= 1024L * 1024)                return $"{bytes / (1024L * 1024)}MB";
        if (bytes >= 1024L)                       return $"{bytes / 1024L}KB";
        return $"{bytes}B";
    }
}
