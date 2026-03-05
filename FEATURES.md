# FileBeam — Feature Backlog

Pick a label when you want to work on a feature (e.g. "let's do FB-04").

---

## Small / Self-contained

### FB-01 · Invite max-uses cap ✅

Each invite tracks browser joins and Bearer API calls independently, with optional caps on each.

**Browser (join link):** `joinMaxUses` caps how many times `/join/{id}` can be redeemed. Once `joinUseCount` reaches the cap the invite auto-deactivates (`IsActive = false`), which also stops Bearer access. Default: unlimited. Modal quick-select: 1 / 5 / 10 / Unlimited.

**API (Bearer token):** `bearerMaxUses` caps how many authenticated API requests the Bearer token can make. `bearerUseCount` is a new counter incremented by the auth middleware on every successful Bearer authentication. Default: unlimited. When the cap is reached the Bearer token is rejected but the join link (and any existing browser sessions) remain unaffected unless `joinMaxUses` is also hit.

**UI:** Uses column shows `join: X / N  api: X / N` when caps are set, or plain `X` when unlimited.

**Design decision — Path A (same token, separate counters):** browser link and Bearer token share the same invite ID. Independent caps are enforced via separate counters on the same record. This keeps the data model simple and backwards-compatible with existing `--invites-file` JSON (new fields are nullable/optional).

**Future — Path B (separate token IDs):** genuinely decouple the browser join token from the Bearer API token so that sharing the join link no longer exposes API access. This eliminates the shared-ID security warning but requires a breaking change to the invite record shape, persistence format, auth middleware, and all invite endpoints. Deferred; revisit if the shared-ID concern becomes a user pain point.

### FB-02 · Upload progress bar ✅

Show a per-file upload progress bar in the browser UI with percentage and estimated speed. Uses `XMLHttpRequest` (already used for CSRF) instead of a plain form `POST` so that `progress` events are available. No backend changes required.

### FB-03 · Clipboard paste upload

Paste an image (or any file) from the clipboard directly onto the page to upload it. Listens for the `paste` event on `document`, extracts `DataTransfer` items, and submits them through the same upload path as drag-and-drop. Works on all modern browsers.

**Implementation:**

- Listen for `paste` event on `document` in `app.js`
- Extract `event.clipboardData.files` or filter `event.clipboardData.items` to `kind === "file"`
- Re-use the existing `uploadFiles()` JS function to POST to `/upload/{subpath}`
- Show a toast/notification indicating how many files were pasted
- Works in all roles that can upload (admin, rw, wo); no server-side changes required

### FB-04 · Download count per file

Track how many times each file has been downloaded and display the count in the directory listing. Counts are in-memory only and reset on server restart.

**Implementation:**

- `ConcurrentDictionary<string, long>` keyed by absolute file path, held in `RouteHandlers`
- Increment on every successful `GET /download/{**subpath}` (skip range-resume requests that start at offset > 0, or only count the first range for partial requests)
- Add `downloadCount` field to `GET /info/{**subpath}` JSON response
- Display as a "Downloads" column in the file listing table (admin role only; hidden from other roles to avoid information leakage)
- No persistence; README notes that counts are ephemeral

---

## Medium

### FB-05 · Webhook on upload

POST a JSON payload to a configurable URL whenever a file is successfully uploaded. Configured via `--webhook-url <url>`. Non-blocking — fired in a background task so the upload response is not delayed. Failed deliveries are logged at warn level but not retried.

**Payload:**

```json
{
  "event": "upload",
  "file": "filename.ext",
  "path": "/relative/path/filename.ext",
  "sizeBytes": 12345,
  "uploadedBy": "invite:Alice",
  "timestamp": "2026-03-04T12:00:00Z"
}
```

**Implementation:**

- New CLI flag `--webhook-url <url>`; add `WebhookUrl` to `FileBeamConfig` and `ToCliCommand` export
- Fire-and-forget `HttpClient.PostAsJsonAsync` in `UploadFiles` handler after successful write
- Log delivery failure to console; do not block upload response
- README: document `--webhook-url` flag and payload schema

