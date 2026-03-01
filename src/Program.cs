using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using FileBeam;
using QRCoder;
using Spectre.Console;

// ── Parse CLI args ─────────────────────────────────────────────────────────────
string? cliDownloadDir      = null;
string? cliUploadDir        = null;
string? cliPassword         = null;
string? cliCredentialsFile  = null;
int?    cliPort             = null;
bool    readOnly            = false;
bool    perSender           = false;
long    maxFileSize         = 0;   // bytes; 0 = unlimited
long    maxUploadBytes      = 0;   // per-sender cumulative bytes; 0 = unlimited
long    maxUploadTotal      = 0;   // total upload directory cap; 0 = unlimited
long?   maxUploadSize       = null; // null = keep large default; 0 = unlimited
string? cliTlsCert          = null;
string? cliTlsKey           = null;
string? auditLogPath        = null;
long    auditLogMaxSize     = 0;
int     shareTtl            = 3600; // default share link TTL in seconds
int     rateLimit           = 60;  // requests per minute per IP
string  logLevel            = "info"; // info | debug

for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--download"         || args[i] == "-s")    && i + 1 < args.Length)
        cliDownloadDir = args[++i];
    else if ((args[i] == "--upload"      || args[i] == "-d")    && i + 1 < args.Length)
        cliUploadDir = args[++i];
    else if ((args[i] == "--port"        || args[i] == "-p")    && i + 1 < args.Length)
        cliPort = int.TryParse(args[++i], out var p) ? p : null;
    else if ((args[i] == "--password"    || args[i] == "--pw")  && i + 1 < args.Length)
        cliPassword = args[++i];
    else if (args[i] == "--credentials-file"                    && i + 1 < args.Length)
        cliCredentialsFile = args[++i];
    else if (args[i] == "--readonly"     || args[i] == "-r")
        readOnly = true;
    else if (args[i] == "--per-sender")
        perSender = true;
    else if (args[i] == "--max-file-size"  && i + 1 < args.Length)
        maxFileSize = long.TryParse(args[++i], out var mf) ? mf : 0;
    else if (args[i] == "--max-upload-bytes" && i + 1 < args.Length)
    {
        var raw = args[++i];
        if (!TryParseSize(raw, out var parsed))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --max-upload-bytes invalid value '{Markup.Escape(raw)}'. " +
                "Use e.g. 500MB, 2GB, 100KB, or 'unlimited'.");
            return 1;
        }
        maxUploadBytes = parsed;
    }
    else if (args[i] == "--max-upload-total" && i + 1 < args.Length)
    {
        var raw = args[++i];
        if (!TryParseSize(raw, out var parsed))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --max-upload-total invalid value '{Markup.Escape(raw)}'. " +
                "Use e.g. 10GB, 500MB, or 'unlimited'.");
            return 1;
        }
        maxUploadTotal = parsed;
    }
    else if (args[i] == "--max-upload-size" && i + 1 < args.Length)
    {
        var raw = args[++i];
        if (!TryParseSize(raw, out var parsed))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --max-upload-size invalid value '{Markup.Escape(raw)}'. " +
                "Use e.g. 500MB, 2GB, 100KB, or 'unlimited'.");
            return 1;
        }
        maxUploadSize = parsed;
    }
    else if (args[i] == "--tls-cert" && i + 1 < args.Length)
        cliTlsCert = args[++i];
    else if (args[i] == "--tls-key" && i + 1 < args.Length)
        cliTlsKey = args[++i];
    else if (args[i] == "--share-ttl" && i + 1 < args.Length)
        shareTtl = int.TryParse(args[++i], out var st) && st > 0 ? st : 3600;
    else if (args[i] == "--audit-log" && i + 1 < args.Length)
        auditLogPath = args[++i];
    else if (args[i] == "--audit-log-max-size" && i + 1 < args.Length)
        auditLogMaxSize = long.TryParse(args[++i], out var als) ? als : 0;
    else if (args[i] == "--rate-limit"   && i + 1 < args.Length)
        rateLimit = int.TryParse(args[++i], out var rl) ? rl : 60;
    else if (args[i] == "--log-level"   && i + 1 < args.Length)
        logLevel = args[++i].ToLowerInvariant();
}

