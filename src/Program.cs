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
string? cliAdminUsername    = null;
string? cliAdminPassword    = null;
int?    cliPort             = null;
bool?   cliReadOnly         = null;
bool?   cliPerSender        = null;
string? cliConfigFile       = null;
string? cliInvitesFile      = null;
bool    printConfig         = false;
long    maxFileSize         = 0;   // bytes; 0 = unlimited
long    maxUploadBytes      = 0;   // per-sender cumulative bytes; 0 = unlimited
long    maxUploadTotal      = 0;   // total upload directory cap; 0 = unlimited
long?   maxUploadSize       = null; // null = keep large default; 0 = unlimited
string? cliTlsCert          = null;
string? cliTlsKey           = null;
string? cliTlsPfx           = null;
string? cliTlsPfxPassword   = null;
string? auditLogPath        = null;
long    auditLogMaxSize     = 0;
int     shareTtl            = 3600; // default share link TTL in seconds
int     rateLimit           = 60;  // requests per minute per IP
string  logLevel            = "info"; // info | debug
TimeSpan? uploadTtl         = null; // auto-delete TTL for uploaded files; null = disabled
int     maxConcurrentZips   = 2;   // max simultaneous ZIP streams; 0 = unlimited
long    maxZipBytes         = 0;   // max directory size for ZIP; 0 = unlimited
bool    cliNoQrAutologin    = false; // --no-qr-autologin: skip embedding token in startup QR

for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--download"         || args[i] == "-s")    && i + 1 < args.Length)
        cliDownloadDir = args[++i];
    else if ((args[i] == "--upload"      || args[i] == "-d")    && i + 1 < args.Length)
        cliUploadDir = args[++i];
    else if ((args[i] == "--port"        || args[i] == "-p")    && i + 1 < args.Length)
        cliPort = int.TryParse(args[++i], out var p) ? p : null;
    else if (args[i] == "--admin-username"                      && i + 1 < args.Length)
        cliAdminUsername = args[++i];
    else if (args[i] == "--admin-password"                      && i + 1 < args.Length)
        cliAdminPassword = args[++i];
    else if (args[i] == "--readonly"     || args[i] == "-r")
        cliReadOnly = true;
    else if (args[i] == "--per-sender")
        cliPerSender = true;
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
    else if (args[i] == "--tls-pfx" && i + 1 < args.Length)
        cliTlsPfx = args[++i];
    else if (args[i] == "--tls-pfx-password" && i + 1 < args.Length)
        cliTlsPfxPassword = args[++i];
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
    else if (args[i] == "--config"          && i + 1 < args.Length)
        cliConfigFile = args[++i];
    else if (args[i] == "--invites-file"    && i + 1 < args.Length)
        cliInvitesFile = args[++i];
    else if (args[i] == "--upload-ttl" && i + 1 < args.Length)
    {
        var raw = args[++i];
        if (!TryParseDuration(raw, out var parsedTtl))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --upload-ttl invalid value '{Markup.Escape(raw)}'. " +
                "Use e.g. 30m, 24h, 7d, or a number of seconds.");
            return 1;
        }
        uploadTtl = parsedTtl;
    }
    else if (args[i] == "--max-concurrent-zips" && i + 1 < args.Length)
        maxConcurrentZips = int.TryParse(args[++i], out var mz) ? Math.Max(0, mz) : 2;
    else if (args[i] == "--max-zip-size" && i + 1 < args.Length)
    {
        var raw = args[++i];
        if (!TryParseSize(raw, out maxZipBytes))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --max-zip-size invalid value '{Markup.Escape(raw)}'. " +
                "Use e.g. 10GB, 500MB, or 'unlimited'.");
            return 1;
        }
    }
    else if (args[i] == "--print-config")
        printConfig = true;
    else if (args[i] == "--no-qr-autologin")
        cliNoQrAutologin = true;
}

