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

### FB-04 · Download count per file

Track how many times each file has been downloaded and display the count in the directory listing. Counts are kept in-memory (lost on restart) and optionally persisted to a sidecar JSON file via a new `--download-counts` flag. The listing shows a small "N↓" badge next to each filename.

---

## Medium

### FB-05 · Webhook on upload

POST a JSON payload to a configurable URL whenever a file is successfully uploaded. Payload includes filename, size, uploader identity, timestamp, and destination path. Configured via `--webhook-url <url>` (and optionally `--webhook-secret` for HMAC-SHA256 signature verification). Non-blocking — fired in a background task so upload response is not delayed. Failed deliveries are logged but not retried.

### FB-06 · File preview panel

Inline preview for images (PNG, JPEG, GIF, WebP, SVG), plain text, Markdown (rendered), and PDF (via browser `<embed>`). Clicking a supported file opens a side panel or modal instead of triggering a download. The panel has a Download button. Unsupported types fall back to the current download behaviour.

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

### FB-09 · Active sessions dashboard

An admin page at `GET /admin/sessions` showing all currently active invite-based sessions: invite name, role, IP address, last-seen timestamp, and a Revoke button that invalidates the invite immediately. Sessions are tracked via a lightweight in-memory registry updated by the auth middleware on each request. Complements the existing revocation system.

### FB-10 · Folder upload

Drag a local folder onto the upload area and upload its entire directory tree, preserving relative paths. Uses the `webkitdirectory` attribute and the `DataTransferItem.webkitGetAsEntry()` API to walk the tree client-side, then uploads each file to the appropriate subpath on the server. Falls back gracefully on browsers that don't support the API.

### FB-11 · Bandwidth throttling

Cap download throughput per connection with a `--max-download-rate` flag (e.g. `1MB/s`, `500KB/s`). Implemented as a throttled stream wrapper around the file stream. Useful for sharing large files over a connection where you want to preserve headroom for other traffic. Upload throttling is out of scope for this feature.
