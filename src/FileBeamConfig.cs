using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileBeam;

/// <summary>
/// Represents the contents of a filebeam.json config file.
/// All fields are optional; CLI flags override any values set here.
/// NOTE: passwords are intentionally not supported — pass --password via CLI
///       or use --credentials-file for per-user authentication.
/// </summary>
internal sealed class FileBeamConfig
{
    [JsonPropertyName("download")]        public string? Download        { get; init; }
    [JsonPropertyName("upload")]          public string? Upload          { get; init; }
    [JsonPropertyName("port")]            public int?    Port            { get; init; }
    [JsonPropertyName("credentialsFile")] public string? CredentialsFile { get; init; }
    [JsonPropertyName("invitesFile")]     public string? InvitesFile     { get; init; }
    [JsonPropertyName("readonly")]        public bool?   ReadOnly        { get; init; }
    [JsonPropertyName("perSender")]       public bool?   PerSender       { get; init; }
    [JsonPropertyName("maxFileSize")]     public string? MaxFileSize     { get; init; }
    [JsonPropertyName("maxUploadBytes")]  public string? MaxUploadBytes  { get; init; }
    [JsonPropertyName("maxUploadTotal")]  public string? MaxUploadTotal  { get; init; }
    [JsonPropertyName("maxUploadSize")]   public string? MaxUploadSize   { get; init; }
    [JsonPropertyName("tlsCert")]         public string? TlsCert         { get; init; }
    [JsonPropertyName("tlsKey")]          public string? TlsKey          { get; init; }
    [JsonPropertyName("shareTtl")]        public int?    ShareTtl        { get; init; }
    [JsonPropertyName("auditLog")]        public string? AuditLog        { get; init; }
    [JsonPropertyName("auditLogMaxSize")] public string? AuditLogMaxSize { get; init; }
    [JsonPropertyName("rateLimit")]       public int?    RateLimit       { get; init; }
    [JsonPropertyName("logLevel")]        public string? LogLevel        { get; init; }

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
    /// The --password flag is always omitted; all other flags that differ from their defaults
    /// (or are non-null) are included.
    /// </summary>
    public static string ToCliCommand(
        string  download,
        string  upload,
        int     port,
        string? credentialsFile,
        string? invitesFile,
        bool    readOnly,
        bool    perSender,
        long    maxFileSize,
        long    maxUploadBytes,
        long    maxUploadTotal,
        long?   maxUploadSize,
        string? tlsCert,
        string? tlsKey,
        int     shareTtl,
        string? auditLog,
        long    auditLogMaxSize,
        int     rateLimit,
        string  logLevel)
    {
        var sb = new StringBuilder("filebeam.exe");
        sb.Append($" --download \"{download}\"");
        if (!string.Equals(download, upload, StringComparison.OrdinalIgnoreCase))
            sb.Append($" --upload \"{upload}\"");
        if (port != 8080)
            sb.Append($" --port {port}");
        if (credentialsFile != null)
            sb.Append($" --credentials-file \"{credentialsFile}\"");
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
        // --password intentionally omitted
        return sb.ToString();
    }

    /// <summary>
    /// Serializes the effective resolved configuration to indented JSON for display.
    /// Fields that map to the given effective values are included; password is always omitted.
    /// </summary>
    public static string ToDisplayJson(
        string  download,
        string  upload,
        int     port,
        string? credentialsFile,
        string? invitesFile,
        bool    readOnly,
        bool    perSender,
        long    maxFileSize,
        long    maxUploadBytes,
        long    maxUploadTotal,
        long?   maxUploadSize,
        string? tlsCert,
        string? tlsKey,
        int     shareTtl,
        string? auditLog,
        long    auditLogMaxSize,
        int     rateLimit,
        string  logLevel)
    {
        var obj = new
        {
            // _passwordOmitted is a hint for readers; the JSON key name is intentional
            _note          = "password is intentionally omitted — pass via --password flag",
            download,
            upload,
            port,
            credentialsFile,
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
            shareTtl,
            auditLog,
            auditLogMaxSize = auditLogMaxSize > 0 ? FormatBytes(auditLogMaxSize) : "unlimited",
            rateLimit,
            logLevel,
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