### FB-06 · File preview panel

Inline preview for images (PNG, JPEG, GIF, WebP, SVG) and plain text / code files. Clicking a supported file opens a side panel or modal instead of triggering a download. The panel has a Download button. Unsupported types fall back to the current download behaviour.

**Implementation:**

- Client-side panel (slide-in drawer or modal) in `app.js` / `index.html`
- Determine render mode from the `mime` field in the existing `GET /info/{path}` response — no new endpoint needed
- Image preview: `<img src="/download/{path}">` inside the panel
- Text/code preview: `fetch("/download/{path}")` → display in `<pre>`; cap at first 64 KB client-side
- "Download" button: `<a href="/download/{path}" download>`
- Panel closes on Escape or click-outside
- No server-side changes required

### FB-07 · Expiry auto-delete ✅

Automatically delete uploaded files after a configurable TTL. Set at the server level with `--upload-ttl <duration>` (e.g. `24h`, `7d`, `30m`). A background task runs every 60 seconds and removes files from `uploadDir` whose `LastWriteTime` is older than the TTL. Empty directories are pruned bottom-up after each sweep. The directory listing shows a live "expires in X" countdown badge on upload views.

**Admin exemption:** When `--per-sender` is active, the admin's named subfolder is skipped entirely. Without `--per-sender`, all files expire equally (no per-file metadata is stored).

**Implementation:**

- `src/UploadExpirer.cs` — `IAsyncDisposable` background worker (mirrors `AuditLogger` pattern)
- `--upload-ttl` CLI flag + `uploadTtl` config field in `filebeam.json`
- Expiry column injected into upload-context directory views (`/upload-area`, `/my-uploads`, `/admin/uploads`) only; main browse view unaffected
- Client-side countdown JS reuses `_fmtExpiry()` pattern from invite expiry
- 16 new tests (10 UploadExpirerTests + 6 HtmlRendererExpiryTests)

#### FB-07a · Expiry cleanup logging ✅

Console log output for every file the cleanup job touches, using AnsiConsole styled lines matching the existing request-log format.

- **Deleted files:** `[EXPIR]` line with relative path and file age — e.g. `user/report.pdf — expired after 2h 30m`
- **Admin-exempt files (past TTL, not deleted):** `[SKIP]` line with relative path — e.g. `admin/notes.txt — never expires`
- **Fresh files:** no log line (unchanged)
- **UI:** `/admin/uploads` expiry column shows "never expires" for files under the admin's exempt subfolder (when `--per-sender` is active); other senders' files still show a countdown. Same for the admin's own `/my-uploads` view.

**Implementation:**

- `UploadExpirer` accepts an optional `Action<string>? log` callback; `Program.cs` wires it to `AnsiConsole.MarkupLine`
- `UploadExpirer.AdminSubfolder` property exposes the computed exempt path so `Program.cs` can forward it to `RouteHandlers`
- `HtmlRenderer.RenderDirectory` gains `string? adminExemptPath` parameter; renders "never expires" cell instead of countdown when the file's full path is under that folder
- `RouteHandlers` gains `string? adminExemptPath` parameter; passed through to admin/uploads and my-uploads render calls
- ~6 new tests (log called/not called on deletion and exempt skip; "never expires" rendered in HTML)

#### FB-07b · Startup directory creation ✅

Auto-create the `--download` and `--upload` directories on startup if they do not exist, instead of exiting with an error. Uses `Directory.CreateDirectory` (idempotent, creates parent dirs). No CLI flags or config changes needed.

### FB-08 · Audit log viewer ✅

A read-only admin page at `GET /admin/audit` that renders the last N lines of the NDJSON audit log in a table (timestamp, action, user, file, bytes, IP, request ID). Auto-refreshes every 30 seconds. Only available when `--audit-log` is configured. No new log format changes — parses the existing NDJSON.

---

## Larger

