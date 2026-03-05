using Microsoft.AspNetCore.Http;

namespace FileBeam;

// NOTE: The RouteHandlers class has been split into domain-focused classes:
//   FileWatcher.cs       — SSE / FileSystemWatcher
//   HandlerContext.cs    — Shared state and cross-cutting helpers
//   BrowseHandlers.cs    — Directory listing endpoints
//   DownloadHandlers.cs  — File/ZIP download and /info endpoints
//   UploadHandlers.cs    — Upload endpoints, quota tracking, disk-space
//   ModifyHandlers.cs    — Delete, rename, mkdir endpoints
//   AdminHandlers.cs     — Share tokens, invites, sessions, revocation, config, audit, SSE
//
// This facade exists for backward compatibility with Program.cs and the test suite,
// and delegates every method to the appropriate domain handler.

/// <summary>
/// Thin facade over the domain-focused handler classes.
/// Constructs a single <see cref="HandlerContext"/> shared by all domain handlers,
/// then delegates every public method to the appropriate handler.
/// </summary>
public class RouteHandlers
{
    private readonly BrowseHandlers   _browse;
    private readonly DownloadHandlers _download;
    private readonly UploadHandlers   _upload;
    private readonly ModifyHandlers   _modify;
    private readonly AdminHandlers    _admin;

    public RouteHandlers(
        string rootDir,
        string uploadDir,
        FileWatcher watcher,
        bool isReadOnly = false,
        bool perSender = false,
        long maxFileSize = 0,
        long maxUploadBytesPerSender = 0,
        long maxUploadBytesTotal = 0,
        int maxDirDepth = 10,
        int maxFilesPerDir = 1000,
        string csrfToken = "",
        int shareTtlSeconds = 3600,
        RevocationStore? revocationStore = null,
        InviteStore? inviteStore = null,
        byte[]? sessionKey = null,
        bool isTls = false,
        Action<string, string>? debugLog = null,
        string configJson = "",
        string cliCommand = "",
        string? auditLogPath = null,
        TimeSpan? uploadTtl = null,
        string? adminExemptPath = null,
        SessionRegistry? sessionRegistry = null,
        int maxConcurrentZips = 2,
        long maxZipBytes = 0,
        AutoLoginStore? autoLoginStore = null,
        AuditLogger? auditLogger = null,
        string adminUsername = "admin",
        string adminPassword = "",
        Func<string, bool>? isLockedOut = null,
        Action<string, bool>? recordAuth = null)
    {
        var ctx = new HandlerContext(
            rootDir, uploadDir, watcher, isReadOnly, perSender,
            maxFileSize, maxUploadBytesPerSender, maxUploadBytesTotal,
            maxDirDepth, maxFilesPerDir, csrfToken, shareTtlSeconds,
            revocationStore, inviteStore, sessionKey, isTls, debugLog,
            configJson, cliCommand, auditLogPath, uploadTtl,
            adminExemptPath, sessionRegistry,
            maxConcurrentZips, maxZipBytes, autoLoginStore,
            auditLogger, adminUsername, adminPassword, isLockedOut, recordAuth);

        _browse   = new BrowseHandlers(ctx);
        _download = new DownloadHandlers(ctx);
        _upload   = new UploadHandlers(ctx);
        _modify   = new ModifyHandlers(ctx);
        _admin    = new AdminHandlers(ctx);
    }

    // ── Browse ─────────────────────────────────────────────────────────────────
    public IResult ListDirectory(HttpContext ctx)                               => _browse.ListDirectory(ctx);
    public IResult BrowseDirectory(HttpContext ctx, string? subpath)           => _browse.BrowseDirectory(ctx, subpath);
    public IResult BrowseUploadArea(HttpContext ctx, string? subpath = null)   => _browse.BrowseUploadArea(ctx, subpath);
    public IResult BrowseMyUploads(HttpContext ctx, string? subpath = null)    => _browse.BrowseMyUploads(ctx, subpath);
    public IResult BrowseAdminUploads(HttpContext ctx, string? subpath = null) => _browse.BrowseAdminUploads(ctx, subpath);

    // ── Download ───────────────────────────────────────────────────────────────
    public IResult DownloadFile(HttpContext ctx, string? subpath)             => _download.DownloadFile(ctx, subpath);
    public IResult DownloadZip(HttpContext ctx, string? subpath)              => _download.DownloadZip(ctx, subpath);
    public IResult DownloadUploadAreaFile(HttpContext ctx, string? subpath)   => _download.DownloadUploadAreaFile(ctx, subpath);
    public IResult DownloadUploadAreaZip(HttpContext ctx, string? subpath)    => _download.DownloadUploadAreaZip(ctx, subpath);
    public IResult DownloadMyUpload(HttpContext ctx, string? subpath)         => _download.DownloadMyUpload(ctx, subpath);
    public IResult DownloadMyUploadsZip(HttpContext ctx, string? subpath)     => _download.DownloadMyUploadsZip(ctx, subpath);
    public IResult DownloadAdminUpload(HttpContext ctx, string? subpath)      => _download.DownloadAdminUpload(ctx, subpath);
    public Task<IResult> InfoFile(HttpContext ctx, string? subpath)           => _download.InfoFile(ctx, subpath);
    public Task<IResult> InfoMyUpload(HttpContext ctx, string? subpath)       => _download.InfoMyUpload(ctx, subpath);