/// <summary>
/// Parses a human-readable size string (e.g. "500MB", "2GB", "100KB", "unlimited", "0").
/// Returns true on success; bytes is 0 for "unlimited".
/// </summary>
static bool TryParseSize(string raw, out long bytes)
{
    bytes = 0;
    if (string.IsNullOrWhiteSpace(raw)) return false;

    var s = raw.Trim();
    if (s.Equals("unlimited", StringComparison.OrdinalIgnoreCase) || s == "0")
        return true;

    // Split numeric part from unit suffix
    int split = s.Length;
    while (split > 0 && !char.IsDigit(s[split - 1])) split--;
    if (split == 0) return false;

    if (!long.TryParse(s[..split], out var num) || num < 0) return false;
    var unit = s[split..].Trim().ToUpperInvariant();

    bytes = unit switch
    {
        "" or "B"  => num,
        "KB"       => num * 1_024L,
        "MB"       => num * 1_024L * 1_024,
        "GB"       => num * 1_024L * 1_024 * 1_024,
        "TB"       => num * 1_024L * 1_024 * 1_024 * 1_024,
        _          => -1
    };
    return bytes >= 0;
}

// ── Banner ─────────────────────────────────────────────────────────────────────
AnsiConsole.Write(new FigletText("FileBeam").Color(Color.DodgerBlue1));
AnsiConsole.MarkupLine("[grey]Instant LAN file server[/]\n");

// ── Interactive prompts (skip when no TTY, e.g. inside a container) ──────────
bool interactive = AnsiConsole.Profile.Capabilities.Interactive && !Console.IsInputRedirected;
var  cwd         = Directory.GetCurrentDirectory();

string serveDir = cliDownloadDir ?? (interactive
    ? AnsiConsole.Ask<string>("[bold]Download directory[/] [grey][[press Enter for current]][/]:", cwd)
    : cwd);

serveDir = Path.GetFullPath(serveDir);

if (!Directory.Exists(serveDir))
{
    AnsiConsole.MarkupLine($"[red]Directory not found:[/] {serveDir}");
    return 1;
}

// Upload directory: where uploads land (defaults to serve dir if not specified).
// When a separate upload dir is given, uploaded files are private — browsers only see the serve dir.
string uploadDir = cliUploadDir ?? (interactive
    ? AnsiConsole.Ask<string>("[bold]Upload directory[/] [grey][[press Enter for same as download]][/]:", serveDir)
    : serveDir);

uploadDir = Path.GetFullPath(uploadDir);

if (!Directory.Exists(uploadDir))
    Directory.CreateDirectory(uploadDir);

int port = cliPort ?? (interactive ? AnsiConsole.Ask("[bold]Port[/]:", 8080) : 8080);

string? password = cliPassword;

// ── Validate and load per-user credentials file (optional) ────────────────────
CredentialFileWatcher? credWatcher = null;
if (!string.IsNullOrEmpty(cliCredentialsFile))
{
    var absCredPath = Path.GetFullPath(cliCredentialsFile);

    // Hard-stop on unrecoverable config problems
    if (Directory.Exists(absCredPath))
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] --credentials-file path is a directory: {absCredPath}");
        return 1;
    }

    var credDir = Path.GetDirectoryName(absCredPath)!;
    if (!Directory.Exists(credDir))
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] --credentials-file parent directory does not exist: {credDir}");
        return 1;
    }

    if (File.Exists(absCredPath))
    {
        // Verify the file is actually readable before starting the server
        try { _ = File.ReadAllBytes(absCredPath); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] cannot read credentials file: {Markup.Escape(ex.Message)}");
            return 1;
        }

        // Report any parse warnings at startup
        var (_, warnings) = CredentialStore.LoadFileWithDiagnostics(absCredPath);
        foreach (var w in warnings)
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] credentials file line {w.LineNumber}: {Markup.Escape(w.Reason)} — \"{Markup.Escape(w.LineText)}\"");
    }
    else
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] credentials file not found: {absCredPath} — " +
            "server will reject all per-user logins until the file appears");
    }

    credWatcher = new CredentialFileWatcher(absCredPath);

    // Log every hot-reload to the console
    credWatcher.Reloaded += creds =>
    {
        var t = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (creds.Count == 0)
            AnsiConsole.MarkupLine($"[grey]{t}[/]  [yellow][[WARN]][/]  credentials file unloaded — all per-user logins rejected");
        else
            AnsiConsole.MarkupLine($"[grey]{t}[/]  [blue][[INFO]][/]  credentials reloaded — {creds.Count} user{(creds.Count == 1 ? "" : "s")}");
    };
}

