# CLAUDE.md — FileBeam Developer & Contributor Guide

> C# / .NET 10 LAN file-sharing server. Single self-contained executable built on ASP.NET Core Kestrel.

## Commands

| Task | Command |
|---|---|
| Run (debug) | `dotnet run --project src/` |
| Build | `dotnet build` |
| Test (all) | `dotnet test` |
| Test (single) | `dotnet test --filter "FullyQualifiedName~ClassName.MethodName"` |
| Publish (Windows) | `dotnet publish src/ -p:PublishProfile=win-x64` |
| Publish (Linux) | `dotnet publish src/ -p:PublishProfile=linux-x64` |
| Publish (macOS) | `dotnet publish src/ -p:PublishProfile=osx-arm64` |
| Docker build | `docker compose build` |
| Docker run | `docker compose up -d` |

## Architecture

Monolithic single-process design — no microservices, no database, no external dependencies beyond .NET.

### Entry Point

`Program.cs` uses top-level statements. Startup sequence:

1. Parse CLI args (20+ flags)
2. Load `filebeam.json` config (auto-discovered in CWD or via `--config`)
3. Interactive prompts (TTY detection) for download dir, upload dir, port
4. Resolve admin password (env var → CLI flag → key file → auto-generate)
5. Validate TLS cert/key if provided
6. Build Kestrel app, register middleware, map routes
7. Run

### Middleware Pipeline Order (critical for modifications)

Middleware is registered in this exact order — ordering matters:

1. **Request logging** — Stopwatch timing, request ID, console + audit log output
2. **Rate limiter** — Fixed-window per-IP (`--rate-limit`, default 60 req/min)
3. **Auth** — IP revocation → brute-force lockout → Basic Auth → Bearer token → session cookie
4. **CSRF validation** — Checks `X-CSRF-Token` header or `_csrf` form field on POST/DELETE/PATCH/PUT

Route handlers are mapped after all middleware.

### Authentication (three methods, checked in order)

| Method | Mechanism | Role |
|---|---|---|
| Basic Auth | `Authorization: Basic …` with admin username + password | `admin` |
| Bearer Token | `Authorization: Bearer <invite-token-id>` | invite role (`rw`, `ro`, `wo`) |
| Session Cookie | `fb.session` HMAC-SHA256 signed cookie (set on `/join/{token}`) | invite role |

Unauthenticated access is allowed only for `/s/{token}` (share link) and `/join/{token}` (invite join).

### Config Resolution (highest precedence first)

1. CLI flags
2. `filebeam.json` config file
3. Environment variables (passwords only: `FILEBEAM_ADMIN_PASSWORD`)
4. Hardcoded defaults (port 8080, 100 GB body limit, CWD as download dir)

### Embedded Resources

`wwwroot/index.html` and `wwwroot/app.js` are embedded into the assembly via `<EmbeddedResource>` in the `.csproj`. At runtime, loaded via `Assembly.GetManifestResourceStream()` with resource name `FileBeam.wwwroot.<filename>`.

### Live Reload (SSE)

`FileWatcher` monitors the download directory via `FileSystemWatcher`. Clients connect to `/events` for Server-Sent Events. SSE connections are capped at 50 by default.

## Source File Map

### Main Project (`src/`)

| File | Role |
|---|---|
| `Program.cs` | Entry point: CLI parsing, config, middleware pipeline, route mapping |
| `RouteHandlers.cs` | All HTTP endpoint handlers (browse, download, upload, delete, rename, share, admin, SSE) |
| `FileBeamConfig.cs` | Config file (`filebeam.json`) parsing, validation, JSON/CLI export |
| `AdminAuth.cs` | Basic Auth, Bearer token auth, password resolution helpers |
| `InviteStore.cs` | In-memory invite token store with optional JSON file persistence |
| `RevocationStore.cs` | Thread-safe IP and user revocation (ConcurrentDictionary) |
| `AuditLogger.cs` | Non-blocking audit logger via async Channel with file rotation |
| `HtmlRenderer.cs` | HTML template generation for all pages (role-aware UI) |
| `MimeTypes.cs` | File extension → MIME type mapping |
| `ResourceLoader.cs` | Embedded resource loader for wwwroot files |
| `wwwroot/index.html` | Base HTML template with CSS (embedded resource) |
| `wwwroot/app.js` | Client-side JavaScript: uploads, SSE, UI interactions |

