using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using QRCoder;

namespace FileBeam;

/// <summary>
/// Handles admin endpoints: share tokens, invite management, active sessions,
/// revocation, config export, audit log viewer, and the SSE live-reload stream.
/// </summary>
internal sealed class AdminHandlers(HandlerContext ctx)
{
    // In-memory share token store: token → (resolved file path, expiry, creator username).
    private readonly ConcurrentDictionary<string, (string FilePath, DateTimeOffset Expiry, string Creator)> _shareTokens = new();

    // ── Share links ────────────────────────────────────────────────────────────

    // POST /share/{**subpath}?ttl=<seconds>  — create a time-limited download token
    internal IResult CreateShareLink(HttpContext httpCtx, string? subpath)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrEmpty(subpath))
            return Results.BadRequest("No file specified.");

        string resolved;
        try { resolved = ctx.SafeResolvePath(subpath); }
        catch { return Results.StatusCode(StatusCodes.Status403Forbidden); }

        if (!File.Exists(resolved))
            return Results.NotFound("File not found.");

        var ttl = ctx.ShareTtlSeconds;
        if (httpCtx.Request.Query.TryGetValue("ttl", out var ttlStr)
            && int.TryParse(ttlStr, out var parsedTtl) && parsedTtl > 0)
            ttl = parsedTtl;

        // Lazy eviction of expired tokens before inserting a new one
        foreach (var key in _shareTokens.Keys.ToList())
            if (_shareTokens.TryGetValue(key, out var t) && t.Expiry <= DateTimeOffset.UtcNow)
                _shareTokens.TryRemove(key, out _);

        var creator = httpCtx.Items.TryGetValue("fb.user", out var u) && u is string s ? s : "?";
        var token   = RandomNumberGenerator.GetHexString(64, lowercase: true);
        _shareTokens[token] = (resolved, DateTimeOffset.UtcNow.AddSeconds(ttl), creator);

        return Results.Json(new { url = $"/s/{token}", expiresIn = ttl });
    }

    // GET /s/{token}  — redeem a share token (no auth required)
    internal IResult RedeemShareLink(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_shareTokens.TryGetValue(token, out var entry))
            return Results.NotFound("Share link not found.");

        if (entry.Expiry <= DateTimeOffset.UtcNow)
        {
            _shareTokens.TryRemove(token, out _);
            return Results.StatusCode(StatusCodes.Status410Gone);
        }

        if (!File.Exists(entry.FilePath))
        {
            _shareTokens.TryRemove(token, out _);
            return Results.NotFound("File no longer exists.");
        }

        var info    = new FileInfo(entry.FilePath);
        var mime    = MimeTypes.GetMimeType(entry.FilePath);
        var stream  = new FileStream(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return Results.File(stream, mime, info.Name, enableRangeProcessing: true);
    }

    // GET /admin/shares  — list all live share tokens with creator (admin only)
    internal IResult ListShareTokens(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var now = DateTimeOffset.UtcNow;
        var live = _shareTokens
            .Where(kv => kv.Value.Expiry > now)
            .Select(kv => new
            {
                tokenPrefix = kv.Key[..8] + "…",
                file        = Path.GetRelativePath(ctx.RootDir, kv.Value.FilePath).Replace('\\', '/'),
                creator     = kv.Value.Creator,
                expiresAt   = kv.Value.Expiry.ToString("o"),
                expiresIn   = (int)(kv.Value.Expiry - now).TotalSeconds
            })
            .OrderBy(t => t.expiresAt)
            .ToList();

        return Results.Json(live);
    }

    // ── Revocation ─────────────────────────────────────────────────────────────

    // GET /admin/revoke  — list active bans (admin only)
    internal IResult ListRevocations(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return Results.Json(new
        {
            users = ctx.RevocationStore?.RevokedUsers ?? [],
            ips   = ctx.RevocationStore?.RevokedIps   ?? []
        });
    }

    // POST /admin/revoke/user/{username}
    internal IResult RevokeUser(HttpContext httpCtx, string username)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(username))
            return Results.BadRequest("Username required.");

        ctx.RevocationStore?.RevokeUser(username);
        return Results.Ok(new { revoked = username });
    }

    // POST /admin/unrevoke/user/{username}
    internal IResult UnrevokeUser(HttpContext httpCtx, string username)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        ctx.RevocationStore?.UnrevokeUser(username);
        return Results.Ok(new { unrevoked = username });
    }

    // POST /admin/revoke/ip/{ip}
    internal IResult RevokeIp(HttpContext httpCtx, string ip)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(ip))
            return Results.BadRequest("IP required.");

        ctx.RevocationStore?.RevokeIp(ip);
        return Results.Ok(new { revokedIp = ip });
    }

    // POST /admin/unrevoke/ip/{ip}
    internal IResult UnrevokeIp(HttpContext httpCtx, string ip)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        ctx.RevocationStore?.UnrevokeIp(ip);
        return Results.Ok(new { unrevokedIp = ip });
    }

    // ── Config & Audit ─────────────────────────────────────────────────────────

    // GET /admin/config  — return resolved config as JSON (admin only, used by config export modal)
    internal IResult GetAdminConfig(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (ctx.ConfigJson.Length == 0)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        return Results.Content(ctx.ConfigJson, "application/json");
    }

    // GET /admin/audit  — last 200 audit log entries rendered as HTML (admin only)
    // Returns 404 when no audit log file is configured (or when writing to stdout).
    internal IResult GetAuditLog(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!ctx.HasAuditLog)
            return Results.NotFound();

        List<AuditEntry> entries;
        if (!File.Exists(ctx.AuditLogPath))
        {
            entries = [];
        }
        else
        {
            entries = File.ReadLines(ctx.AuditLogPath!)
                .TakeLast(200)
                .Select(TryParseAuditEntry)
                .OfType<AuditEntry>()
                .ToList();
        }

        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks("admin", ctx.PerSender, separateDir, showHome: true,
            hasInvites: ctx.InviteStore is not null, hasAuditLog: true, hasSessions: ctx.HasSessions,
            hasQr: ctx.AutoLoginStore is not null);
        return Results.Content(HtmlRenderer.RenderAuditLog(entries, navLinks), "text/html");
    }

    private static AuditEntry? TryParseAuditEntry(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return new AuditEntry(
                Timestamp:  root.TryGetProperty("timestamp",   out var ts) ? ts.GetString()  ?? "" : "",
                Username:   root.TryGetProperty("username",    out var un) ? un.GetString()       : null,
                RemoteIp:   root.TryGetProperty("remote_ip",  out var ip) ? ip.GetString()  ?? "" : "",
                Action:     root.TryGetProperty("action",     out var ac) ? ac.GetString()  ?? "" : "",
                Path:       root.TryGetProperty("path",       out var p)  ? p.GetString()   ?? "" : "",
                Bytes:      root.TryGetProperty("bytes",      out var b)  ? b.GetInt64()         : 0,
                StatusCode: root.TryGetProperty("status_code", out var sc) ? sc.GetInt32()        : 0
            );
        }
        catch { return null; }
    }

    // ── Sessions ───────────────────────────────────────────────────────────────

    // GET /admin/sessions  — active invite sessions dashboard (admin only)
    internal IResult GetAdminSessions(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var sessions = ctx.SessionRegistry is not null && ctx.InviteStore is not null
            ? ctx.SessionRegistry.GetActive(ctx.InviteStore)
            : Array.Empty<SessionInfo>();

        var bearers = ctx.AutoLoginStore?.GetActiveBearers() ?? Array.Empty<(string, BearerSession)>();

        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks("admin", ctx.PerSender, separateDir, showHome: true,
            hasInvites: ctx.InviteStore is not null, hasAuditLog: ctx.HasAuditLog, hasSessions: true,
            hasQr: ctx.AutoLoginStore is not null);
        return Results.Content(HtmlRenderer.RenderSessionsAdmin(sessions, navLinks, ctx.CsrfToken, bearers), "text/html");
    }

    // POST /admin/sessions/{id}/revoke  — revoke an invite and clear its sessions (admin only)
    internal IResult RevokeSession(HttpContext httpCtx, string id)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (ctx.InviteStore is null)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        if (!ctx.InviteStore.Revoke(id))
            return Results.NotFound($"Invite '{id}' not found.");

        ctx.SessionRegistry?.RemoveByInvite(id);
        return Results.Redirect("/admin/sessions");
    }

    // POST /admin/sessions/autologin/{prefix}/revoke  — revoke a QR session bearer (admin only)
    internal IResult RevokeAutoLoginBearer(HttpContext httpCtx, string prefix)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (ctx.AutoLoginStore is null)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        ctx.AutoLoginStore.RevokeBearer(prefix);
        return Results.Redirect("/admin/sessions");
    }

    // ── Invite management ──────────────────────────────────────────────────────

    // POST /admin/invites  — create a new invite token (admin only)
    // Body: JSON { friendlyName, role, expiresAt? }
    internal async Task<IResult> CreateInvite(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (ctx.InviteStore is null)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        InviteCreateRequest? req;
        try { req = await httpCtx.Request.ReadFromJsonAsync<InviteCreateRequest>(); }
        catch { return Results.BadRequest("Invalid JSON body."); }

        if (req is null || string.IsNullOrWhiteSpace(req.FriendlyName))
            return Results.BadRequest("friendlyName is required.");

        var validRoles = new[] { "admin", "rw", "ro", "wo" };
        var role = req.Role?.ToLowerInvariant() ?? "rw";
        if (!validRoles.Contains(role))
            return Results.BadRequest($"Invalid role '{role}'. Valid roles: {string.Join(", ", validRoles)}.");

        var creator = httpCtx.Items.TryGetValue("fb.user", out var u) && u is string s ? s : "admin";
        var token   = ctx.InviteStore.Create(req.FriendlyName.Trim(), role, req.ExpiresAt, creator,
            req.JoinMaxUses, req.BearerMaxUses);

        return Results.Json(TokenToDto(token), statusCode: StatusCodes.Status201Created);
    }

    // GET /admin/invites  — admin invite management page (HTML) or JSON list for API callers
    // Content negotiation: browsers receive a full HTML page; requests with Accept: application/json
    // receive the raw token array (used by the admin page's own JavaScript for mutations).
    internal IResult ListInvites(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // JSON branch — explicit API request
        var accept = httpCtx.Request.Headers.Accept.ToString();
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.InviteStore is null) return Results.StatusCode(StatusCodes.Status501NotImplemented);
            return Results.Json(ctx.InviteStore.GetAll().Select(TokenToDto).ToList());
        }

        // HTML branch — browser navigation
        if (ctx.InviteStore is null)
        {
            var msg = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Invites disabled</title></head>" +
                      "<body style=\"font-family:sans-serif;padding:2rem;background:#0f1117;color:#aaa\">" +
                      "<h2>Invites not enabled</h2><p>Start FileBeam with <code>--invites-file</code> to use invite tokens.</p>" +
                      "<p><a href=\"/\" style=\"color:#5ba4f5\">Home</a></p></body></html>";
            return Results.Content(msg, "text/html");
        }

        var baseUrl      = $"{httpCtx.Request.Scheme}://{httpCtx.Request.Host}";
        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks     = HtmlRenderer.BuildNavLinks("admin", ctx.PerSender, separateDir, showHome: true, hasInvites: true, hasAuditLog: ctx.HasAuditLog, hasSessions: ctx.HasSessions, hasQr: ctx.AutoLoginStore is not null);
        var html         = HtmlRenderer.RenderInvitesAdmin(ctx.InviteStore.GetAll(), ctx.CsrfToken, baseUrl, navLinks);
        return Results.Content(html, "text/html");
    }

    // DELETE /admin/invites/{id}  — revoke an invite token (admin only)
    internal IResult RevokeInvite(HttpContext httpCtx, string id)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (ctx.InviteStore is null)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        if (!ctx.InviteStore.Revoke(id))
            return Results.NotFound($"Invite '{id}' not found.");

        return Results.Ok(new { revoked = id });
    }

    // PATCH /admin/invites/{id}  — edit an invite's name/role/expiry (admin only)
    // Body: JSON { friendlyName?, role?, expiresAt?, clearExpiry? }
    internal async Task<IResult> EditInvite(HttpContext httpCtx, string id)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (ctx.InviteStore is null)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        InviteEditRequest? req;
        try { req = await httpCtx.Request.ReadFromJsonAsync<InviteEditRequest>(); }
        catch { return Results.BadRequest("Invalid JSON body."); }

        if (req is null)
            return Results.BadRequest("Request body required.");

        var validRoles = new[] { "admin", "rw", "ro", "wo" };
        if (req.Role is not null && !validRoles.Contains(req.Role.ToLowerInvariant()))
            return Results.BadRequest($"Invalid role '{req.Role}'.");

        if (!ctx.InviteStore.Edit(id,
                friendlyName:         req.FriendlyName?.Trim(),
                role:                 req.Role?.ToLowerInvariant(),
                expiresAt:            req.ExpiresAt,
                clearExpiry:          req.ClearExpiry,
                joinMaxUses:          req.JoinMaxUses,
                bearerMaxUses:        req.BearerMaxUses,
                clearJoinMaxUses:     req.ClearJoinMaxUses,
                clearBearerMaxUses:   req.ClearBearerMaxUses))
            return Results.NotFound($"Invite '{id}' not found.");

        ctx.InviteStore.TryGet(id, out var updated);
        return Results.Json(updated is null ? null : TokenToDto(updated));
    }

    // GET /join/{token}  — redeem an invite, set session cookie, redirect to /
    // This endpoint is exempt from auth middleware so unauthenticated users can reach it.
    internal IResult JoinWithInvite(HttpContext httpCtx, string token)
    {
        if (ctx.InviteStore is null)
            return Results.Content(JoinErrorHtml("Invites are not enabled on this server."), "text/html");

        // If the request already carries a valid session for this exact invite, skip TryRedeem
        // so that refreshing the /join page doesn't burn an additional use against the quota.
        InviteToken? invite = null;
        if (ctx.SessionKey is not null && httpCtx.Request.Cookies.TryGetValue("fb.session", out var existingCookie))
        {
            var dot = existingCookie.LastIndexOf('.');
            if (dot > 0)
            {
                try
                {
                    var payloadBytes   = Base64UrlDecode(existingCookie[..dot]);
                    using var doc      = JsonDocument.Parse(payloadBytes);
                    var existingTokenId = doc.RootElement.GetProperty("tokenId").GetString();
                    if (existingTokenId == token &&
                        TryValidateSessionCookie(existingCookie, ctx.SessionKey, ctx.InviteStore, out _, out _, out _))
                    {
                        ctx.InviteStore.TryGet(token, out invite);
                    }
                }
                catch { /* fall through to TryRedeem */ }
            }
        }

        invite ??= ctx.InviteStore.TryRedeem(token);
        if (invite is null)
            return Results.Content(
                JoinErrorHtml("This invite link is invalid, expired, or has been revoked."),
                "text/html",
                statusCode: StatusCodes.Status404NotFound);

        // Build signed session cookie: Base64Url(payload) + "." + Base64Url(HMAC-SHA256(key, payload))
        if (ctx.SessionKey is not null)
        {
            var payloadObj = new
            {
                tokenId    = invite.Id,
                role       = invite.Role,
                issuedAt   = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                expiresAt  = invite.ExpiresAt?.ToUnixTimeSeconds()
            };
            var payloadJson  = JsonSerializer.Serialize(payloadObj);
            var payloadB64   = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
            var sig          = HMACSHA256.HashData(ctx.SessionKey, Encoding.UTF8.GetBytes(payloadB64));
            var cookieValue  = payloadB64 + "." + Base64UrlEncode(sig);

            var cookieOpts = new CookieOptions
            {
                HttpOnly  = true,
                SameSite  = SameSiteMode.Lax,
                Secure    = ctx.IsTls,
                Path      = "/",
            };
            if (invite.ExpiresAt.HasValue)
                cookieOpts.Expires = invite.ExpiresAt.Value;

            httpCtx.Response.Cookies.Append("fb.session", cookieValue, cookieOpts);
        }

        var serverUrl = $"{httpCtx.Request.Scheme}://{httpCtx.Request.Host}";
        return Results.Content(JoinSuccessHtml(invite.FriendlyName, invite.Role, invite.Id, serverUrl), "text/html");
    }

    // ── Auto-login ─────────────────────────────────────────────────────────────

    // GET /auto-login/{token}  — redeem a startup QR auto-login token, redirect with continuation token
    // This endpoint is exempt from auth middleware (handled in Program.cs bypass).
    // The session cookie is NOT set here. A short-lived single-use continuation token is embedded
    // in the redirect URL (?_fbs=…). The auth middleware sets the cookie when the browser lands on
    // GET /, ensuring the cookie is in the response the browser keeps — not an intermediate redirect
    // that mobile browsers and QR-scanner webviews don't reliably forward cookies through.
    internal IResult RedeemAutoLogin(HttpContext httpCtx, string token)
    {
        var store = ctx.AutoLoginStore;
        var ip    = httpCtx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (store is null || !store.TryRedeem(token))
        {
            ctx.AuditLogger?.Log(DateTimeOffset.UtcNow.ToString("o"), null, ip, "qr-redeem-fail", $"/auto-login/{token[..Math.Min(8, token.Length)]}…", 0, 401);
            return Results.Content(HtmlRenderer.RenderAutoLoginError(), "text/html");
        }

        if (ctx.SessionKey is null)
        {
            ctx.AuditLogger?.Log(DateTimeOffset.UtcNow.ToString("o"), null, ip, "qr-redeem-fail", "/auto-login/…", 0, 500);
            return Results.Content(HtmlRenderer.RenderAutoLoginError(), "text/html");
        }

        ctx.AuditLogger?.Log(DateTimeOffset.UtcNow.ToString("o"), "admin", ip, "qr-redeem", "/auto-login/…", 0, 302);
        var contToken = store.GenerateContinuationToken();
        return Results.Redirect($"/?_fbs={contToken}");
    }

    // GET /admin/qr  — regenerate a fresh admin auto-login QR (admin only)
    internal IResult GetAdminQr(HttpContext httpCtx)
    {
        if (HandlerContext.GetRole(httpCtx) != "admin")
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (ctx.AutoLoginStore is null)
            return Results.Content("<p>Auto-login is disabled (--no-qr-autologin).</p>", "text/html");

        var scheme = ctx.IsTls ? "https" : "http";
        var host   = httpCtx.Request.Host.ToString();
        var token  = ctx.AutoLoginStore.Generate();
        var url    = $"{scheme}://{host}/auto-login/{token.Token}";

        var ip = httpCtx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var user = httpCtx.Items.TryGetValue("fb.user", out var u) && u is string s ? s : "admin";
        ctx.AuditLogger?.Log(DateTimeOffset.UtcNow.ToString("o"), user, ip, "qr-generate", "/admin/qr", 0, 200);

        using var qrGen = new QRCodeGenerator();
        var qrData  = qrGen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
        var pngQr   = new PngByteQRCode(qrData);
        var base64  = Convert.ToBase64String(pngQr.GetGraphic(20));

        bool separateDir = !ctx.RootDir.Equals(ctx.UploadDir, StringComparison.OrdinalIgnoreCase);
        var navLinks = HtmlRenderer.BuildNavLinks("admin", ctx.PerSender, separateDir, showHome: true,
            hasInvites: ctx.InviteStore is not null, hasAuditLog: ctx.HasAuditLog,
            hasSessions: ctx.HasSessions, hasQr: true);
        return Results.Content(HtmlRenderer.RenderAdminQr(base64, token.ExpiresAt, navLinks), "text/html");
    }

    // ── HTML Login page (unauthenticated) ──────────────────────────────────────

    // GET /login  — render login form (exempt from auth middleware)
    internal IResult GetLoginPage(HttpContext httpCtx)
    {
        var next = httpCtx.Request.Query.TryGetValue("next", out var n) ? n.ToString() : null;
        return Results.Content(HtmlRenderer.RenderLoginPage(ctx.CsrfToken, error: null, next: next), "text/html");
    }

    // POST /login  — validate credentials and set admin session cookie (exempt from auth middleware)
    internal async Task<IResult> PostLogin(HttpContext httpCtx)
    {
        var ip = httpCtx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Brute-force lockout check
        if (ctx.IsLockedOut?.Invoke(ip) == true)
        {
            await Task.Delay(200);
            return Results.Content(HtmlRenderer.RenderLoginPage(ctx.CsrfToken,
                error: "Too many failed attempts. Please wait and try again.", next: null), "text/html",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        IFormCollection form;
        try { form = await httpCtx.Request.ReadFormAsync(); }
        catch { return Results.BadRequest("Invalid form body."); }

        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        var next     = form["next"].ToString();

        // Validate against admin credentials using constant-time comparison
        var usernameMatch = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(username),
            System.Text.Encoding.UTF8.GetBytes(ctx.AdminUsername));
        var passwordMatch = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(password),
            System.Text.Encoding.UTF8.GetBytes(ctx.AdminPassword));

        if (!usernameMatch || !passwordMatch)
        {
            await Task.Delay(200);
            ctx.RecordAuth?.Invoke(ip, false);
            ctx.AuditLogger?.Log(DateTimeOffset.UtcNow.ToString("o"), username, ip, "admin-login-fail", "/login", 0, 401);
            return Results.Content(
                HtmlRenderer.RenderLoginPage(ctx.CsrfToken, error: "Invalid username or password.", next: next),
                "text/html", statusCode: StatusCodes.Status401Unauthorized);
        }

        ctx.RecordAuth?.Invoke(ip, true);
        ctx.AuditLogger?.Log(DateTimeOffset.UtcNow.ToString("o"), username, ip, "admin-login", "/login", 0, 302);

        // Issue admin session cookie (same format as QR auto-login: no tokenId, role=admin)
        if (ctx.SessionKey is not null)
        {
            static string B64Url(byte[] d) =>
                Convert.ToBase64String(d).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var pl = B64Url(System.Text.Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    role     = "admin",
                    issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                })));
            var sig = HMACSHA256.HashData(ctx.SessionKey, System.Text.Encoding.UTF8.GetBytes(pl));
            httpCtx.Response.Cookies.Append("fb.session", pl + "." + B64Url(sig),
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax,
                    Secure = ctx.IsTls, Path = "/" });
        }

        // Validate and sanitize the `next` redirect target (must start with / and not contain //)
        var redirectTarget = !string.IsNullOrEmpty(next) && next.StartsWith('/') && !next.Contains("//")
            ? next : "/";
        return Results.Redirect(redirectTarget);
    }

    // ── SSE live reload ────────────────────────────────────────────────────────

    // GET /events  — Server-Sent Events stream for live reload
    internal async Task FileEvents(HttpContext httpCtx)
    {
        var ch = ctx.Watcher.TrySubscribe();
        if (ch is null)
        {
            // SSE connection cap reached — reject with 503
            httpCtx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        httpCtx.Response.Headers["Content-Type"]      = "text/event-stream";
        httpCtx.Response.Headers["Cache-Control"]     = "no-cache";
        httpCtx.Response.Headers["X-Accel-Buffering"] = "no";
        await httpCtx.Response.Body.FlushAsync();

        var lifetime = httpCtx.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            httpCtx.RequestAborted, lifetime.ApplicationStopping);

        try
        {
            await foreach (var msg in ch.Reader.ReadAllAsync(cts.Token))
            {
                await httpCtx.Response.WriteAsync($"data: {msg}\n\n");
                await httpCtx.Response.Body.FlushAsync();
            }
        }
        catch (OperationCanceledException) { /* client disconnected — normal */ }
        finally
        {
            ctx.Watcher.Unsubscribe(ch);
        }
    }

    // ── Invite helpers ─────────────────────────────────────────────────────────

    private sealed record InviteCreateRequest(
        string          FriendlyName,
        string?         Role,
        DateTimeOffset? ExpiresAt,
        int?            JoinMaxUses   = null,
        int?            BearerMaxUses = null);

    private sealed record InviteEditRequest(
        string?         FriendlyName,
        string?         Role,
        DateTimeOffset? ExpiresAt,
        bool            ClearExpiry        = false,
        int?            JoinMaxUses        = null,
        int?            BearerMaxUses      = null,
        bool            ClearJoinMaxUses   = false,
        bool            ClearBearerMaxUses = false);

    private static object TokenToDto(InviteToken t) => new
    {
        id             = t.Id,
        friendlyName   = t.FriendlyName,
        role           = t.Role,
        expiresAt      = t.ExpiresAt?.ToString("o"),
        useCount       = t.UseCount,
        joinMaxUses    = t.JoinMaxUses,
        bearerUseCount = t.BearerUseCount,
        bearerMaxUses  = t.BearerMaxUses,
        createdBy      = t.CreatedBy,
        isActive       = t.IsActive,
        createdAt      = t.CreatedAt.ToString("o"),
    };

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Validates a signed <c>fb.session</c> cookie value and returns the authenticated role
    /// and user identity if valid.
    /// </summary>
    /// <remarks>
    /// Cookie format: <c>Base64Url(payloadJson) + "." + Base64Url(HMAC-SHA256(key, payloadB64))</c>
    /// Validation rejects cookies with bad signatures, expired sessions, or revoked/inactive invite tokens.
    /// When <paramref name="inviteStore"/> is provided the live token state is always checked, so
    /// revoking an invite immediately invalidates all issued cookies.
    /// </remarks>
    public static bool TryValidateSessionCookie(
        string       cookieValue,
        byte[]       sessionKey,
        InviteStore? inviteStore,
        out string?  role,
        out string?  user,
        out string?  inviteId)
    {
        role     = null;
        user     = null;
        inviteId = null;

        var dot = cookieValue.LastIndexOf('.');
        if (dot < 0) return false;

        var payloadB64 = cookieValue[..dot];
        var sigB64     = cookieValue[(dot + 1)..];

        byte[] expectedSig;
        try   { expectedSig = Base64UrlDecode(sigB64); }
        catch { return false; }

        var actualSig = HMACSHA256.HashData(sessionKey, Encoding.UTF8.GetBytes(payloadB64));
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig)) return false;

        byte[] payloadBytes;
        try   { payloadBytes = Base64UrlDecode(payloadB64); }
        catch { return false; }

        try
        {
            using var doc  = JsonDocument.Parse(payloadBytes);
            var root       = doc.RootElement;

            // Admin session cookie (from /auto-login) — no invite tokenId required
            if (!root.TryGetProperty("tokenId", out var tidProp) ||
                tidProp.ValueKind == JsonValueKind.Null)
            {
                var adminRole = root.TryGetProperty("role", out var rp) ? rp.GetString() : null;
                role     = adminRole;
                user     = "admin";
                inviteId = null;
                return role == "admin";
            }

            var tokenId    = root.GetProperty("tokenId").GetString();
            var cookieRole = root.GetProperty("role").GetString();

            if (string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(cookieRole)) return false;

            // Check cookie-level expiry (fast path — avoids a store lookup when clearly stale)
            if (root.TryGetProperty("expiresAt", out var expEl) && expEl.ValueKind != JsonValueKind.Null)
            {
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expEl.GetInt64()) return false;
            }

            // Validate live token state (catches revocations after the cookie was issued)
            if (inviteStore is not null)
            {
                if (!inviteStore.TryGet(tokenId, out var invite)) return false;
                if (!invite!.IsActive) return false;
                if (invite.ExpiresAt.HasValue && invite.ExpiresAt.Value < DateTimeOffset.UtcNow) return false;

                role     = invite.Role;           // use live role — may have been edited since issue
                user     = $"invite:{invite.Id}";
                inviteId = invite.Id;
            }
            else
            {
                role = cookieRole;
                user = "invite:?";
            }

            return true;
        }
        catch { return false; }
    }

    private static string JoinSuccessHtml(string friendlyName, string role, string inviteId, string serverUrl)
    {
        var encodedName   = HttpUtility.HtmlEncode(friendlyName);
        var encodedRole   = HttpUtility.HtmlEncode(role);
        var encodedId     = HttpUtility.HtmlEncode(inviteId);
        var encodedServer = HttpUtility.HtmlEncode(serverUrl);
        return
            "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>Invite Accepted \u2014 FileBeam</title>" +
            "<style>body{font-family:sans-serif;max-width:520px;margin:4rem auto;padding:0 1rem}" +
            ".card{background:#f0f9f0;border:1px solid #6c6;border-radius:8px;padding:1.5rem}" +
            "h2{margin-top:0}.role{font-weight:bold;color:#252}" +
            ".api-box{margin-top:1.2rem;background:#f5f5f5;border:1px solid #ddd;border-radius:6px;padding:.9rem 1rem}" +
            ".api-box h4{margin:0 0 .5rem;font-size:.9rem;color:#333}" +
            ".key-row{display:flex;align-items:center;gap:.5rem;margin-bottom:.6rem}" +
            ".key-row code{flex:1;background:#fff;border:1px solid #ccc;border-radius:4px;padding:.3rem .5rem;font-size:.82rem;overflow:auto;white-space:nowrap}" +
            ".copy-btn{padding:.25rem .6rem;font-size:.8rem;cursor:pointer;border:1px solid #aaa;border-radius:4px;background:#fff}" +
            ".copy-btn:active{background:#e0ffe0}" +
            "pre{margin:.4rem 0 0;background:#fff;border:1px solid #ccc;border-radius:4px;padding:.5rem;font-size:.78rem;overflow:auto;white-space:pre}" +
            ".note{margin-top:1.5rem;font-size:.85rem;color:#555;border-top:1px solid #ddd;padding-top:1rem}" +
            ".btn{display:inline-block;margin-top:1.2rem;padding:.55rem 1.4rem;background:#22c55e;" +
            "color:#fff;border-radius:6px;text-decoration:none;font-weight:bold}</style>" +
            "</head><body>" +
            "<h2>FileBeam</h2>" +
            "<div class=\"card\">" +
            "<h3>\u2713 Invite accepted</h3>" +
            $"<p>Welcome, <span class=\"role\">{encodedName}</span>! " +
            $"You have been granted <span class=\"role\">{encodedRole}</span> access.</p>" +
            "<a class=\"btn\" href=\"/\">Continue to FileBeam</a>" +
            "<div class=\"api-box\">" +
            "<h4>API Key (for curl / CLI)</h4>" +
            "<div class=\"key-row\">" +
            $"<code id=\"api-key-val\">{encodedId}</code>" +
            "<button class=\"copy-btn\" onclick=\"navigator.clipboard.writeText(document.getElementById('api-key-val').textContent).then(()=>{this.textContent='Copied!';setTimeout(()=>this.textContent='Copy',1500)})\">Copy</button>" +
            "</div>" +
            $"<pre>curl -H \"X-API-Key: {encodedId}\" {encodedServer}/download/</pre>" +
            "<p style=\"font-size:.78rem;color:#666;margin:.5rem 0 0\">Use <code>X-API-Key: &lt;key&gt;</code> to authenticate curl and CLI requests without a browser session. No CSRF token required.</p>" +
            "</div>" +
            "</div>" +
            "</body></html>";
    }

    private static string JoinErrorHtml(string message) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
        "<title>Invite Error \u2014 FileBeam</title>" +
        "<style>body{font-family:sans-serif;max-width:480px;margin:4rem auto;padding:0 1rem}" +
        ".err{background:#fee;border:1px solid #faa;border-radius:6px;padding:1rem 1.5rem;color:#900}</style>" +
        "</head><body>" +
        "<h2>FileBeam Invite</h2>" +
        $"<div class=\"err\">{HttpUtility.HtmlEncode(message)}</div>" +
        "<p><a href=\"/\">Go to FileBeam</a></p>" +
        "</body></html>";
}