### ✅ FB-09 · Active sessions dashboard

An admin page at `GET /admin/sessions` showing all currently active invite-based sessions: invite name, role, IP address, last-seen timestamp, and a Revoke button that invalidates the invite immediately. Sessions are tracked via a lightweight in-memory registry updated by the auth middleware on each request. Complements the existing revocation system.

### FB-10 · Folder upload

Drag a local folder onto the upload area and upload its entire directory tree, preserving relative paths. Uses the `webkitdirectory` attribute and the `DataTransferItem.webkitGetAsEntry()` API to walk the tree client-side, then uploads each file to the appropriate subpath on the server. Falls back gracefully on browsers that don't support the API.

**Implementation:**

- Add `webkitdirectory multiple` to the file `<input>` in `index.html`; expose as an opt-in toggle ("Upload folder" button) alongside the existing file picker
- Browser provides `file.webkitRelativePath`; strip the top-level folder name (optional UI toggle to keep it)
- POST each file individually to `/upload/{subpath}/{relativePath}` preserving nested structure
- Server: `UploadFiles` handler calls `Directory.CreateDirectory` for intermediate segments before writing; `SafeResolvePath` already prevents traversal
- Drag-and-drop: `DataTransferItem.webkitGetAsEntry()` → recursive `readEntries()` → same upload path
- Show per-file progress using the existing progress bar; aggregate percentage across all queued files

### FB-11 · Bandwidth throttling

Cap upload and download throughput per connection via `--max-upload-rate` and `--max-download-rate` flags (e.g. `1MB/s`, `500KB/s`). Useful for sharing large files over a connection where you want to preserve headroom for other traffic.

**Implementation:**

- Implement `ThrottledStream` — a wrapping `Stream` that uses a token-bucket algorithm to enforce a per-connection byte rate via `Task.Delay`
- Upload throttling: wrap `ctx.Request.Body` before reading in `UploadFiles`
- Download throttling: wrap the `FileStream` before passing to `Results.File()`
- Rate is per-connection (not shared globally); document this in README
- Add `MaxUploadRate` and `MaxDownloadRate` to `FileBeamConfig` and `ToCliCommand` export
- Parse human-readable values (`1MB/s`, `500KB/s`) the same way as `--max-upload-size`

---

## Gaps / New Features

### FB-12 · Server-side file search

A search endpoint and UI input that finds files by name across the entire directory tree (not just the current page).

**Implementation:**

- New endpoint: `GET /search?q=<term>&root=<subpath>`
- Server recursively enumerates `serveDir` (or `serveDir/<subpath>`) via `Directory.EnumerateFiles(..., "*", SearchOption.AllDirectories)`
- Filters file names (not full paths) case-insensitively against `q`
- Returns JSON array: `[{ "name": "foo.txt", "path": "/relative/path/foo.txt", "sizeBytes": 123, "modified": "..." }]`
- Cap results at 500 entries; include `"truncated": true` in response if exceeded
- Client: search box above the file table in `index.html`; on Enter or button click calls `/search?q=...&root=<currentPath>` and renders a flat result list
- Auth: same requirement as directory listing (any authenticated user)
- README: document `GET /search` endpoint

### FB-13 · File sorting (column headers)

Click column headers (Name, Size, Modified) to sort the file listing ascending/descending.

**Implementation:**

- Client-side only — sort already-rendered table rows in `app.js`
- Add `data-sort-key` attributes to `<th>` cells: `name`, `size`, `modified`
- Click cycles: ascending → descending → original order; show ▲/▼ indicator in header
- `HtmlRenderer` emits `data-size` and `data-modified` attributes on `<tr>` rows so JS can sort numerically/chronologically without re-parsing display text
- Persist sort preference (column + direction) to `localStorage`
- No server-side changes required

### ✅ FB-14 · Bulk select + delete/download

Checkbox selection on the file listing table with a toolbar for acting on multiple files at once.

**Implementation:**