### Test Project (`tests/FileBeam.Tests/`)

| File | Covers |
|---|---|
| `FileBeamConfigTests.cs` | Config loading, CLI command generation |
| `AdminBearerAuthTests.cs` | Basic Auth, Bearer token auth, password resolution |
| `CookieSessionTests.cs` | Session cookie HMAC validation |
| `InviteStoreTests.cs` | Invite creation, revocation, expiry |
| `RevocationStoreTests.cs` | IP/user revocation |
| `RouteHandlersTests.cs` | Route handlers: path resolution, role checks, upload limits |
| `HtmlRendererTests.cs` | HTML output, role-based UI visibility |
| `MimeTypesTests.cs` | MIME type mapping |

## Code Conventions

- **.NET 10** with C# 13 — nullable reference types enabled, implicit usings enabled
- **File-scoped namespaces** (`namespace FileBeam;`)
- **Top-level statements** in `Program.cs` (no `Main` method)
- **Records** for data types (`InviteToken` record)
- **Primary constructors** where applicable
- **XML doc comments** (`<summary>`, `<param>`) on public/internal APIs
- No linter or formatter configured — follow existing style

### Naming

| Element | Convention | Example |
|---|---|---|
| Classes, methods, properties | PascalCase | `RouteHandlers`, `UploadFiles` |
| Local variables, parameters | camelCase | `serveDir`, `maxFileSize` |
| Private fields | `_camelCase` | `_tokens`, `_filePath` |
| Constants | PascalCase | `MaxFailures`, `LockoutSeconds` |

### Dependencies

| Package | Purpose |
|---|---|
| `QRCoder` | QR code generation for LAN URL display |
| `Spectre.Console` | Terminal UI (panels, markup, prompts) |
| `xunit` | Test framework (test project only) |

## Security Patterns

**These rules MUST be followed in all code changes.**

### Constant-Time Comparisons
All password and secret comparisons MUST use `CryptographicOperations.FixedTimeEquals()`. Never use `==` or `string.Equals()` for secrets. See `AdminAuth.TryAdminBasicAuth()` for the reference implementation.

### Path Traversal Protection
All user-supplied paths MUST be resolved via `Path.GetFullPath()` and validated with `StartsWith(rootDir)` to ensure they stay within the allowed directory. Filenames from uploads MUST be stripped of directory components. See `RouteHandlers.SafeResolvePath()`.

### CSRF Tokens
All state-changing requests (POST, DELETE, PATCH, PUT) MUST include a valid CSRF token — either via `X-CSRF-Token` header or `_csrf` form field. The token is a 32-byte cryptographically random Base64 string generated once per process.

### HMAC-SHA256 Session Cookies
Session cookies (`fb.session`) MUST be signed with HMAC-SHA256 using the per-process session key. Cookies are HttpOnly, SameSite=Lax. The session key is regenerated on every process start (not persistent).

### Brute-Force Lockout
Failed auth attempts incur a 200ms delay. After 10 failures from the same IP, the IP is locked out for 60 seconds. The tracking dictionary is trimmed at 10,000 entries to prevent memory exhaustion.

### Cryptographic Randomness
All tokens, passwords, and CSRF values MUST be generated via `RandomNumberGenerator`. Never use `System.Random` for security-sensitive values.

### Input Sanitization
HTML output MUST use `HttpUtility.HtmlEncode()` for user-supplied strings. Console output MUST use `Markup.Escape()` (Spectre.Console). URLs MUST use `Uri.EscapeDataString()`.

## Testing

- **Framework**: xUnit with `[Fact]` attributes
- **Naming**: `Method_Scenario_Expected` (e.g., `AdminBasicAuth_CorrectCredentials_ReturnsTrue`)
- **HTTP tests**: Use `DefaultHttpContext` with manually configured properties — no full server spinup
- **File system tests**: Create temp directories in setup, clean up in `finally` blocks
- **Assertions**: `Assert.True`, `Assert.False`, `Assert.Equal`, `Assert.Contains`
- **Run**: `dotnet test` from repo root

## Commit Conventions

- **Prefixes**: `feat:`, `fix:`, `chore:`, `docs:`, `test:`, `refactor:`
- **Branch naming**: `feature/short-description` or `fix/short-description` (hyphenated)
- **No CI/CD** — run `dotnet build` and `dotnet test` locally before committing