// ── Validate TLS cert/key (optional) ──────────────────────────────────────────
X509Certificate2? tlsCertificate = null;
if (cliTlsCert != null || cliTlsKey != null)
{
    if (cliTlsCert is null || cliTlsKey is null)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] --tls-cert and --tls-key must both be provided together.");
        return 1;
    }

    var absCert = Path.GetFullPath(cliTlsCert);
    var absKey  = Path.GetFullPath(cliTlsKey);

    foreach (var (label, path) in new[] { ("--tls-cert", absCert), ("--tls-key", absKey) })
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {label} file not found: {Markup.Escape(path)}");
            return 1;
        }
        try { _ = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] cannot read {label}: {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    try { tlsCertificate = X509Certificate2.CreateFromPemFile(absCert, absKey); }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] failed to load TLS certificate: {Markup.Escape(ex.Message)}");
        return 1;
    }
}

// ── Resolve LAN IPs ────────────────────────────────────────────────────────────
var ips = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up
             && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
    .Select(a => a.Address.ToString())
    .ToList();

// ── Info panel ─────────────────────────────────────────────────────────────────
bool separateDrop = !string.Equals(
    Path.GetFullPath(serveDir), Path.GetFullPath(uploadDir), StringComparison.OrdinalIgnoreCase);

var panel = new Panel(
    Align.Left(new Markup(
        $"[bold]Download:[/] {serveDir}\n" +
        (separateDrop ? $"[bold]Upload:[/]   {uploadDir}\n" : "") +
        (perSender    ? "[bold]Upload:[/]   Per-sender folders\n" : "") +
        (maxUploadBytes > 0 ? $"[bold]Quota:[/]    {FormatBytes(maxUploadBytes)} per sender\n" : "") +
        (maxUploadTotal > 0 ? $"[bold]Cap:[/]      {FormatBytes(maxUploadTotal)} total upload directory\n" : "") +
        (readOnly     ? "[bold yellow]Mode:[/]     Read-only (uploads disabled)\n" : "") +
        (logLevel != "info" ? $"[bold]Log:[/]      {logLevel}\n" : "") +
        (tlsCertificate != null ? "[bold green]TLS:[/]      HTTPS enabled\n" : "") +
        (credWatcher is not null
            ? credWatcher.Current.Count > 0
                ? $"[bold]Auth:[/]     {credWatcher.Current.Count} per-user credential{(credWatcher.Current.Count == 1 ? "" : "s")} loaded" +
                  (tlsCertificate is null ? " [yellow](HTTP — credentials unencrypted)[/]" : "") + "\n"
                : "[bold yellow]Auth:[/]     Per-user credentials enforced (0 valid entries — all logins rejected)" +
                  (tlsCertificate is null ? " [yellow](HTTP — credentials unencrypted)[/]" : "") + "\n"
            : "") +
        (!string.IsNullOrEmpty(password)
            ? "[bold]Auth:[/]     Shared password required" +
              (tlsCertificate is null ? " [yellow](HTTP — credentials unencrypted)[/]" : "") + "\n"
            : "") +
        string.Join("\n", ips.Select(ip => $"[bold]URL:[/]      [link]{(tlsCertificate != null ? "https" : "http")}://{ip}:{port}[/]")) +
        (ips.Count == 0 ? $"\n[bold]URL:[/]      {(tlsCertificate != null ? "https" : "http")}://localhost:{port}" : ""))))
{
    Header = new PanelHeader(" FileBeam is running ", Justify.Center),
    Border = BoxBorder.Rounded,
    BorderStyle = new Style(Color.DodgerBlue1),
    Padding = new Padding(1, 0)
};
AnsiConsole.Write(panel);

// ── QR code for quick mobile access ──────────────────────────────────────────
var scheme = tlsCertificate != null ? "https" : "http";
var qrUrl  = ips.Count > 0 ? $"{scheme}://{ips[0]}:{port}" : $"{scheme}://localhost:{port}";
using var qrGen  = new QRCodeGenerator();
var qrData       = qrGen.CreateQrCode(qrUrl, QRCodeGenerator.ECCLevel.L);
var asciiQr      = new AsciiQRCode(qrData);
AnsiConsole.WriteLine(asciiQr.GetGraphic(1, "█", " "));

AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]\n");

// ── Generate per-session CSRF token ───────────────────────────────────────────
var csrfToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