// ── Load config file (filebeam.json in CWD or --config path) ──────────────────
{
    var cwdConfig = Path.Combine(Directory.GetCurrentDirectory(), "filebeam.json");
    var configPath = cliConfigFile is not null
        ? Path.GetFullPath(cliConfigFile)
        : File.Exists(cwdConfig) ? cwdConfig : null;

    if (configPath is not null)
    {
        if (!FileBeamConfig.TryLoad(configPath, out var cfg, out var cfgErr))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] cannot load config file '{Markup.Escape(configPath)}': {Markup.Escape(cfgErr!)}");
            return 1;
        }

        // Apply config as defaults — CLI flags (set above) take precedence.
        // Nullable cli* vars: ??= only assigns when the CLI left the var null.
        // Value-type vars (maxFileSize etc.): config wins when CLI left them at 0/default.
        cliDownloadDir    ??= cfg!.Download    is not null ? Path.GetFullPath(cfg.Download) : null;
        cliUploadDir      ??= cfg!.Upload      is not null ? Path.GetFullPath(cfg.Upload)   : null;
        cliPort           ??= cfg!.Port;
        cliAdminUsername  ??= cfg!.AdminUsername;
        cliInvitesFile    ??= cfg!.InvitesFile;
        cliReadOnly       ??= cfg!.ReadOnly;
        cliPerSender      ??= cfg!.PerSender;
        cliTlsCert        ??= cfg!.TlsCert;
        cliTlsKey         ??= cfg!.TlsKey;
        cliTlsPfx         ??= cfg!.TlsPfx;
        cliTlsPfxPassword ??= cfg!.TlsPfxPassword;

        if (maxFileSize == 0    && cfg!.MaxFileSize    is not null
            && TryParseSize(cfg.MaxFileSize,    out var cfgMF))  maxFileSize    = cfgMF;
        if (maxUploadBytes == 0 && cfg!.MaxUploadBytes is not null
            && TryParseSize(cfg.MaxUploadBytes, out var cfgMUB)) maxUploadBytes = cfgMUB;
        if (maxUploadTotal == 0 && cfg!.MaxUploadTotal is not null
            && TryParseSize(cfg.MaxUploadTotal, out var cfgMUT)) maxUploadTotal = cfgMUT;
        if (maxUploadSize is null && cfg!.MaxUploadSize is not null
            && TryParseSize(cfg.MaxUploadSize,  out var cfgMUS)) maxUploadSize  = cfgMUS;
        if (shareTtl == 3600    && cfg!.ShareTtl       is not null) shareTtl    = cfg.ShareTtl.Value;
        if (auditLogMaxSize == 0 && cfg!.AuditLogMaxSize is not null
            && TryParseSize(cfg.AuditLogMaxSize, out var cfgALS)) auditLogMaxSize = cfgALS;
        if (rateLimit == 60     && cfg!.RateLimit       is not null) rateLimit   = cfg.RateLimit.Value;
        if (logLevel == "info"  && cfg!.LogLevel        is not null) logLevel    = cfg.LogLevel.ToLowerInvariant();
        auditLogPath ??= cfg!.AuditLog;
        if (uploadTtl is null && cfg!.UploadTtl is not null
            && TryParseDuration(cfg.UploadTtl, out var cfgUploadTtl)) uploadTtl = cfgUploadTtl;
        if (maxConcurrentZips == 2 && cfg!.MaxConcurrentZips.HasValue)
            maxConcurrentZips = Math.Max(0, cfg.MaxConcurrentZips.Value);
        if (maxZipBytes == 0 && cfg!.MaxZipSize is not null)
            TryParseSize(cfg.MaxZipSize, out maxZipBytes);
        if (!cliNoQrAutologin && cfg!.QrAutologin == false)
            cliNoQrAutologin = true;

        AnsiConsole.MarkupLine($"[grey]Config loaded: {Markup.Escape(configPath)}[/]");
    }
}

// Resolve bool flags (nullable CLI vars default to false if neither CLI nor config set them)
bool readOnly      = cliReadOnly  ?? false;
bool perSender     = cliPerSender ?? false;
bool qrAutologin   = !cliNoQrAutologin;

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