- Add a checkbox column to the file table in `index.html`; "select all" checkbox in the header row
- Bulk actions toolbar appears when ≥1 file is selected: **Download as ZIP** (all roles) and **Delete selected** (admin only)
- Bulk download: POST selected paths to new `POST /download-zip` endpoint with JSON body `{ "paths": [...] }` → streams a ZIP response (reuse ZIP logic from `/upload-area/download-zip/`)
- Bulk delete: POST selected paths to new `POST /admin/delete-bulk` endpoint → deletes each, returns JSON summary of successes/failures
- Both endpoints require a valid CSRF token
- README: document new endpoints

### FB-15 · ZIP download from main browse view

Extend the existing ZIP download capability (currently upload-area only) to the main browse tree.

**Implementation:**

- New route: `GET /download-zip/{**subpath}` mapped against `serveDir`
- Reuse the existing ZIP streaming helper from `/upload-area/download-zip/`; only the root directory differs
- Add a "Download as ZIP" button to directory rows in the main browse listing (`HtmlRenderer`)
- Blocked for `wo` role (consistent with single-file download restriction)
- README: document endpoint

### FB-16 · Per-file QR code

Generate a QR code for the direct download URL of any file so users on phones can scan it without copying a link.

**Implementation:**

- New endpoint: `GET /qr/{**subpath}` — returns a PNG QR code for `http(s)://<host>:<port>/download/<path>`
- Use the existing `QRCoder` NuGet package (already a dependency)
- QR encodes the direct download URL (not a share token; use the share flow for expiring links)
- In the file listing, add a small QR icon button per file row; clicking opens a modal with the QR `<img>`
- No auth required to *view* the QR image (it only encodes a URL); downloading the file itself still enforces normal auth
- README: document `GET /qr/{**subpath}` endpoint

### FB-17 · Admin auto-login QR at startup ✅

Embed a single-use, time-limited admin token into the startup QR code so the admin can scan it to log in without typing credentials. Opt-out via `--no-qr-autologin`.

**Behaviour:**

- Default on; `--no-qr-autologin` disables it and falls back to printing the bare server URL in the QR
- At startup, generate a cryptographically random 32-byte token (`RandomNumberGenerator`)
- QR encodes `http(s)://<host>:<port>/auto-login/{token}` instead of the bare URL
- Print the expiry time alongside the QR in the terminal output, e.g. `⏱ Code expires at 09:32:15 (5 min)`
- `GET /auto-login/{token}`: validates token (not expired, not already used), sets HMAC-signed `fb.session` cookie with `admin` role, burns the token immediately, redirects to `/`
- Token is single-use: burned on first redemption regardless of remaining TTL
- Token TTL: 5 minutes from server start
- Expired or already-used token returns a plain HTML error page (no 401 challenge, no retry prompt)

**Re-generating the QR:**

- New admin endpoint `GET /admin/qr`: requires existing admin auth (Basic Auth or session cookie), generates a fresh token (invalidating any previous unused token), returns an HTML page containing the QR code as a `<img src="data:image/png;base64,…">` and the new expiry time
- This lets the admin get a fresh scannable link on demand — e.g. to hand to themselves on a second device — without restarting the server
- Link to `/admin/qr` added to the admin nav

**Implementation:**

- `AutoLoginToken` record: `{ string Token, DateTimeOffset ExpiresAt, bool Used }` stored in a single `volatile` field in `Program.cs` (only one token active at a time)
- Token generation and replacement is atomic (replace the field reference; no dictionary needed)
- `GET /auto-login/{token}` is exempt from the auth middleware (like `/s/{token}` and `/join/{token}`)
- `GET /admin/qr` is admin-only; calls the same token-generation helper and renders a minimal HTML page with the QR PNG (use `QRCoder` to produce a `PngByteQRCode`, base64-encode it inline)
- `--no-qr-autologin` flag; add `QrAutologin` bool to `FileBeamConfig` and `ToCliCommand` export
- README: document `--no-qr-autologin`, the expiry notice, and `GET /admin/qr`