// ── Build & start Kestrel ──────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders(); // suppress default ASP.NET logging; we do our own
// Effective body-size limit for Kestrel and form parsing:
// --max-upload-size takes precedence; otherwise fall back to --max-file-size headroom;
// 0/null on --max-upload-size means unlimited (null in Kestrel).
long? effectiveBodyLimit = maxUploadSize switch
{
    0    => null,                                   // unlimited
    long n when n > 0 => n,                         // explicit limit
    _    => maxFileSize > 0                          // fallback: per-file size + 1 MB headroom
               ? maxFileSize + (1024 * 1024)
               : 100L * 1024 * 1024 * 1024          // 100 GB safety cap
};

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxRequestBodySize = effectiveBodyLimit;
    opts.Listen(IPAddress.Any, port, listenOpts =>
    {
        if (tlsCertificate != null)
            listenOpts.UseHttps(tlsCertificate);
    });
});

// Allow large multipart uploads (default is 30 MB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = effectiveBodyLimit ?? long.MaxValue;
});

// ── Rate limiting: fixed-window per remote IP ─────────────────────────────────
builder.Services.AddRateLimiter(opts =>
{
    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimit,
            Window      = TimeSpan.FromMinutes(1),
            QueueLimit  = 0
        });
    });
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();

// Wire up FileWatcher and route handlers
using var fileWatcher = new FileWatcher(serveDir, maxSseConnections: 50);
var revocationStore = new RevocationStore();

// ── Audit logger (optional) ───────────────────────────────────────────────────
// --audit-log <path>  → log to file
// --audit-log -       → log to stdout (mixed with console output)
// (absent)            → no audit logging
AuditLogger? auditLogger = auditLogPath is not null
    ? new AuditLogger(auditLogPath == "-" ? null : auditLogPath, auditLogMaxSize)
    : null;

Action<string, string>? debugLog = logLevel == "debug"
    ? (reqId, msg) =>
      {
          var t = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
          AnsiConsole.MarkupLine($"[grey]{t}[/]  [dim][[DBG]][/]   [grey][[{reqId}]][/]  [grey]---[/]  [grey]---[/]  {Markup.Escape(msg)}");
      }
    : null;

var handlers = new RouteHandlers(
    serveDir, uploadDir, fileWatcher,
    isReadOnly:             readOnly,
    perSender:              perSender,
    maxFileSize:            maxFileSize,
    maxUploadBytesPerSender:maxUploadBytes,
    maxUploadBytesTotal:    maxUploadTotal,
    csrfToken:              csrfToken,
    shareTtlSeconds:        shareTtl,
    revocationStore:        revocationStore,
    debugLog:               debugLog);

