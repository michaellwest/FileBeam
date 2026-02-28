using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using FileBeam;
using QRCoder;
using Spectre.Console;

// ── Parse CLI args ─────────────────────────────────────────────────────────────
string? cliDownloadDir = null;
string? cliUploadDir   = null;
string? cliPassword    = null;
int?    cliPort        = null;
bool    readOnly       = false;
bool    perSender      = false;
long    maxFileSize    = 0;   // bytes; 0 = unlimited
long    maxUploadBytes = 0;   // per-sender cumulative bytes; 0 = unlimited
int     rateLimit      = 60;  // requests per minute per IP

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
    else if (args[i] == "--readonly"     || args[i] == "-r")
        readOnly = true;
    else if (args[i] == "--per-sender")
        perSender = true;
    else if (args[i] == "--max-file-size"  && i + 1 < args.Length)
        maxFileSize = long.TryParse(args[++i], out var mf) ? mf : 0;
    else if (args[i] == "--max-upload-bytes" && i + 1 < args.Length)
        maxUploadBytes = long.TryParse(args[++i], out var mu) ? mu : 0;
    else if (args[i] == "--rate-limit"   && i + 1 < args.Length)
        rateLimit = int.TryParse(args[++i], out var rl) ? rl : 60;
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

if (!Directory.Exists(uploadDir))
    Directory.CreateDirectory(uploadDir);

int port = cliPort ?? (interactive ? AnsiConsole.Ask("[bold]Port[/]:", 8080) : 8080);

string? password = cliPassword;

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
        (readOnly     ? "[bold yellow]Mode:[/]     Read-only (uploads disabled)\n" : "") +
        (!string.IsNullOrEmpty(password)
            ? "[bold]Auth:[/]     Password required [yellow](HTTP — credentials unencrypted)[/]\n"
            : "") +
        string.Join("\n", ips.Select(ip => $"[bold]URL:[/]      [link]http://{ip}:{port}[/]")) +
        (ips.Count == 0 ? $"\n[bold]URL:[/]      http://localhost:{port}" : ""))))
{
    Header = new PanelHeader(" FileBeam is running ", Justify.Center),
    Border = BoxBorder.Rounded,
    BorderStyle = new Style(Color.DodgerBlue1),
    Padding = new Padding(1, 0)
};
AnsiConsole.Write(panel);

// ── QR code for quick mobile access ──────────────────────────────────────────
var qrUrl = ips.Count > 0 ? $"http://{ips[0]}:{port}" : $"http://localhost:{port}";
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
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Listen(IPAddress.Any, port);
    opts.Limits.MaxRequestBodySize = maxFileSize > 0
        ? maxFileSize + (1024 * 1024)   // add 1 MB headroom for multipart framing
        : 100L * 1024 * 1024 * 1024;    // 100 GB default cap
});

// Allow large multipart uploads (default is 30 MB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxFileSize > 0
        ? maxFileSize + (1024 * 1024)
        : 100L * 1024 * 1024 * 1024;
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
var handlers = new RouteHandlers(
    serveDir, uploadDir, fileWatcher,
    isReadOnly:             readOnly,
    perSender:              perSender,
    maxFileSize:            maxFileSize,
    maxUploadBytesPerSender:maxUploadBytes,
    csrfToken:              csrfToken);

// ── Console request log (with elapsed time) ──────────────────────────────────
// Must be registered before route mappings so it wraps endpoint execution.
// Registering it after MapGet/MapPost in WebApplication places it after the
// endpoint middleware, causing it to call next() on an already-started response
// which throws in Production (works in Development only because
// UseDeveloperExceptionPage masks the error).
app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    await next();
    sw.Stop();

    // Suppress noisy SSE keepalive log entries
    if (ctx.Request.Path == "/events") return;

    var ip     = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    var method = ctx.Request.Method;
    var path   = ctx.Request.Path.Value ?? "/";
    var status = ctx.Response.StatusCode;
    var color  = status >= 400 ? "red" : status >= 300 ? "yellow" : "green";
    var time   = DateTime.Now.ToString("HH:mm:ss");

    // Build optional transfer info suffix (file name + size for uploads/downloads)
    string transfer = "";
    if (ctx.Items.TryGetValue("fb.bytes", out var bytesObj) && bytesObj is long bytes)
    {
        var size = FormatBytes(bytes);
        if (ctx.Items.TryGetValue("fb.count", out var countObj) && countObj is int count && count > 1)
            transfer = $"  [grey]{count} files  {size}[/]";
        else if (ctx.Items.TryGetValue("fb.file", out var fileObj) && fileObj is string fileName)
            transfer = $"  [grey]{Markup.Escape(fileName)}  {size}[/]";
        else
            transfer = $"  [grey]{size}[/]";
    }

    AnsiConsole.MarkupLine(
        $"[grey]{time}[/]  [{color}]{status}[/]  [bold]{method,-6}[/] {Markup.Escape(path)}{transfer}  [grey]{ip}  {sw.ElapsedMilliseconds}ms[/]");
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
if (!string.IsNullOrEmpty(password))
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
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var colon   = decoded.IndexOf(':');
                if (colon >= 0)
                {
                    // Constant-time comparison to prevent timing attacks
                    var submitted = Encoding.UTF8.GetBytes(decoded[(colon + 1)..]);
                    var expected  = Encoding.UTF8.GetBytes(password);
                    if (CryptographicOperations.FixedTimeEquals(submitted, expected))
                        authenticated = true;
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
app.MapGet("/download/{**subpath}", handlers.DownloadFile);
app.MapPost("/upload/{**subpath}",  handlers.UploadFiles);
app.MapPost("/delete/{**subpath}",  handlers.DeleteFile);
app.MapPost("/rename/{**subpath}",  handlers.RenameFile);
app.MapGet("/events",               handlers.FileEvents);

await app.RunAsync();
return 0;
