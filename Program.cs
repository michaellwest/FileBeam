using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FileBeam;
using Spectre.Console;

// ── Parse CLI args ─────────────────────────────────────────────────────────────
string? cliDir = null;
string? cliPassword = null;
int? cliPort = null;

for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "--dir" || args[i] == "-d") && i + 1 < args.Length)
        cliDir = args[++i];
    else if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
        cliPort = int.TryParse(args[++i], out var p) ? p : null;
    else if ((args[i] == "--password" || args[i] == "--pw") && i + 1 < args.Length)
        cliPassword = args[++i];
}

// ── Banner ─────────────────────────────────────────────────────────────────────
AnsiConsole.Write(new FigletText("FileBeam").Color(Color.DodgerBlue1));
AnsiConsole.MarkupLine("[grey]Instant LAN file server[/]\n");

// ── Interactive prompts (skip if CLI args provided) ───────────────────────────
string serveDir = cliDir ?? AnsiConsole.Ask<string>(
    "[bold]Directory to serve[/] [grey][[press Enter for current]][/]:",
    Directory.GetCurrentDirectory());

if (!Directory.Exists(serveDir))
{
    AnsiConsole.MarkupLine($"[red]Directory not found:[/] {serveDir}");
    return 1;
}

int port = cliPort ?? AnsiConsole.Ask("[bold]Port[/]:", 8080);

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
var panel = new Panel(
    Align.Left(new Markup(
        $"[bold]Serving:[/]  {serveDir}\n" +
        (!string.IsNullOrEmpty(password) ? "[bold]Auth:[/]     Password required\n" : "") +
        string.Join("\n", ips.Select(ip => $"[bold]URL:[/]      [link]http://{ip}:{port}[/]")) +
        (ips.Count == 0 ? $"\n[bold]URL:[/]      http://localhost:{port}" : ""))))
{
    Header = new PanelHeader(" FileBeam is running ", Justify.Center),
    Border = BoxBorder.Rounded,
    BorderStyle = new Style(Color.DodgerBlue1),
    Padding = new Padding(1, 0)
};
AnsiConsole.Write(panel);
AnsiConsole.MarkupLine("\n[grey]Press Ctrl+C to stop.[/]\n");

// ── Build & start Kestrel ──────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders(); // suppress default ASP.NET logging; we do our own
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Listen(IPAddress.Any, port);
    opts.Limits.MaxRequestBodySize = 100L * 1024 * 1024 * 1024; // 100 GB
});

// Allow large multipart uploads (default is 30 MB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 100L * 1024 * 1024 * 1024; // 100 GB
});

var app = builder.Build();

// Wire up FileWatcher and route handlers
using var fileWatcher = new FileWatcher(serveDir);
var handlers = new RouteHandlers(serveDir, fileWatcher);

app.MapGet("/",                     handlers.ListDirectory);
app.MapGet("/browse/{**subpath}",   handlers.BrowseDirectory);
app.MapGet("/download/{**subpath}", handlers.DownloadFile);
app.MapPost("/upload/{**subpath}",  handlers.UploadFiles);
app.MapGet("/events",               handlers.FileEvents);

// ── Console request log (with elapsed time) ───────────────────────────────────
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
    AnsiConsole.MarkupLine(
        $"[grey]{time}[/]  [{color}]{status}[/]  [bold]{method,-6}[/] {Markup.Escape(path)}  [grey]{ip}  {sw.ElapsedMilliseconds}ms[/]");
});

// ── Basic Auth (optional) ─────────────────────────────────────────────────────
if (!string.IsNullOrEmpty(password))
{
    app.Use(async (ctx, next) =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var colon   = decoded.IndexOf(':');
                // Accept any username; only the password is checked.
                if (colon >= 0 && decoded[(colon + 1)..] == password)
                {
                    await next();
                    return;
                }
            }
            catch { /* malformed Base64 — fall through to 401 */ }
        }

        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"FileBeam\"";
        ctx.Response.StatusCode = 401;
    });
}

await app.RunAsync();
return 0;