// ── Console request log (with elapsed time) ──────────────────────────────────
// Must be registered before route mappings so it wraps endpoint execution.
// Registering it after MapGet/MapPost in WebApplication places it after the
// endpoint middleware, causing it to call next() on an already-started response
// which throws in Production (works in Development only because
// UseDeveloperExceptionPage masks the error).
app.Use(async (ctx, next) =>
{
    var sw        = Stopwatch.StartNew();
    var requestId = Guid.NewGuid().ToString("N")[..8];
    ctx.Items["fb.request.id"] = requestId;

    // Emit an "upload started" line before the body streams in so large uploads
    // are visible in the console while they're in flight.
    if (ctx.Request.Method == "POST" && ctx.Request.Path.StartsWithSegments("/upload"))
    {
        var startIp   = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        var startPath = ctx.Request.Path.Value ?? "/";
        var startTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        AnsiConsole.MarkupLine(
            $"[grey]{startTime}[/]  [blue][[INFO]][/]  [grey][[{requestId}]][/]  [grey]---[/]  [bold]POST[/]  {Markup.Escape(startPath)}  [grey]{startIp}  uploading…[/]");
    }

    Exception? unhandled = null;
    try { await next(); }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex) { unhandled = ex; throw; }
    finally
    {
        sw.Stop();

        // Suppress noisy SSE keepalive log entries
        if (ctx.Request.Path != "/events")
        {
            var ip     = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var method = ctx.Request.Method;
            var path   = ctx.Request.Path.Value ?? "/";
            var status = unhandled is not null ? 500 : ctx.Response.StatusCode;
            var color  = status >= 400 ? "red" : status >= 300 ? "yellow" : "green";
            var time   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            // Build optional transfer info suffix (file name + size for uploads/downloads)
            string transfer = "";
            if (unhandled is null && ctx.Items.TryGetValue("fb.bytes", out var bytesObj) && bytesObj is long bytes)
            {
                var size = FormatBytes(bytes);
                if (ctx.Items.TryGetValue("fb.count", out var countObj) && countObj is int count && count > 1)
                    transfer = $"  [grey]{count} files  {size}[/]";
                else if (ctx.Items.TryGetValue("fb.file", out var fileObj) && fileObj is string fileName)
                    transfer = $"  [grey]{Markup.Escape(fileName)}  {size}[/]";
                else
                    transfer = $"  [grey]{size}[/]";
            }

            var exInfo = unhandled is not null
                ? $"  [red]{Markup.Escape(unhandled.GetType().Name)}: {Markup.Escape(unhandled.Message)}[/]"
                : "";

            AnsiConsole.MarkupLine(
                $"[grey]{time}[/]  [grey][[INFO]][/]  [grey][[{requestId}]][/]  [{color}]{status}[/]  [bold]{method}[/]  {Markup.Escape(path)}{transfer}{exInfo}  [grey]{ip}  {sw.ElapsedMilliseconds}ms[/]");

            // ── Audit log entry for file transfer actions ──────────────────────
            if (auditLogger is not null && unhandled is null)
            {
                var auditAction = (method, path) switch
                {
                    ("GET",  var p) when p.StartsWith("/download/",     StringComparison.OrdinalIgnoreCase) => "download",
                    ("POST", var p) when p.StartsWith("/upload/",       StringComparison.OrdinalIgnoreCase) => "upload",
                    ("POST", var p) when p.StartsWith("/delete/",       StringComparison.OrdinalIgnoreCase) => "delete",
                    ("POST", var p) when p.StartsWith("/rename/",       StringComparison.OrdinalIgnoreCase) => "rename",
                    _ => null
                };
                if (auditAction is not null)
                {
                    // Extract Basic Auth username (if any); never log the password
                    string? auditUser = null;
                    var authHdr = ctx.Request.Headers.Authorization.ToString();
                    if (authHdr.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authHdr[6..]));
                            var colon   = decoded.IndexOf(':');
                            if (colon > 0) auditUser = decoded[..colon];
                        }
                        catch { /* malformed header */ }
                    }

                    var auditBytes = ctx.Items.TryGetValue("fb.bytes", out var ab) && ab is long lb ? lb : 0L;
                    auditLogger.Log(time, auditUser, ip, auditAction, path, auditBytes, status);
                }
            }
        }
    }
});

static string FormatBytes(long bytes) => bytes switch
{
    < 1_024                => $"{bytes} B",
    < 1_024 * 1_024        => $"{bytes / 1_024.0:F1} KB",
    < 1_024L * 1_024 * 1_024 => $"{bytes / (1_024.0 * 1_024):F1} MB",
    _                      => $"{bytes / (1_024.0 * 1_024 * 1_024):F1} GB"
};

// ── Rate limiter middleware ────────────────────────────────────────────────────
app.UseRateLimiter();