/// <summary>
/// Parses a human-readable duration string (e.g. "30m", "24h", "7d", "3600").
/// Plain integers are treated as seconds. Returns false on invalid input.
/// </summary>
static bool TryParseDuration(string raw, out TimeSpan result)
{
    result = default;
    if (string.IsNullOrWhiteSpace(raw)) return false;

    var s = raw.Trim();

    // Plain integer → seconds
    if (long.TryParse(s, out var secs) && secs > 0)
    {
        result = TimeSpan.FromSeconds(secs);
        return true;
    }

    if (s.Length < 2) return false;
    var suffix = s[^1];
    if (!long.TryParse(s[..^1], out var num) || num <= 0) return false;

    result = suffix switch
    {
        's' or 'S' => TimeSpan.FromSeconds(num),
        'm' or 'M' => TimeSpan.FromMinutes(num),
        'h' or 'H' => TimeSpan.FromHours(num),
        'd' or 'D' => TimeSpan.FromDays(num),
        _ => TimeSpan.Zero
    };
    return result > TimeSpan.Zero;
}

/// <summary>
/// Formats a TimeSpan as a compact human-readable string (e.g. "30m", "24h", "7d").
/// </summary>
static string FormatDuration(TimeSpan ts)
{
    if (ts.TotalSeconds < 60)    return $"{(long)ts.TotalSeconds}s";
    if (ts.TotalMinutes < 60)    return $"{(long)ts.TotalMinutes}m";
    if (ts.TotalHours   < 24)    return $"{(long)ts.TotalHours}h";
    return $"{(long)ts.TotalDays}d";
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
    Directory.CreateDirectory(serveDir);

// Upload directory: where uploads land (defaults to serve dir if not specified).
// When a separate upload dir is given, uploaded files are private — browsers only see the serve dir.
string uploadDir = cliUploadDir ?? (interactive
    ? AnsiConsole.Ask<string>("[bold]Upload directory[/] [grey][[press Enter for same as download]][/]:", serveDir)
    : serveDir);

uploadDir = Path.GetFullPath(uploadDir);

if (!Directory.Exists(uploadDir))
    Directory.CreateDirectory(uploadDir);

int port = cliPort ?? (interactive ? AnsiConsole.Ask("[bold]Port[/]:", 8080) : 8080);

// ── Invite store (optional) ────────────────────────────────────────────────────
InviteStore inviteStore = new(cliInvitesFile is not null ? Path.GetFullPath(cliInvitesFile) : null);

// ── Per-session HMAC key for signing invite cookies ───────────────────────────
var sessionKey = RandomNumberGenerator.GetBytes(32);

// ── Resolve admin credentials ─────────────────────────────────────────────────
var adminUsername    = cliAdminUsername ?? "admin";
var adminKeyFilePath = Path.Combine(Directory.GetCurrentDirectory(), "filebeam-admin.key");
var adminPassword    = AdminAuth.ResolveAdminPassword(
    Environment.GetEnvironmentVariable("FILEBEAM_ADMIN_PASSWORD"),
    cliAdminPassword,
    adminKeyFilePath,
    out bool adminPasswordGenerated);

if (adminPasswordGenerated)
    AnsiConsole.MarkupLine(
        $"[yellow bold]Admin password auto-generated:[/] {Markup.Escape(adminPassword)}\n" +
        $"[grey]Saved to: {Markup.Escape(adminKeyFilePath)}[/]\n");

// ── Validate TLS cert/key or PFX (optional, mutually exclusive) ───────────────
X509Certificate2? tlsCertificate = null;
bool hasPem = cliTlsCert != null || cliTlsKey != null;
bool hasPfx = cliTlsPfx  != null;

if (hasPem && hasPfx)
{
    AnsiConsole.MarkupLine("[red]Error:[/] --tls-cert/--tls-key and --tls-pfx are mutually exclusive. Use one or the other.");
    return 1;
}

if (hasPem)
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

    // Round-trip through PKCS12 so Windows SChannel can access the private key
    // during the TLS handshake (ephemeral in-memory keys are not usable by SChannel).
    try
    {
        using var ephemeral = X509Certificate2.CreateFromPemFile(absCert, absKey);
        tlsCertificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] failed to load TLS certificate: {Markup.Escape(ex.Message)}");
        return 1;
    }
}
else if (hasPfx)
{
    var absPfx = Path.GetFullPath(cliTlsPfx!);

    if (!File.Exists(absPfx))
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] --tls-pfx file not found: {Markup.Escape(absPfx)}");
        return 1;
    }
    try { _ = File.ReadAllBytes(absPfx); }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] cannot read --tls-pfx: {Markup.Escape(ex.Message)}");
        return 1;
    }

    // LoadPkcs12FromFile registers the key with Windows CNG directly — no round-trip needed.
    try
    {
        tlsCertificate = X509CertificateLoader.LoadPkcs12FromFile(absPfx, cliTlsPfxPassword);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] failed to load TLS PFX certificate: {Markup.Escape(ex.Message)}");
        return 1;
    }
}

