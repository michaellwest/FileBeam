# ⚡ FileBeam

A dead-simple LAN file server. Run it, share the URL, your colleague downloads (or uploads) the file. No setup, no accounts, no cloud.

## Features

- 📁 Browse directories and subdirectories
- ⬇️ Download files with **resume support** (HTTP range requests)
- ⬆️ Upload files via drag-and-drop or file picker (up to 100 GB)
- 🗑️ Delete and rename files directly from the browser
- 🔒 Optional Basic Auth — shared password or per-user credentials
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
| `--download`           | `-s`   | Current directory    | Directory to browse and serve                                     |
| `--upload`             | `-d`   | Same as `--download` | Upload destination (private; not visible to browsers)             |
| `--port`               | `-p`   | `8080`               | Port to listen on                                                 |
| `--password`           | `--pw` | _(none)_             | Shared Basic Auth password (any username accepted)                |
| `--credentials-file`   |        | _(none)_             | Path to a per-user credentials file (see [Per-user auth](#per-user-auth)) |
| `--invites-file`       |        | _(none)_             | Path to store invite tokens as JSON (see [Invites](#invites))     |
| `--readonly`           | `-r`   | _(off)_              | Disable uploads; hide the upload form                             |
| `--per-sender`         |        | _(off)_              | Bucket uploads into per-sender subfolders inside `--upload`       |
| `--max-upload-size`    |        | _(none)_             | Max request body size: `100MB`, `2GB`, `unlimited`                |
| `--max-upload-bytes`   |        | _(none)_             | Per-sender cumulative upload quota: `2GB`, `500MB`, `unlimited`   |
| `--max-upload-total`   |        | _(none)_             | Total upload directory cap (all senders): `10GB`, `500MB`         |
| `--tls-cert`           |        | _(none)_             | Path to TLS certificate PEM file (must be used with `--tls-key`)  |
| `--tls-key`            |        | _(none)_             | Path to TLS private key PEM file (must be used with `--tls-cert`) |
| `--log-level`          |        | `info`               | Console verbosity: `info` (default) or `debug`                    |
| `--config`             |        | _(none)_             | Path to a config file (see [Config file](#config-file))           |
| `--print-config`       |        | _(off)_              | Print the fully resolved config as JSON and exit (no server start) |

#### Config file

FileBeam can load all its settings from a `filebeam.json` file so you don't have to retype flags every run.

**Auto-discovery:** if `filebeam.json` exists in the same directory as the exe, it is loaded automatically with no flags required. Use `--config <path>` to point to a file elsewhere.

**Precedence:** hardcoded defaults → config file → CLI flags. CLI flags always win, so you can override a saved setting without editing the file.

**Passwords are intentionally omitted** from config files — pass `--password` on the CLI or use `--credentials-file` for per-user auth.

```jsonc
// filebeam.json
{
  "download":       "./files",
  "upload":         "./uploads",
  "port":           9090,
  "credentialsFile": "./users.txt",
  "invitesFile":    "./invites.json",
  "readonly":       false,
  "perSender":      false,
  "maxFileSize":    "500MB",
  "maxUploadBytes": "2GB",
  "maxUploadTotal": "10GB",
  "maxUploadSize":  "500MB",
  "tlsCert":        "./cert.pem",
  "tlsKey":         "./key.pem",
  "shareTtl":       3600,
  "auditLog":       "./audit.log",
  "auditLogMaxSize": "10MB",
  "rateLimit":      60,
  "logLevel":       "info"
}
```

All size fields accept the same human-readable format as the CLI (`500MB`, `2GB`, `unlimited`). JSON comments (`//`, `/* */`) and trailing commas are supported.

**Debugging config:** run `filebeam.exe --print-config` to see the fully resolved settings as JSON (passwords excluded) and exit without starting the server. Useful for verifying that the config file was picked up correctly.

```
filebeam.exe --config ./myconfig.json --print-config
```

#### HTTPS / TLS

FileBeam can serve over HTTPS using a PEM certificate and private key:

```
filebeam.exe --download ./share --tls-cert server.crt --tls-key server.key
```

Both flags must be provided together. Missing or unreadable files exit with code 1. The QR code and URL printed at startup automatically use `https://` when TLS is active.

**Generating a development certificate with [certz](https://github.com/michaellwest/certz):**

```
certz create --name filebeam --ip 192.168.1.100
filebeam.exe --download ./share --tls-cert filebeam.crt --tls-key filebeam.key
```

Clients connecting to a self-signed cert will see a browser warning; you can add the CA to your trust store to suppress it.

#### Per-user auth

`--credentials-file` points to a plain-text file with one `username:password` entry per line.
Lines starting with `#` and blank lines are ignored. Passwords may contain colons — only the first colon is the delimiter. Duplicate usernames: last entry wins.

```
# creds.txt
alice:S3cret!Pass
bob:AnotherPass123
```

```
filebeam.exe --download ./share --credentials-file ./creds.txt
```

`--password` and `--credentials-file` can be used together. A request is authenticated when it matches **either** a per-user entry **or** the shared password.

**Safeguards:**
- If the file is missing at startup, FileBeam **still enforces auth** — all logins are rejected until the file appears. A warning is printed to the console.
- Malformed lines (missing `:`, empty username, empty password) are skipped with a per-line warning showing the line number.
- If the file path is a directory, or the parent directory does not exist, or the file is unreadable, FileBeam exits with an error rather than starting unprotected.
- The file is **watched for changes** and reloaded automatically (300 ms debounce). Deleting the file at runtime locks everyone out; recreating it restores access. No restart required.
- Credentials are loaded once per save event. Changes take effect within ~300 ms.

#### Invites

Invite tokens let you grant time-limited or permanent access without sharing your main credentials. When a user opens the invite link, a signed session cookie is set and they are redirected to the file browser — no password prompt needed.

```
filebeam.exe --download ./share --credentials-file ./creds.txt --invites-file ./invites.json
```

Invite management is done via `GET /admin/invites` (HTML admin page, admin role required) or the REST API:

| Method   | Endpoint                   | Description                                                  |
| -------- | -------------------------- | ------------------------------------------------------------ |
| `GET`    | `/admin/invites`           | Admin UI page (HTML) — create, copy, and revoke invites      |
| `POST`   | `/admin/invites`           | Create an invite (JSON body, `Accept: application/json`)     |
| `GET`    | `/admin/invites`           | List all tokens (JSON when `Accept: application/json`)       |
| `DELETE` | `/admin/invites/{id}`      | Revoke an invite                                             |
| `PATCH`  | `/admin/invites/{id}`      | Edit name / role / expiry                                    |
| `GET`    | `/join/{token}`            | Redeem invite, set signed session cookie, show welcome page  |

The admin UI page shows active invites in a table with one-click copy-link and revoke buttons, plus a "New Invite" modal (name, role picker, expiry picker). Inactive / expired invites appear in a collapsed section. An **Invites** nav link is added to all pages when `--invites-file` is configured.

**Create body** (`Content-Type: application/json`, `X-CSRF-Token: <token>`):
```json
{ "friendlyName": "Alice", "role": "rw", "expiresAt": "2026-12-31T23:59:59Z" }
```
`role` defaults to `"rw"` if omitted. `expiresAt` is optional (omit for a non-expiring invite).

**Edit body** (`PATCH`):
```json
{ "friendlyName": "Alice 2", "role": "ro", "clearExpiry": true }
```
Any field may be omitted to leave it unchanged. Set `clearExpiry: true` to remove the expiry.

**Persistence:** if `--invites-file` is set, tokens are saved to a JSON file automatically on create, revoke, or edit, and reloaded at startup. Without the flag, tokens are in-memory only and lost on restart.

**Session cookies:** when a user visits their invite link, FileBeam sets a signed `fb.session` cookie (HMAC-SHA256, HttpOnly, SameSite=Lax). On subsequent requests the auth middleware verifies the signature and checks that the invite token is still active — revoking an invite immediately invalidates all cookies linked to it on the next request. The welcome page shown after joining includes a note for CLI users, who must use Basic Auth credentials if configured.

**Revocation:** deleting a token via `DELETE /admin/invites/{id}` sets it inactive immediately. All browser sessions issued via that invite are rejected from the next request onward.

#### Admin config export

Admin users get a **Config** link in the navigation bar (visible on the main browse page). Clicking it opens a two-tab modal:

- **Config File** tab — shows the current resolved configuration as indented JSON (passwords always omitted). A **Download JSON** button downloads it as `filebeam.json`, ready to use with `--config` or by dropping it in the working directory.
- **CLI Command** tab — shows the equivalent `filebeam.exe ...` command line (passwords omitted). A **Copy** button copies it to the clipboard.

The underlying data is also available as a plain JSON API endpoint:

| Method | Endpoint        | Description                                                     |
| ------ | --------------- | --------------------------------------------------------------- |
| `GET`  | `/admin/config` | Returns effective resolved config as JSON (admin role required) |

#### Per-sender upload folders

When `--per-sender` is set, each uploader's files land in their own subfolder inside the upload directory:

- If Basic Auth is enabled (`--password` or `--credentials-file`), the subfolder is named after the **username**.
- Otherwise, it is named after the sender's **IP address**.

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
# Password-protected, read-only
docker run -p 8080:8080 -v /data:/srv/share filebeam --password secret --readonly

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
To add a password, enable read-only mode, or activate per-sender folders, add a `command` override:

```yaml
services:
  filebeam:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./share:/srv/share
    restart: unless-stopped
    command: ["--password", "secret", "--readonly"]
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

- **Authentication is optional.** Use `--password` (shared) or `--credentials-file` (per-user) to enable Basic Auth for trusted-but-not-open LAN scenarios. Both can be active at the same time. Without auth, anyone on the network can access the server.
- **Credentials file permissions.** The credentials file contains plaintext passwords. Restrict read access to the user running FileBeam (e.g. `chmod 600 creds.txt` on Linux/macOS). On Windows, use NTFS ACLs to limit access.
- **No HTTPS.** Intended for LAN use — add a reverse proxy if you need TLS. Basic Auth credentials are transmitted in the clear over HTTP.
- Path traversal is blocked; requests cannot escape the served directory.
- Filenames are sanitised on upload (directory components stripped).

## License

MIT