    // ── Upload ─────────────────────────────────────────────────────────────────
    public Task<IResult> UploadFiles(HttpContext ctx, string? subpath)        => _upload.UploadFiles(ctx, subpath);
    public Task<IResult> UploadToUploadArea(HttpContext ctx, string? subpath) => _upload.UploadToUploadArea(ctx, subpath);
    public Task<IResult> UploadToMyUploads(HttpContext ctx, string? subpath)  => _upload.UploadToMyUploads(ctx, subpath);
    public IResult DiskSpace(HttpContext ctx)                                  => _upload.DiskSpace(ctx);

    // ── Modify ─────────────────────────────────────────────────────────────────
    public IResult DeleteFile(HttpContext ctx, string? subpath)                   => _modify.DeleteFile(ctx, subpath);
    public IResult DeleteDir(HttpContext ctx, string? subpath)                    => _modify.DeleteDir(ctx, subpath);
    public Task<IResult> RenameFile(HttpContext ctx, string? subpath)             => _modify.RenameFile(ctx, subpath);
    public Task<IResult> RenameDir(HttpContext ctx, string? subpath)              => _modify.RenameDir(ctx, subpath);
    public IResult MkDir(HttpContext ctx, string? subpath)                        => _modify.MkDir(ctx, subpath);
    public IResult DeleteMyUpload(HttpContext ctx, string? subpath)               => _modify.DeleteMyUpload(ctx, subpath);
    public Task<IResult> RenameMyUpload(HttpContext ctx, string? subpath)         => _modify.RenameMyUpload(ctx, subpath);
    public Task<IResult> RenameMyUploadDir(HttpContext ctx, string? subpath)      => _modify.RenameMyUploadDir(ctx, subpath);
    public IResult DeleteAdminUpload(HttpContext ctx, string? subpath)            => _modify.DeleteAdminUpload(ctx, subpath);
    public Task<IResult> RenameAdminUpload(HttpContext ctx, string? subpath)      => _modify.RenameAdminUpload(ctx, subpath);
    public Task<IResult> RenameAdminUploadDir(HttpContext ctx, string? subpath)   => _modify.RenameAdminUploadDir(ctx, subpath);

    // ── Admin ──────────────────────────────────────────────────────────────────
    public IResult CreateShareLink(HttpContext ctx, string? subpath)   => _admin.CreateShareLink(ctx, subpath);
    public IResult RedeemShareLink(string? token)                      => _admin.RedeemShareLink(token);
    public IResult ListShareTokens(HttpContext ctx)                     => _admin.ListShareTokens(ctx);
    public IResult ListRevocations(HttpContext ctx)                     => _admin.ListRevocations(ctx);
    public IResult RevokeUser(HttpContext ctx, string username)         => _admin.RevokeUser(ctx, username);
    public IResult UnrevokeUser(HttpContext ctx, string username)       => _admin.UnrevokeUser(ctx, username);
    public IResult RevokeIp(HttpContext ctx, string ip)                 => _admin.RevokeIp(ctx, ip);
    public IResult UnrevokeIp(HttpContext ctx, string ip)               => _admin.UnrevokeIp(ctx, ip);
    public IResult GetAdminConfig(HttpContext ctx)                      => _admin.GetAdminConfig(ctx);
    public IResult GetAuditLog(HttpContext ctx)                         => _admin.GetAuditLog(ctx);
    public IResult GetAdminSessions(HttpContext ctx)                    => _admin.GetAdminSessions(ctx);
    public IResult RevokeSession(HttpContext ctx, string id)            => _admin.RevokeSession(ctx, id);
    public Task<IResult> CreateInvite(HttpContext ctx)                  => _admin.CreateInvite(ctx);
    public IResult ListInvites(HttpContext ctx)                         => _admin.ListInvites(ctx);
    public IResult RevokeInvite(HttpContext ctx, string id)             => _admin.RevokeInvite(ctx, id);
    public Task<IResult> EditInvite(HttpContext ctx, string id)         => _admin.EditInvite(ctx, id);
    public IResult JoinWithInvite(HttpContext ctx, string token)        => _admin.JoinWithInvite(ctx, token);
    public Task FileEvents(HttpContext ctx)                             => _admin.FileEvents(ctx);
    public IResult RedeemAutoLogin(HttpContext ctx, string token)      => _admin.RedeemAutoLogin(ctx, token);
    public IResult GetAdminQr(HttpContext ctx)                         => _admin.GetAdminQr(ctx);
    public IResult GetLoginPage(HttpContext ctx)                       => _admin.GetLoginPage(ctx);
    public Task<IResult> PostLogin(HttpContext ctx)                    => _admin.PostLogin(ctx);
    public IResult RevokeAutoLoginBearer(HttpContext ctx, string prefix) => _admin.RevokeAutoLoginBearer(ctx, prefix);

    // ── Static helpers (kept for backward compatibility with Program.cs) ───────
    /// <inheritdoc cref="AdminHandlers.TryValidateSessionCookie"/>
    public static bool TryValidateSessionCookie(
        string       cookieValue,
        byte[]       sessionKey,
        InviteStore? inviteStore,
        out string?  role,
        out string?  user,
        out string?  inviteId)
        => AdminHandlers.TryValidateSessionCookie(cookieValue, sessionKey, inviteStore, out role, out user, out inviteId);
}
