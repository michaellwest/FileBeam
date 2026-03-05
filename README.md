# ⚡ FileBeam

A dead-simple LAN file server. Run it, share the URL, your colleague downloads (or uploads) the file. No setup, no accounts, no cloud.

## Features

- 📁 Browse directories and subdirectories
- ⬇️ Download files with **resume support** (HTTP range requests)
- ⬆️ Upload files via drag-and-drop or file picker (up to 100 GB)
- 🗑️ Delete and rename files directly from the browser
- 🔒 Admin account with Basic Auth — password auto-generated or configurable
- 🚫 Read-only mode to disable uploads
- 📥 Per-sender upload folders — each contributor's files land in their own subfolder
- 🔄 Live reload — the browser updates automatically when files change
- 🖥️ Clean dark-themed web UI
- 🔍 Live request log in the console (with elapsed time)
- 📦 Single `.exe` — no install, no runtime required

## Usage

### Interactive mode

Just double-click `filebeam.exe` or run it from a terminal:

```
filebeam.exe
```

You'll be prompted for a serve directory, drop directory, and port (defaults to current directory, port 8080).
When running non-interactively (e.g. inside a container with no TTY), all prompts are skipped and defaults are used automatically.

### CLI mode (scriptable / no prompts)

```
filebeam.exe --download "./share/download" --port 9000
```

