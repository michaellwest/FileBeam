# FileBeam Security Analysis & Recommendations

This document covers known attack surfaces in FileBeam, their severity in a typical LAN context, and concrete mitigation recommendations for each.

---

## 1. Disk Exhaustion via Upload Flooding

**Severity:** High
**Affected code:** [src/Program.cs:110–117](src/Program.cs#L110-L117), [src/RouteHandlers.cs:178–244](src/RouteHandlers.cs#L178-L244)

**Problem**
`MaxRequestBodySize` and `MultipartBodyLengthLimit` are both set to 100 GB. There is no per-file limit, no per-user quota, no cumulative session limit, and no check against available disk space. An attacker — with or without a password — can saturate the disk in a single or repeated request.

**Recommendations**

- Add a `--max-file-size` CLI flag (e.g., default `10 GB`) enforced per individual file via a `LengthLimitedStream` wrapper before writing to disk. Reject with `413 Payload Too Large` when exceeded.
- Add a `--max-upload-bytes` CLI flag for a cumulative per-session (per-IP or per-username) quota tracked in a `ConcurrentDictionary<string, long>`. Increment on each successful upload; reject with `429` when exceeded.
- Before writing each file, check `DriveInfo.AvailableFreeSpace` against a minimum headroom threshold (e.g., 512 MB) and reject with `507 Insufficient Storage` if headroom would be breached.
- Lower the Kestrel and FormOptions limits to match `--max-file-size` so oversized bodies are rejected before the handler is even invoked.

---

## 2. SSE Connection Exhaustion

**Severity:** High
**Affected code:** [src/RouteHandlers.cs:319–344](src/RouteHandlers.cs#L319-L344), [src/RouteHandlers.cs:13–61](src/RouteHandlers.cs#L13-L61)

**Problem**
`GET /events` imposes no cap on concurrent connections. Each open connection adds an entry to `FileWatcher._clients` (a `List<Channel<string>>` with no max size) and holds an async task alive indefinitely. An attacker can open thousands of SSE connections to exhaust the thread pool and process memory.

**Recommendations**

- Add a `SemaphoreSlim _sseLimit` (e.g., `new SemaphoreSlim(50, 50)`) to `FileWatcher`. In `FileEvents`, call `TryEnter` before subscribing and return `503 Service Unavailable` immediately if the cap is reached. Release in the `finally` block.
- Expose the cap as a `--max-sse-connections` CLI flag.
- Add a server-side keepalive timeout: if no `reload` event is written within N minutes, close the SSE stream. This reclaims connections from clients that disconnected without sending a TCP FIN (e.g., mobile devices going to sleep).

---

## 3. Password Brute Force

**Severity:** High (when password is configured)
**Affected code:** [src/Program.cs:175–197](src/Program.cs#L175-L197)

**Problem**
The Basic Auth middleware has no failed-attempt tracking, no lockout, no delay, and no rate limit. On a LAN link, an attacker can attempt thousands of passwords per second.

**Recommendations**

- Track failed attempts per IP in a `ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)>`. After N consecutive failures (e.g., 10), return `429 Too Many Requests` with a `Retry-After` header and lock out that IP for a window (e.g., 60 seconds). Reset the counter on a successful authentication.
- Add a fixed `Task.Delay(200–500 ms)` on every failed authentication attempt, regardless of lockout state, to slow systematic attacks without requiring state for the delay itself.
- Replace the direct string equality check with `CryptographicOperations.FixedTimeEquals` (see §7 below).

---

## 4. Symlink / Junction Traversal

**Severity:** Medium
**Affected code:** [src/RouteHandlers.cs:65–78](src/RouteHandlers.cs#L65-L78), [src/RouteHandlers.cs:85–98](src/RouteHandlers.cs#L85-L98)

**Problem**
`SafeResolvePath` and `SafeResolveUploadPath` use `Path.GetFullPath`, which normalizes the string path but does **not** dereference symlinks or directory junctions on Windows. A symlink named `escape` located inside `serveDir` that points to `C:\` would pass the `StartsWith(rootDir)` check — because the symlink itself lives under `rootDir` — but filesystem operations through it would access the symlink's target outside the root.

**Recommendations**

- After resolving the path string, check whether any component of the path is a reparse point using `new FileInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint)` (for files) or `new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint)` (for directories). Reject with `403 Forbidden` if any component is a symlink or junction.
- Alternatively, use `File.ResolveLinkTarget(resolved, returnFinalTarget: true)` / `Directory.ResolveLinkTarget(resolved, returnFinalTarget: true)` (.NET 6+) to obtain the real physical path, then re-validate that real path against `rootDir`.
- Walk each path component from `rootDir` to `resolved` and reject at the first reparse point encountered to catch intermediate symlinks.

---

## 5. Directory Creation Bomb

**Severity:** Medium
**Affected code:** [src/RouteHandlers.cs:195–197](src/RouteHandlers.cs#L195-L197)

**Problem**
`UploadFiles` calls `Directory.CreateDirectory(resolved)` unconditionally. An attacker can craft a `subpath` that creates hundreds of nested or sibling directories in a single request (e.g., `a/b/c/.../z`), exhausting directory entries, inode tables, or simply cluttering the upload tree.

**Recommendations**

- Enforce a maximum path depth relative to `uploadDir`. Compute `resolved.Split(Path.DirectorySeparatorChar).Length - uploadDir.Split(Path.DirectorySeparatorChar).Length` and reject with `400 Bad Request` when it exceeds a configurable limit (e.g., `--max-depth 5`, default 5).
- Enforce a maximum number of entries (files + subdirectories) per directory before creating a new subdirectory. Check `Directory.EnumerateFileSystemEntries(parentDir).Take(limit + 1).Count() > limit` and reject with `507` when exceeded.

---

## 6. `ResolveUniqueFileName` Stat-Loop Amplification

**Severity:** Medium
**Affected code:** [src/RouteHandlers.cs:250–267](src/RouteHandlers.cs#L250-L267)

**Problem**
The de-duplication loop calls `File.Exists` up to 10,000 times sequentially on each upload. If a directory is pre-seeded with `foo (1).txt` through `foo (9,999).txt`, every subsequent upload of `foo.txt` performs 10,000 blocking filesystem stat calls before writing anything. Repeated concurrently, this can make the server unresponsive.

**Recommendations**

- Add a per-directory file count cap (e.g., 1,000 files) checked before accepting each upload. `Directory.GetFiles(resolved).Length >= maxFilesPerDir` → `507 Insufficient Storage`. This eliminates the attack vector entirely while also keeping directories navigable.
- Reduce the loop maximum from 10,000 to a small number (e.g., 100) and return an error rather than falling back to a GUID after 100 collisions. This bounds the worst-case stat calls to 100.

---

## 7. Timing Attack on Password Comparison

**Severity:** Low
**Affected code:** [src/Program.cs:185](src/Program.cs#L185)

**Problem**
`decoded[(colon + 1)..] == password` is a standard string equality check, which short-circuits on the first differing character. This leaks information about the correct password length and prefix via response-time measurement. On a LAN with microsecond-resolution timing, this is exploitable with enough samples.

**Recommendation**

- Replace with a constant-time comparison:
  ```csharp
  var submittedBytes = Encoding.UTF8.GetBytes(decoded[(colon + 1)..]);
  var expectedBytes  = Encoding.UTF8.GetBytes(password);
  if (CryptographicOperations.FixedTimeEquals(submittedBytes, expectedBytes))
  ```
  `CryptographicOperations.FixedTimeEquals` is available in `System.Security.Cryptography` (.NET 5+) and always compares all bytes regardless of where they first differ.

---

## 8. No CSRF Protection on Mutation Endpoints

**Severity:** Low (LAN context)
**Affected code:** [src/RouteHandlers.cs:269–316](src/RouteHandlers.cs#L269-L316)

**Problem**
`POST /delete/{**subpath}` and `POST /rename/{**subpath}` perform state-changing operations with no CSRF token. A malicious page visited by an authenticated user can submit cross-origin forms to these endpoints and silently delete or rename files.

**Recommendations**

- Generate a per-session random token (e.g., `Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))`) at startup and store it in a static field. Embed it as a hidden `<input name="_csrf">` in every HTML form rendered by `HtmlRenderer`. Validate it on each `POST` before processing.
- As a simpler alternative, check the `Origin` or `Referer` request header on all mutation endpoints and reject requests where the origin does not match the server's own base URL.

---

## 9. Plain HTTP Exposes Basic Auth Credentials

**Severity:** Low (expected for LAN tool; document clearly)
**Affected code:** [src/Program.cs:107–110](src/Program.cs#L107-L110)

**Problem**
FileBeam listens on plain HTTP. When `--password` is configured, credentials are transmitted as base64-encoded cleartext in every request header. Anyone performing a passive network capture on the same LAN segment can recover the password instantly.

**Recommendations**

- Add a warning to the startup banner when a password is set and no TLS is active: e.g., `[yellow]Warning:[/] Password is transmitted in cleartext over HTTP. Use on trusted networks only.`
- Long-term: add an optional `--cert <pfx>` / `--cert-key <pem>` flag to configure HTTPS on the Kestrel listener. The `TLS over self-signed cert` UX on LAN is poor, but it does protect credentials.
- Document this limitation explicitly in README under a Security section.

---

## 10. No Global Request Rate Limiting

**Severity:** Low–Medium (depends on exposure)
**Affected code:** [src/Program.cs:131–162](src/Program.cs#L131-L162) (middleware chain)

**Problem**
There is no rate limiting at the Kestrel or middleware level. An attacker can flood any endpoint — browse, download, upload — at full HTTP throughput to saturate CPU, memory, or disk I/O.

**Recommendations**

- Add `Microsoft.AspNetCore.RateLimiting` (built into .NET 7+, no extra package needed). Register a fixed-window or token-bucket policy keyed on `RemoteIpAddress`:
  ```csharp
  builder.Services.AddRateLimiter(opts =>
  {
      opts.AddFixedWindowLimiter("perIp", o =>
      {
          o.PermitLimit = 60;         // requests per window
          o.Window = TimeSpan.FromMinutes(1);
          o.QueueLimit = 0;
      });
      opts.RejectionStatusCode = 429;
  });
  app.UseRateLimiter();
  ```
- Apply the policy selectively — more permissive on `GET /browse` (browsing), stricter on `POST /upload` (I/O-intensive).
- Expose limits as CLI flags (`--rate-limit`, `--rate-window`) for flexibility.

---

## Summary

| # | Issue | Severity | Effort to Fix |
|---|---|---|---|
| 1 | Disk exhaustion via upload flooding | High | Medium |
| 2 | SSE connection exhaustion | High | Low |
| 3 | Password brute force (no lockout) | High | Low |
| 4 | Symlink / junction traversal | Medium | Low |
| 5 | Directory creation bomb | Medium | Low |
| 6 | `ResolveUniqueFileName` stat-loop | Medium | Low |
| 7 | Timing attack on password | Low | Trivial |
| 8 | No CSRF tokens | Low | Medium |
| 9 | Basic Auth over plain HTTP | Low | High (TLS) / Trivial (warning) |
| 10 | No global rate limiting | Low–Medium | Medium |

Items 2, 3, 4, 5, 6, and 7 are all low-effort changes with meaningful security impact and are recommended as a first pass.
