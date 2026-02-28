# ⚡ FileBeam

A dead-simple LAN file server. Run it, share the URL, your colleague downloads (or uploads) the file. No setup, no accounts, no cloud.

## Features

- 📁 Browse directories and subdirectories
- ⬇️ Download files with **resume support** (HTTP range requests)
- ⬆️ Upload files via drag-and-drop or file picker (up to 100 GB)
- 🗑️ Delete and rename files directly from the browser
- 🔒 Optional Basic Auth password protection
- 🚫 Read-only mode to disable uploads
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

You'll be prompted for a source directory, upload directory, and port (defaults to current directory, port 8080).

### CLI mode (scriptable / no prompts)

```
filebeam.exe --dir "C:\Transfers" --port 9000
```

| Flag | Short | Default | Description |
|------|-------|---------|-------------|
| `--dir` | `-d` | Current directory | Directory to browse and serve |
| `--target` | `-t` | Same as `--dir` | Upload destination (private; not visible to browsers) |
| `--port` | `-p` | `8080` | Port to listen on |
| `--password` | `--pw` | _(none)_ | Require this password via Basic Auth (any username accepted) |
| `--readonly` | `-r` | _(off)_ | Disable uploads; hide the upload form |

### Share the URL

FileBeam prints all your LAN IP addresses on startup:

```
╭─ FileBeam is running ─╮
│  Source:   C:\Transfers
│  URL:      http://192.168.1.42:8080
╰───────────────────────╯
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

## Build

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
# Debug run
dotnet run

# Publish single exe (win-x64)
dotnet publish -c Release
```

Output: `bin\Release\net10.0\win-x64\publish\filebeam.exe`

---

## Security notes

- **Authentication is optional.** Use `--password` to enable Basic Auth for trusted-but-not-open LAN scenarios. Without it, anyone on the network can access the server.
- **No HTTPS.** Intended for LAN use — add a reverse proxy if you need TLS.
- Path traversal is blocked; requests cannot escape the served directory.
- Filenames are sanitised on upload (directory components stripped).

## License

MIT