| Flag                   | Short  | Default              | Description                                                       |
| ---------------------- | ------ | -------------------- | ----------------------------------------------------------------- |
| `--download`           | `-s`   | Current directory    | Directory to browse and serve (created if missing)                |
| `--upload`             | `-d`   | Same as `--download` | Upload destination (private; not visible to browsers; created if missing) |
| `--port`               | `-p`   | `8080`               | Port to listen on                                                 |
| `--admin-username`     |        | `admin`              | Username for the built-in admin account                           |
| `--admin-password`     |        | _(auto-generated)_   | Admin password (or set `FILEBEAM_ADMIN_PASSWORD` env var)         |
| `--invites-file`       |        | _(none)_             | Path to store invite tokens as JSON (see [Invites](#invites))     |
| `--readonly`           | `-r`   | _(off)_              | Disable uploads; hide the upload form                             |
| `--per-sender`         |        | _(off)_              | Bucket uploads into per-sender subfolders inside `--upload`       |
| `--max-upload-size`    |        | _(none)_             | Max request body size: `100MB`, `2GB`, `unlimited`                |
| `--max-upload-bytes`   |        | _(none)_             | Per-sender cumulative upload quota: `2GB`, `500MB`, `unlimited`   |
| `--max-upload-total`   |        | _(none)_             | Total upload directory cap (all senders): `10GB`, `500MB`         |
| `--tls-cert`           |        | _(none)_             | Path to TLS certificate PEM file (must be used with `--tls-key`)  |
| `--tls-key`            |        | _(none)_             | Path to TLS private key PEM file (must be used with `--tls-cert`) |
| `--tls-pfx`            |        | _(none)_             | Path to a PKCS#12/PFX certificate bundle (alternative to `--tls-cert`/`--tls-key`) |
| `--tls-pfx-password`   |        | _(none)_             | Password for the PFX file (optional; omit if the file has no password) |
| `--log-level`          |        | `info`               | Console verbosity: `info` (default) or `debug`                    |
| `--upload-ttl`         |        | _(none)_             | Auto-delete uploaded files after this duration (`30m`, `24h`, `7d`). When `--per-sender` is active the admin's named subfolder is exempt. |
| `--max-concurrent-zips` |       | `2`                  | Max simultaneous ZIP downloads; excess requests return 503. Set to `0` for unlimited. |
| `--max-zip-size`       |        | _(none)_             | Reject ZIP requests for directories exceeding this size (`10GB`, `500MB`); returns 413. |
| `--no-qr-autologin`    |        | _(off)_              | Disable the auto-login token embedded in the startup QR code; QR encodes the bare server URL instead. |
| `--config`             |        | _(none)_             | Path to a config file (see [Config file](#config-file))           |
| `--print-config`       |        | _(off)_              | Print the fully resolved config as JSON and exit (no server start) |

#### Config file

FileBeam can load all its settings from a `filebeam.json` file so you don't have to retype flags every run.

**Auto-discovery:** if `filebeam.json` exists in the same directory as the exe, it is loaded automatically with no flags required. Use `--config <path>` to point to a file elsewhere.

**Precedence:** hardcoded defaults → config file → CLI flags. CLI flags always win, so you can override a saved setting without editing the file.

**`adminPassword` is intentionally excluded** from config files — pass `--admin-password` on the CLI, set `FILEBEAM_ADMIN_PASSWORD`, or let FileBeam auto-generate one and save it to `filebeam-admin.key`.

```jsonc
// filebeam.json
{
  "download":       "./files",
  "upload":         "./uploads",
  "port":           9090,
  "adminUsername":  "admin",
  "invitesFile":    "./invites.json",
  "readonly":       false,
  "perSender":      false,
  "maxFileSize":    "500MB",
  "maxUploadBytes": "2GB",
  "maxUploadTotal": "10GB",
  "maxUploadSize":  "500MB",
  "tlsCert":        "./cert.pem",
  "tlsKey":         "./key.pem",
  "tlsPfx":         "./cert.pfx",
  "tlsPfxPassword": "changeme",
  "shareTtl":       3600,
  "auditLog":       "./audit.log",
  "auditLogMaxSize": "10MB",
  "rateLimit":      60,
  "logLevel":       "info",
  "uploadTtl":      "24h",
  "qrAutologin":    true
}
```

All size fields accept the same human-readable format as the CLI (`500MB`, `2GB`, `unlimited`). JSON comments (`//`, `/* */`) and trailing commas are supported.

**Debugging config:** run `filebeam.exe --print-config` to see the fully resolved settings as JSON (passwords excluded) and exit without starting the server. Useful for verifying that the config file was picked up correctly.

```
filebeam.exe --config ./myconfig.json --print-config
```

#### HTTPS / TLS

FileBeam supports two ways to enable HTTPS. The two methods are **mutually exclusive** — using both at the same time exits with an error.

**Option A — PEM certificate + key** (generated by certz, Let's Encrypt, etc.):

```
filebeam.exe --download ./share --tls-cert server.crt --tls-key server.key
```

Both flags must be provided together. Missing or unreadable files exit with code 1.

**Option B — PKCS#12 / PFX bundle** (common on Windows, exported from IIS or Certificate Manager):

```
# With a password
filebeam.exe --download ./share --tls-pfx cert.pfx --tls-pfx-password secret

# Without a password (many PFX files have no password)
filebeam.exe --download ./share --tls-pfx cert.pfx
```

To create a PFX from existing PEM files:
```
openssl pkcs12 -export -in cert.pem -inkey key.pem -out cert.pfx -passout pass:secret
```

The QR code and URL printed at startup automatically use `https://` when TLS is active.

**Generating a development certificate with [certz](https://github.com/michaellwest/certz):**

```
certz create --name filebeam --ip 192.168.1.100
filebeam.exe --download ./share --tls-cert filebeam.crt --tls-key filebeam.key
```

Clients connecting to a self-signed cert will see a browser warning; you can add the CA to your trust store to suppress it.

#### Admin account

FileBeam always requires authentication. On first run, if no password is configured, it **auto-generates a 16-character random password**, prints it prominently in the terminal, and saves it to `filebeam-admin.key` in the working directory. On subsequent restarts the key file is read automatically, so the password survives without re-entering it.

**Password resolution order** (first match wins):
1. `FILEBEAM_ADMIN_PASSWORD` environment variable — ideal for scripts and containers
2. `--admin-password <pass>` CLI flag — convenient for one-offs (appears in process list)
3. `filebeam-admin.key` file in the working directory — auto-read on restart
4. Auto-generate and write to `filebeam-admin.key`

```
# Set a password via environment variable (recommended for production)
FILEBEAM_ADMIN_PASSWORD=mysecret filebeam.exe --download ./share

# Or pass it directly (visible in process list)
filebeam.exe --download ./share --admin-password mysecret

# Or let FileBeam generate one — it prints to console and saves to filebeam-admin.key
filebeam.exe --download ./share
```

The admin account uses the username `admin` by default; override with `--admin-username <name>`.

#### Invites

Invite tokens let you grant time-limited or permanent access to other users without sharing the admin password. Each token can be used as a **browser join link** or a **CLI Bearer token** — they share the same invite ID.

```
filebeam.exe --download ./share --invites-file ./invites.json
```

**Browser access:** the recipient visits `http://host/join/{id}`. FileBeam sets a signed `fb.session` cookie (HMAC-SHA256, HttpOnly, SameSite=Lax) and redirects to the file browser — no password prompt needed.

**CLI / API access:** use the token ID as an HTTP Bearer token:

```bash
# Download a file using a Bearer token
curl -H "Authorization: Bearer <invite-id>" http://host/download/report.pdf -o report.pdf

# PowerShell
Invoke-WebRequest http://host/download/report.pdf `
  -Headers @{ Authorization = "Bearer <invite-id>" } `
  -OutFile report.pdf
```

> ⚠ **Security note:** sharing the browser join link also exposes the Bearer token ID — both grant the same access. Use TLS (`--tls-cert` / `--tls-key`) when transmitting Bearer tokens over untrusted networks. Revoking an invite immediately invalidates both the cookie sessions and Bearer token.

**UseCount tracking:** Bearer token usage does **not** increment the invite's `useCount` — that counter only reflects browser `/join` events.

**Invite management** is done via `GET /admin/invites` (HTML admin page, admin role required) or the REST API:

| Method   | Endpoint                   | Description                                                  |
| -------- | -------------------------- | ------------------------------------------------------------ |
| `GET`    | `/admin/invites`           | Admin UI page (HTML) — create, copy, and revoke invites      |
| `POST`   | `/admin/invites`           | Create an invite (JSON body, `Accept: application/json`)     |
| `GET`    | `/admin/invites`           | List all tokens (JSON when `Accept: application/json`)       |
| `DELETE` | `/admin/invites/{id}`      | Revoke an invite                                             |
| `PATCH`  | `/admin/invites/{id}`      | Edit name / role / expiry                                    |
| `GET`    | `/join/{token}`            | Redeem invite, set signed session cookie, show welcome page  |

The admin UI page shows active invites in a table with one-click copy buttons for the join link (🔗) and Bearer token (⌨), plus a revoke button. The **New Invite** modal shows both the browser join URL and the `Bearer <id>` string after creation. Inactive / expired invites appear in a collapsed section. An **Invites** nav link is added to all pages when `--invites-file` is configured.

The **Expires** column shows a live relative countdown that updates every 10 seconds (e.g. `expires in 2h 30m`, `expires in 45s`). Expired invites that haven't been cleaned up display `expired Xm ago` in red. Hover the cell for the absolute UTC timestamp.

**Create body** (`Content-Type: application/json`, `X-CSRF-Token: <token>`):
```json
{ "friendlyName": "Alice", "role": "rw", "expiresAt": "2026-12-31T23:59:59Z", "joinMaxUses": 1, "bearerMaxUses": 100 }
```
`role` defaults to `"rw"` if omitted. `expiresAt`, `joinMaxUses`, and `bearerMaxUses` are optional (omit for unlimited).

**Edit body** (`PATCH`):
```json
{ "friendlyName": "Alice 2", "role": "ro", "clearExpiry": true, "joinMaxUses": 5, "clearBearerMaxUses": true }
```
Any field may be omitted to leave it unchanged. Set `clearExpiry: true`, `clearJoinMaxUses: true`, or `clearBearerMaxUses: true` to remove a cap.

**Use caps:** each invite tracks browser joins and Bearer API calls independently.

- `joinMaxUses` — caps how many times `/join/{id}` can be redeemed (browser only). When the cap is reached the invite **auto-deactivates** (`IsActive = false`), which also stops Bearer access.
- `bearerMaxUses` — caps how many times the Bearer token can authenticate per request. When the cap is reached the Bearer token is rejected, but the join link and any existing browser sessions are **not** affected — the invite stays active for join purposes unless `joinMaxUses` is also hit.

The **Uses** column in the admin UI shows `join: X / N` and `api: X / N` when caps are set, or a plain number when unlimited. The New Invite modal includes quick-select dropdowns for both caps (Unlimited / 1 / 5 / 10 for join; Unlimited / 1 / 10 / 100 for Bearer).

**Persistence:** if `--invites-file` is set, tokens are saved to a JSON file automatically on create, revoke, or edit, and reloaded at startup. Without the flag, tokens are in-memory only and lost on restart.

**Session cookies:** when a user visits their invite link, FileBeam sets a signed `fb.session` cookie (HMAC-SHA256, HttpOnly, SameSite=Lax). On subsequent requests the auth middleware verifies the signature and checks that the invite token is still active — revoking an invite immediately invalidates all cookies linked to it on the next request.

**Revocation:** `DELETE /admin/invites/{id}` sets the token inactive immediately. All browser sessions and Bearer token requests using that invite are rejected from the next request onward.

#### Admin config export

Admin users get a **Config** link in the navigation bar (visible on the main browse page). Clicking it opens a two-tab modal:

- **Config File** tab — shows the current resolved configuration as indented JSON (passwords always omitted). A **Download JSON** button downloads it as `filebeam.json`, ready to use with `--config` or by dropping it in the working directory.
- **CLI Command** tab — shows the equivalent `filebeam.exe ...` command line (passwords omitted). A **Copy** button copies it to the clipboard.

The underlying data is also available as a plain JSON API endpoint:

| Method | Endpoint        | Description                                                     |
| ------ | --------------- | --------------------------------------------------------------- |
| `GET`  | `/admin/config` | Returns effective resolved config as JSON (admin role required) |

#### Audit log viewer

When `--audit-log` is configured with a file path (not `-`/stdout), an **Audit Log** link appears in the admin navigation bar. It opens `GET /admin/audit`, a read-only HTML page showing the last 200 log entries in reverse-chronological order (most recent first). The page auto-refreshes every 30 seconds.

| Method | Endpoint       | Description                                                        |
| ------ | -------------- | ------------------------------------------------------------------ |
| `GET`  | `/admin/audit` | Last 200 audit entries as HTML table (admin only; 404 if no file)  |

**Columns:** Timestamp · Action · User · File · Bytes · IP · Status

The page returns `404 Not Found` when `--audit-log` is absent or set to `-` (stdout mode). Malformed or unparseable log lines are silently skipped.

#### Active sessions dashboard

A **Sessions** link appears in the admin navigation bar. It opens `GET /admin/sessions`, a real-time HTML dashboard showing every invite-based session (Bearer token or cookie) that has been active within the past 30 minutes.

| Method | Endpoint                         | Description                                               |
| ------ | -------------------------------- | --------------------------------------------------------- |
| `GET`  | `/admin/sessions`                | Active invite sessions as HTML table (admin only)         |
| `POST` | `/admin/sessions/{id}/revoke`    | Revoke the invite and clear its sessions (admin only)     |

**Columns:** Invite name · Role badge (color-coded) · IP · Auth method (bearer/cookie) · Last seen (relative time)

Each row has a **Revoke** button that immediately deactivates the underlying invite token and removes all associated session entries. The page auto-refreshes every 30 seconds. Admin's own Basic Auth sessions are not tracked (only invite-based auth appears here).

#### Admin auto-login QR

By default the startup QR code encodes a **single-use 5-minute auto-login token** so the admin can scan once and land directly in an authenticated session — no password typing needed:

```
Scanning the QR code → GET /auto-login/{token} → admin session cookie set → redirect to /
```

- The token is burned on first use (scanning again shows an error page).
- The token expires after 5 minutes. FileBeam prints the expiry time next to the QR.
- Disable with `--no-qr-autologin` (or `"qrAutologin": false` in `filebeam.json`) to encode the bare server URL instead.

While logged in, admins can regenerate a fresh QR at any time:

| Method | Endpoint      | Description                                                    |
| ------ | ------------- | -------------------------------------------------------------- |
| `GET`  | `/auto-login/{token}` | Redeem a startup auto-login token (unauthenticated)  |
| `GET`  | `/admin/qr`   | Generate a new auto-login QR and display it as HTML (admin only) |

#### Upload expiry auto-delete

Use `--upload-ttl` to automatically delete uploaded files from `uploadDir` after a configurable time-to-live:

```
filebeam.exe --upload ./inbox --upload-ttl 24h
```

Accepted formats: `30m` (minutes), `24h` (hours), `7d` (days), or a plain number of seconds (`3600`). A background task scans every 60 seconds and removes files whose `LastWriteTime` is older than the TTL. Empty directories are pruned bottom-up after each sweep.

**Admin exemption:** When `--per-sender` is active, the admin user's named subfolder is skipped so admin-uploaded files are never auto-deleted. Without `--per-sender`, all files expire equally.

**UI:** Upload views (`/upload-area`, `/my-uploads`, `/admin/uploads`) show an **Expires** column with a live countdown badge (updated every 10 seconds). Files that have already passed their TTL are shown in red.

The `uploadTtl` field is also supported in `filebeam.json`:
```json
{ "uploadTtl": "24h" }
```

#### Per-sender upload folders

When `--per-sender` is set, each uploader's files land in their own subfolder inside the upload directory:

- If the user is authenticated via Basic Auth (admin) the subfolder is named after the **admin username**.
- If authenticated via an invite (Bearer token or session cookie) the subfolder is named after the invite's **friendly name**.
- Otherwise (unauthenticated, if auth is somehow bypassed) it is named after the sender's **IP address**.

```
filebeam.exe --download ./share/download --upload ./share/upload --per-sender
```

```
inbox/
  alice/          ← authenticated user
    report.pdf
  192.168.1.42/   ← anonymous sender
    photo.jpg
```

### Share the URL

FileBeam prints all your LAN IP addresses on startup:

```
╭───────── FileBeam is running ────────╮
│  Download: ./share                   │
│  URL:      http://192.168.1.42:8080  │
╰──────────────────────────────────────╯
```

Send that URL to your colleague. They can browse, download, or upload — no software required on their end.

---

## Downloading files — browser, curl, and PowerShell

### Browser

Just click any file link. The browser handles everything, including resuming interrupted downloads if it supports it.

### curl (macOS, Linux, WSL, Git Bash)

**Basic download:**

```bash
curl -O http://192.168.1.42:8080/download/BigFile.zip
```

**Resume an interrupted download** (`-C -` tells curl to detect the offset automatically):

```bash
curl -C - -O http://192.168.1.42:8080/download/BigFile.zip
```

**Download with a progress bar:**

```bash
curl -C - -O --progress-bar http://192.168.1.42:8080/download/BigFile.zip
```

**File in a subdirectory:**

```bash
curl -C - -O http://192.168.1.42:8080/download/Projects/Q4/Report.xlsx
```

**With password protection:**

```bash
curl -u :mysecret -C - -O http://192.168.1.42:8080/download/BigFile.zip
```

### PowerShell (Windows)

**Basic download** (shows progress, saves to current directory):

```powershell
Invoke-WebRequest http://192.168.1.42:8080/download/BigFile.zip -OutFile BigFile.zip
```

> **Tip:** `Invoke-WebRequest` can be slow for large files due to buffering. Use `curl.exe` (ships with Windows 10+) for better performance and resume support:

```powershell
# curl.exe is curl proper, not the Invoke-WebRequest alias
curl.exe -C - -O http://192.168.1.42:8080/download/BigFile.zip
```

**PowerShell with resume using Range header** (pure PowerShell, no curl):

```powershell
$url      = "http://192.168.1.42:8080/download/BigFile.zip"
$outFile  = "BigFile.zip"
$existing = (Test-Path $outFile) ? (Get-Item $outFile).Length : 0

$headers  = @{}
if ($existing -gt 0) { $headers["Range"] = "bytes=$existing-" }

Invoke-WebRequest $url -OutFile $outFile -Headers $headers
```

---

## How resume works

FileBeam supports the **HTTP Range** specification (RFC 7233). When a download is interrupted and restarted:

1. The client sends a `Range: bytes=<offset>-` header indicating where it left off.
2. FileBeam responds with `206 Partial Content` and streams only the remaining bytes.
3. The client appends those bytes to the partial file on disk.

This means **any client that supports range requests** — curl, wget, aria2, modern browsers, PowerShell — can resume interrupted downloads automatically. No special configuration needed on the FileBeam side.

---

## Docker

### Build the image

```bash
docker build -t filebeam .
```

### Run with a host directory (recommended)

Mount a folder from your host so files are always written to disk:

```bash
# Linux / macOS
docker run -p 8080:8080 -v /path/to/share:/srv/share filebeam

# Windows (PowerShell)
docker run -p 8080:8080 -v ./path/to/share:/srv/share filebeam
```

Open `http://localhost:8080` in a browser, or share the host machine's LAN IP.

### Run without a mount

If you omit `-v`, Docker creates an **anonymous managed volume** for `/srv/share`.
Files persist across `docker restart` but are lost when the container is removed (`docker rm`).

```bash
docker run -p 8080:8080 filebeam
```

### Pass additional flags

Append any FileBeam flags after the image name:

```bash
# Set admin password via env var, read-only mode
docker run -p 8080:8080 -v /data:/srv/share -e FILEBEAM_ADMIN_PASSWORD=secret filebeam --readonly

# Per-sender upload folders (separate upload volume)
docker run -p 8080:8080 -v /data/share:/srv/share -v /data/inbox:/srv/drop filebeam \
  --upload /srv/drop --per-sender

# Custom port (also update -p accordingly)
docker run -p 9000:9000 -v /data:/srv/share filebeam --port 9000
```

### Docker Compose

A ready-to-use `docker-compose.yml` is included at the repo root.

```bash
mkdir share                 # create the shared folder first
mkdir share/download
mkdir share/upload
docker compose up -d        # build image and start in background
docker compose logs -f      # follow logs
docker compose down         # stop
```

Files are served from the `./share` directory next to the compose file.
To set a password, enable read-only mode, or activate per-sender folders, add `environment` and `command` overrides:

```yaml
services:
  filebeam:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./share:/srv/share
    restart: unless-stopped
    environment:
      - FILEBEAM_ADMIN_PASSWORD=secret
    command: ["--readonly"]
```

Per-sender example with a separate drop volume:

```yaml
services:
  filebeam:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./share/download:/srv/share/download
      - ./share/upload:/srv/share/upload
    restart: unless-stopped
    command: ["--upload", "/srv/drop", "--per-sender"]
```

---

## Build

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
# Debug run
dotnet run --project src/

# Publish single exe (win-x64)
dotnet publish src/ -p:PublishProfile=win-x64
```

Output: `src\bin\Release\net10.0\win-x64\publish\filebeam.exe`

---

## Security notes

- **Authentication is always required.** FileBeam always creates an admin account. If no password is configured, one is auto-generated and saved to `filebeam-admin.key`. Keep this file private — anyone who reads it gains admin access.
- **Key file permissions.** Restrict read access to `filebeam-admin.key` (e.g. `chmod 600 filebeam-admin.key` on Linux/macOS, NTFS ACLs on Windows). Prefer `FILEBEAM_ADMIN_PASSWORD` env var for non-interactive deployments.
- **Bearer tokens and join links share the same ID.** Distributing a join link exposes the Bearer token for API access. Use TLS (`--tls-cert`/`--tls-key`) when transmitting Bearer tokens over networks you don't fully control.
- **No HTTPS by default.** Intended for LAN use — add a reverse proxy or use `--tls-cert`/`--tls-key` if you need TLS. Without TLS, credentials are transmitted in the clear.
- Path traversal is blocked; requests cannot escape the served directory.
- Filenames are sanitised on upload (directory components stripped).

## License

MIT