// ── --print-config: dump effective config as JSON and exit ────────────────────
if (printConfig)
{
    Console.WriteLine(FileBeamConfig.ToDisplayJson(
        download:          serveDir,
        upload:            uploadDir,
        port:              port,
        adminUsername:     adminUsername != "admin" ? adminUsername : null,
        invitesFile:       cliInvitesFile,
        readOnly:          readOnly,
        perSender:         perSender,
        maxFileSize:       maxFileSize,
        maxUploadBytes:    maxUploadBytes,
        maxUploadTotal:    maxUploadTotal,
        maxUploadSize:     maxUploadSize,
        tlsCert:           cliTlsCert,
        tlsKey:            cliTlsKey,
        tlsPfx:            cliTlsPfx,
        tlsPfxPassword:    cliTlsPfxPassword,
        shareTtl:          shareTtl,
        auditLog:          auditLogPath,
        auditLogMaxSize:   auditLogMaxSize,
        rateLimit:         rateLimit,
        logLevel:          logLevel,
        uploadTtl:         uploadTtl.HasValue ? FormatDuration(uploadTtl.Value) : null,
        maxConcurrentZips: maxConcurrentZips,
        maxZipBytes:       maxZipBytes));
    return 0;
}

// ── Resolve LAN IPs ────────────────────────────────────────────────────────────
/*
var ips = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up
             && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
    .Select(a => a.Address.ToString())
    .ToList();
*/
var ips = NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == OperationalStatus.Up
             && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
    .Where(n => n.GetIPProperties().GatewayAddresses.Any()) // Must have a gateway
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
        $"[bold]Admin:[/]    {Markup.Escape(adminUsername)}" +
        (adminPasswordGenerated
            ? " [yellow](auto-generated — printed above)[/]"
            : tlsCertificate is null ? " [yellow](HTTP — credentials unencrypted)[/]" : "") + "\n" +
        (cliInvitesFile is not null
            ? $"[bold]Invites:[/]  {inviteStore.GetAll().Count} token{(inviteStore.GetAll().Count == 1 ? "" : "s")} loaded from {Markup.Escape(Path.GetFileName(cliInvitesFile))}\n"
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
var scheme  = tlsCertificate != null ? "https" : "http";
var baseUrl = ips.Count > 0 ? $"{scheme}://{ips[0]}:{port}" : $"{scheme}://localhost:{port}";

AutoLoginStore? autoLoginStore = null;
string qrUrl = baseUrl;
if (qrAutologin)
{
    autoLoginStore = new AutoLoginStore();
    var autoToken  = autoLoginStore.Generate();
    qrUrl = $"{baseUrl}/auto-login/{autoToken.Token}";
}

using var qrGen  = new QRCodeGenerator();
var qrData       = qrGen.CreateQrCode(qrUrl, QRCodeGenerator.ECCLevel.L);
var asciiQr      = new AsciiQRCode(qrData);
AnsiConsole.WriteLine(asciiQr.GetGraphicSmall());

if (qrAutologin && autoLoginStore is not null)
{
    var exp = autoLoginStore.GetActive()!.ExpiresAt;
    AnsiConsole.MarkupLine($"[grey]\u23f1 QR auto-login expires at {exp:HH:mm:ss} UTC (5 min)[/]");
}

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
        if (tlsCertificate != null) {
            listenOpts.UseHttps(tlsCertificate);
        }
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

// ── Upload expiry (optional) ──────────────────────────────────────────────────
// --upload-ttl <duration>  → auto-delete files from uploadDir after TTL
// (absent)                 → no auto-deletion
UploadExpirer? uploadExpirer = uploadTtl.HasValue
    ? new UploadExpirer(uploadDir, uploadTtl.Value, perSender, adminUsername,
        log: line =>
        {
            var t = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            AnsiConsole.MarkupLine($"[grey]{t}[/]  {Markup.Escape(line)}");
        })
    : null;

Action<string, string>? debugLog = logLevel == "debug"
    ? (reqId, msg) =>
      {
          var t = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
          AnsiConsole.MarkupLine($"[grey]{t}[/]  [dim][[DBG]][/]   [grey][[{reqId}]][/]  [grey]---[/]  [grey]---[/]  {Markup.Escape(msg)}");
      }
    : null;

var configJson = FileBeamConfig.ToDisplayJson(
    download:          serveDir,
    upload:            uploadDir,
    port:              port,
    adminUsername:     adminUsername != "admin" ? adminUsername : null,
    invitesFile:       cliInvitesFile,
    readOnly:          readOnly,
    perSender:         perSender,
    maxFileSize:       maxFileSize,
    maxUploadBytes:    maxUploadBytes,
    maxUploadTotal:    maxUploadTotal,
    maxUploadSize:     maxUploadSize,
    tlsCert:           cliTlsCert,
    tlsKey:            cliTlsKey,
    tlsPfx:            cliTlsPfx,
    tlsPfxPassword:    cliTlsPfxPassword,
    shareTtl:          shareTtl,
    auditLog:          auditLogPath,
    auditLogMaxSize:   auditLogMaxSize,
    rateLimit:         rateLimit,
    logLevel:          logLevel,
    uploadTtl:         uploadTtl.HasValue ? FormatDuration(uploadTtl.Value) : null,
    maxConcurrentZips: maxConcurrentZips,
    maxZipBytes:       maxZipBytes);

var cliCommand = FileBeamConfig.ToCliCommand(
    download:          serveDir,
    upload:            uploadDir,
    port:              port,
    adminUsername:     adminUsername != "admin" ? adminUsername : null,
    invitesFile:       cliInvitesFile,
    readOnly:          readOnly,
    perSender:         perSender,
    maxFileSize:       maxFileSize,
    maxUploadBytes:    maxUploadBytes,
    maxUploadTotal:    maxUploadTotal,
    maxUploadSize:     maxUploadSize,
    tlsCert:           cliTlsCert,
    tlsKey:            cliTlsKey,
    tlsPfx:            cliTlsPfx,
    tlsPfxPassword:    cliTlsPfxPassword,
    shareTtl:          shareTtl,
    auditLog:          auditLogPath,
    auditLogMaxSize:   auditLogMaxSize,
    rateLimit:         rateLimit,
    logLevel:          logLevel,
    uploadTtl:         uploadTtl.HasValue ? FormatDuration(uploadTtl.Value) : null,
    maxConcurrentZips: maxConcurrentZips,
    maxZipBytes:       maxZipBytes,
    qrAutologin:       qrAutologin);

var sessionRegistry = new SessionRegistry();

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
    inviteStore:            inviteStore,
    sessionKey:             sessionKey,
    isTls:                  tlsCertificate is not null,
    debugLog:               debugLog,
    configJson:             configJson,
    cliCommand:             cliCommand,
    auditLogPath:           auditLogPath,
    uploadTtl:              uploadTtl,
    adminExemptPath:        uploadExpirer?.AdminSubfolder,
    sessionRegistry:        sessionRegistry,
    maxConcurrentZips:      maxConcurrentZips,
    maxZipBytes:            maxZipBytes,
    autoLoginStore:         autoLoginStore);

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
                    ("POST", var p) when p.StartsWith("/rename-dir/",   StringComparison.OrdinalIgnoreCase) => "rename-dir",
                    _ => null
                };
                if (auditAction is not null)
                {
                    // Use the username set by the auth middleware (works for all auth methods)
                    string? auditUser = ctx.Items.TryGetValue("fb.user", out var fbUserObj) && fbUserObj is string fbUserStr
                        ? fbUserStr
                        : null;

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

// ── Auth middleware (always active — admin account always exists) ─────────────
// Per-IP failed-attempt tracking. Entries are trimmed when the dict grows large
// to prevent unbounded memory growth from spoofed source IPs.
{
    var authState = new Dictionary<string, (int Failures, DateTimeOffset LockedUntil)>();
    var authLock  = new object();
    const int MaxFailures    = 10;
    const int LockoutSeconds = 60;
    const int MaxTrackedIPs  = 10_000;

    app.Use(async (ctx, next) =>
    {
        // Share link redemption, invite join, and auto-login are always unauthenticated
        if (ctx.Request.Method == "GET" &&
            (ctx.Request.Path.StartsWithSegments("/s") ||
             ctx.Request.Path.StartsWithSegments("/join") ||
             ctx.Request.Path.StartsWithSegments("/auto-login")))
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

        // Check brute-force lockout
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
        var  header        = ctx.Request.Headers.Authorization.ToString();

        // 1. Basic Auth — admin username + password → role "admin"
        if (AdminAuth.TryAdminBasicAuth(header, adminUsername, adminPassword, out var basicUser))
        {
            ctx.Items["fb.role"] = "admin";
            ctx.Items["fb.user"] = basicUser;
            authenticated = true;
        }

        // 2. Bearer token — invite store lookup (UseCount NOT incremented)
        if (!authenticated &&
            AdminAuth.TryBearerAuth(header, inviteStore, out var bearerRole, out var bearerUser))
        {
            ctx.Items["fb.role"] = bearerRole;
            ctx.Items["fb.user"] = bearerUser;
            authenticated = true;

            // Track session for active sessions dashboard
            var bearerTokenId = header[7..].Trim();
            if (inviteStore.TryGet(bearerTokenId, out var bearerInvite))
                sessionRegistry.Touch(bearerTokenId, bearerInvite!.FriendlyName, bearerRole!, ip, "bearer");
        }

        // 3. Session cookie — invite-based browser auth
        var sessionCookie = ctx.Request.Cookies["fb.session"];
        if (!authenticated && sessionCookie is not null &&
            RouteHandlers.TryValidateSessionCookie(sessionCookie, sessionKey, inviteStore, out var cookieRole, out var cookieUser, out var cookieInviteId))
        {
            ctx.Items["fb.role"] = cookieRole;
            ctx.Items["fb.user"] = cookieUser;
            authenticated = true;

            // Track session for active sessions dashboard
            if (cookieInviteId is not null && inviteStore.TryGet(cookieInviteId, out var cookieInvite))
                sessionRegistry.Touch(cookieInviteId, cookieInvite!.FriendlyName, cookieRole!, ip, "cookie");
        }

        if (authenticated)
        {
            lock (authLock) authState.Remove(ip);
            await next();
            return;
        }

        // Unconditional delay on every failure to slow brute-force attempts
        await Task.Delay(200);

        lock (authLock)
        {
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

// ── CSRF validation for all state-changing requests ───────────────────────────
// The token is generated once at startup and embedded in every served HTML page.
// JavaScript includes it via form field _csrf (legacy forms) or X-CSRF-Token header
// (JSON API calls using fetch with DELETE/PATCH/PUT or application/json POST).
// ReadFormAsync caches the parsed form, so route handlers can call it safely afterwards.
app.Use(async (ctx, next) =>
{
    var method = ctx.Request.Method;
    if (method is "POST" or "DELETE" or "PATCH" or "PUT")
    {
        // 1. Accept token from request header (JSON API and non-POST verbs)
        bool valid = ctx.Request.Headers.TryGetValue("X-CSRF-Token", out var headerToken)
                     && headerToken == csrfToken;

        // 2. For POST, also accept token from form body (HTML form submissions)
        if (!valid && method == "POST")
        {
            var form = await ctx.Request.ReadFormAsync();
            valid = form["_csrf"] == csrfToken;
        }

        if (!valid)
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
app.MapGet("/info/{**subpath}",         handlers.InfoFile);
app.MapPost("/upload/{**subpath}",  handlers.UploadFiles);
app.MapPost("/delete/{**subpath}",  handlers.DeleteFile);
app.MapPost("/delete-dir/{**subpath}", handlers.DeleteDir);
app.MapPost("/rename/{**subpath}",     handlers.RenameFile);
app.MapPost("/rename-dir/{**subpath}", handlers.RenameDir);
app.MapPost("/share/{**subpath}",   handlers.CreateShareLink);
app.MapGet("/s/{token}",            handlers.RedeemShareLink);
app.MapPost("/mkdir/{**subpath}",   handlers.MkDir);
app.MapGet("/upload-area",                          handlers.BrowseUploadArea);
app.MapGet("/upload-area/browse/{**subpath}",       handlers.BrowseUploadArea);
app.MapGet("/upload-area/download/{**subpath}",     handlers.DownloadUploadAreaFile);
app.MapGet("/upload-area/download-zip/{**subpath}", handlers.DownloadUploadAreaZip);
app.MapPost("/upload-area/upload/{**subpath}",      handlers.UploadToUploadArea);
app.MapGet("/disk-space",                           handlers.DiskSpace);
app.MapGet("/events",               handlers.FileEvents);

// ── My Uploads (per-sender scoped view of upload dir) ─────────────────────────
app.MapGet("/my-uploads",                         handlers.BrowseMyUploads);
app.MapGet("/my-uploads/browse/{**subpath}",      handlers.BrowseMyUploads);
app.MapGet("/my-uploads/download/{**subpath}",        handlers.DownloadMyUpload);
app.MapGet("/my-uploads/download-zip/{**subpath}",    handlers.DownloadMyUploadsZip);
app.MapGet("/my-uploads/info/{**subpath}",            handlers.InfoMyUpload);
app.MapPost("/my-uploads/upload/{**subpath}",     handlers.UploadToMyUploads);
app.MapPost("/my-uploads/delete/{**subpath}",         handlers.DeleteMyUpload);
app.MapPost("/my-uploads/rename/{**subpath}",         handlers.RenameMyUpload);
app.MapPost("/my-uploads/rename-dir/{**subpath}",     handlers.RenameMyUploadDir);

// ── Admin: full upload dir browse + share token list + revocation ──────────────
app.MapGet("/admin/uploads",                            handlers.BrowseAdminUploads);
app.MapGet("/admin/uploads/browse/{**subpath}",         handlers.BrowseAdminUploads);
app.MapGet("/admin/uploads/download/{**subpath}",       handlers.DownloadAdminUpload);
app.MapPost("/admin/uploads/delete/{**subpath}",            handlers.DeleteAdminUpload);
app.MapPost("/admin/uploads/rename/{**subpath}",            handlers.RenameAdminUpload);
app.MapPost("/admin/uploads/rename-dir/{**subpath}",        handlers.RenameAdminUploadDir);
// catch-all for direct subpath access (must be after the more-specific routes)
app.MapGet("/admin/uploads/{**subpath}",                handlers.BrowseAdminUploads);
app.MapGet("/admin/shares",                 handlers.ListShareTokens);
app.MapGet("/admin/revoke",                 handlers.ListRevocations);
app.MapPost("/admin/revoke/user/{username}", handlers.RevokeUser);
app.MapPost("/admin/unrevoke/user/{username}", handlers.UnrevokeUser);
app.MapPost("/admin/revoke/ip/{ip}",        handlers.RevokeIp);
app.MapPost("/admin/unrevoke/ip/{ip}",      handlers.UnrevokeIp);

// ── Admin: config export ───────────────────────────────────────────────────────
app.MapGet("/admin/config",                 handlers.GetAdminConfig);

// ── Admin: audit log viewer ────────────────────────────────────────────────────
app.MapGet("/admin/audit",                  handlers.GetAuditLog);

// ── Admin: active sessions dashboard ──────────────────────────────────────────
app.MapGet("/admin/sessions",                       handlers.GetAdminSessions);
app.MapPost("/admin/sessions/{id}/revoke",          handlers.RevokeSession);

// ── Admin: invite management ───────────────────────────────────────────────────
app.MapPost("/admin/invites",               (Delegate)handlers.CreateInvite);
app.MapGet("/admin/invites",                handlers.ListInvites);
app.MapDelete("/admin/invites/{id}",        handlers.RevokeInvite);
app.MapPatch("/admin/invites/{id}",         (Delegate)handlers.EditInvite);

// ── Invite join (unauthenticated) ─────────────────────────────────────────────
app.MapGet("/join/{token}",                 handlers.JoinWithInvite);

// ── Auto-login (unauthenticated — exempt in auth middleware) ──────────────────
app.MapGet("/auto-login/{token}",           handlers.RedeemAutoLogin);
app.MapGet("/admin/qr",                     handlers.GetAdminQr);

try   { await app.RunAsync(); }
finally
{
    if (auditLogger     is not null) await auditLogger.DisposeAsync();
    if (uploadExpirer   is not null) await uploadExpirer.DisposeAsync();
}
return 0;