// ── Basic Auth + brute-force lockout (optional) ───────────────────────────────
// authRequired is true whenever --credentials-file was specified (even if file is
// currently missing) so that the server never silently starts unprotected.
bool authRequired = !string.IsNullOrEmpty(password) || credWatcher is not null;
if (authRequired)
{
    // Per-IP failed-attempt tracking.  Entries are trimmed when the dict grows large
    // to prevent unbounded memory growth from spoofed source IPs.
    var authState = new Dictionary<string, (int Failures, DateTimeOffset LockedUntil)>();
    var authLock  = new object();
    const int    MaxFailures     = 10;
    const int    LockoutSeconds  = 60;
    const int    MaxTrackedIPs   = 10_000;

    app.Use(async (ctx, next) =>
    {
        // Share link redemption (GET /s/{token}) is always unauthenticated
        if (ctx.Request.Method == "GET" && ctx.Request.Path.StartsWithSegments("/s"))
        {
            await next();
            return;
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // IP ban check — hard block before lockout / credential checks
        if (revocationStore.IsIpRevoked(ip))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Check lockout before touching the header
        bool locked;
        lock (authLock)
        {
            locked = authState.TryGetValue(ip, out var state)
                  && state.LockedUntil > DateTimeOffset.UtcNow;
        }

        if (locked)
        {
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"FileBeam\"";
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        bool authenticated = false;
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded  = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var colon    = decoded.IndexOf(':');
                if (colon >= 0)
                {
                    var submittedUser = decoded[..colon];
                    var submittedPass = decoded[(colon + 1)..];

                    // 1. Check per-user credentials (username AND password must match).
                    //    Read Current snapshot once — it may be hot-swapped by the file watcher.
                    if (credWatcher?.Current.TryGetValue(submittedUser, out var userCred) == true)
                    {
                        authenticated = CredentialStore.VerifyPassword(submittedPass, userCred.Password);
                        if (authenticated)
                        {
                            // Username ban check — immediate revocation even with valid credentials
                            if (revocationStore.IsUserRevoked(submittedUser))
                                authenticated = false;
                            else
                            {
                                ctx.Items["fb.role"] = userCred.Role;
                                ctx.Items["fb.user"] = submittedUser;
                            }
                        }
                    }

                    // 2. Fall back to shared password (any username accepted) — role is rw
                    if (!authenticated && !string.IsNullOrEmpty(password))
                    {
                        authenticated = CredentialStore.VerifyPassword(submittedPass, password);
                        if (authenticated)
                        {
                            ctx.Items["fb.role"] = "rw";
                            ctx.Items["fb.user"] = submittedUser;
                        }
                    }
                }
            }
            catch { /* malformed Base64 — fall through to 401 */ }
        }

        if (authenticated)
        {
            // Clear any recorded failures for this IP on successful login
            lock (authLock) authState.Remove(ip);
            await next();
            return;
        }

        // Unconditional delay on every failure to slow brute-force attempts
        await Task.Delay(200);

        lock (authLock)
        {
            // Trim stale entries when the table grows large
            if (authState.Count >= MaxTrackedIPs)
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
                foreach (var key in authState.Keys.Where(k => authState[k].LockedUntil < cutoff).ToList())
                    authState.Remove(key);
            }

            authState.TryGetValue(ip, out var cur);
            var failures  = cur.Failures + 1;
            var lockUntil = failures >= MaxFailures
                ? DateTimeOffset.UtcNow.AddSeconds(LockoutSeconds)
                : cur.LockedUntil;
            authState[ip] = (failures, lockUntil);
        }

        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"FileBeam\"";
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
    });
}

// ── CSRF validation for all state-changing POST requests ─────────────────────
// The token is generated once at startup and embedded in every served HTML page.
// JavaScript includes it in every XHR/form submission.
// ReadFormAsync caches the parsed form, so route handlers can call it safely afterwards.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Method == "POST")
    {
        var form = await ctx.Request.ReadFormAsync();
        if (form["_csrf"] != csrfToken)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});

app.MapGet("/",                     handlers.ListDirectory);
app.MapGet("/browse/{**subpath}",   handlers.BrowseDirectory);
app.MapGet("/download/{**subpath}",     handlers.DownloadFile);
app.MapGet("/download-zip/{**subpath}", handlers.DownloadZip);
app.MapPost("/upload/{**subpath}",  handlers.UploadFiles);
app.MapPost("/delete/{**subpath}",  handlers.DeleteFile);
app.MapPost("/rename/{**subpath}",  handlers.RenameFile);
app.MapPost("/share/{**subpath}",   handlers.CreateShareLink);
app.MapGet("/s/{token}",            handlers.RedeemShareLink);
app.MapPost("/mkdir/{**subpath}",   handlers.MkDir);
app.MapGet("/disk-space",           handlers.DiskSpace);
app.MapGet("/events",               handlers.FileEvents);

// ── My Uploads (per-sender scoped view of upload dir) ─────────────────────────
app.MapGet("/my-uploads",                       handlers.BrowseMyUploads);
app.MapGet("/my-uploads/browse/{**subpath}",    handlers.BrowseMyUploads);
app.MapGet("/my-uploads/download/{**subpath}",  handlers.DownloadMyUpload);

// ── Admin: full upload dir browse + share token list + revocation ──────────────
app.MapGet("/admin/uploads",                handlers.BrowseAdminUploads);
app.MapGet("/admin/uploads/{**subpath}",    handlers.BrowseAdminUploads);
app.MapGet("/admin/shares",                 handlers.ListShareTokens);
app.MapGet("/admin/revoke",                 handlers.ListRevocations);
app.MapPost("/admin/revoke/user/{username}", handlers.RevokeUser);
app.MapPost("/admin/unrevoke/user/{username}", handlers.UnrevokeUser);
app.MapPost("/admin/revoke/ip/{ip}",        handlers.RevokeIp);
app.MapPost("/admin/unrevoke/ip/{ip}",      handlers.UnrevokeIp);

try   { await app.RunAsync(); }
finally
{
    credWatcher?.Dispose();
    if (auditLogger is not null) await auditLogger.DisposeAsync();
}
return 0;
