// ── Upload queue ──────────────────────────────────────────────────────────────
const dz    = document.getElementById('drop-zone');
const inp   = document.getElementById('file-input');
const queue = document.getElementById('upload-queue');
const form  = document.getElementById('upload-form');

// CSRF token embedded by the server — included in every state-changing request
const csrfToken = document.querySelector('meta[name="csrf-token"]')?.content ?? '';

// ── Session bearer token (cookie-free auth for QR auto-login environments) ────
// Some mobile browsers / QR-scanner webviews do not persist cookies.
// The server embeds an admin bearer token in <meta name="fb-bearer"> after QR auto-login.
// We persist it in sessionStorage so navigation within the tab keeps working even after
// the meta tag is no longer injected (subsequent page loads use the stored value).
const _metaBearer = document.querySelector('meta[name="fb-bearer"]')?.content;
if (_metaBearer) { try { sessionStorage.setItem('fb-bearer', _metaBearer); } catch {} }
const autoBearerToken = _metaBearer
  || (() => { try { return sessionStorage.getItem('fb-bearer'); } catch { return null; } })()
  || null;

/** Returns an object with an Authorization header if a session bearer token is available. */
function authHeaders() {
  return autoBearerToken ? { 'Authorization': 'Bearer ' + autoBearerToken } : {};
}

/**
 * Returns a URL with ?_bearer= appended when a session bearer token is available.
 * Used for all in-app navigation and form actions so that browser-level requests
 * carry authentication in environments without cookie support.
 */
function navUrl(href) {
  if (!autoBearerToken || !href || !href.startsWith('/')) return href;
  if (href.includes('_bearer=')) return href;
  return href + (href.includes('?') ? '&' : '?') + '_bearer=' + encodeURIComponent(autoBearerToken);
}

// Intercept all internal link-clicks and add ?_bearer= to the URL so full-page
// navigations authenticate correctly in environments where cookies are not stored.
// Public endpoints (/s/, /join/, /auto-login/) are excluded.
if (autoBearerToken) {
  document.addEventListener('click', function (e) {
    const a = e.target.closest('a[href]');
    if (!a) return;
    const href = a.getAttribute('href');
    if (!href || !href.startsWith('/')) return;
    if (/^\/(s|join|auto-login)\//.test(href)) return;
    if (href.includes('_bearer=')) return;
    e.preventDefault();
    window.location.assign(navUrl(href));
  });
}

let activeUploads = 0;
let pendingRetries = 0;
let anySucceeded  = false;

function fmtSize(b) {
  if (b < 1024)    return b + ' B';
  if (b < 1048576) return (b / 1024).toFixed(1) + ' KB';
  return (b / 1048576).toFixed(1) + ' MB';
}

function escHtml(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// ── Chunked upload threshold (50 MB) ─────────────────────────────────────────
const CHUNK_SIZE      = 50 * 1024 * 1024;  // 50 MB per chunk
const CHUNK_THRESHOLD = CHUNK_SIZE;         // files >= this size use chunked upload

function startUploads(files) {
  [...files].forEach(uploadFile);
}

function uploadFile(file) {
  if (file.size >= CHUNK_THRESHOLD) {
    uploadFileChunked(file);
  } else {
    uploadFileSimple(file);
  }
}

// ── Simple upload (small files, existing XHR + FormData path) ────────────────
function uploadFileSimple(file) {
  activeUploads++;

  const row = document.createElement('div');
  row.className = 'q-row';
  row.innerHTML =
    `<span class="q-name" title="${escHtml(file.name)}">${escHtml(file.name)}</span>` +
    `<span class="q-size">${fmtSize(file.size)}</span>` +
    `<div class="q-bar-wrap"><div class="q-bar"></div></div>` +
    `<span class="q-status">uploading…</span>` +
    `<button class="q-btn" title="Cancel">✕</button>`;
  queue.hidden = false;
  queue.appendChild(row);

  const bar    = row.querySelector('.q-bar');
  const status = row.querySelector('.q-status');
  const btn    = row.querySelector('.q-btn');

  const fd = new FormData();
  fd.append('_csrf', csrfToken);
  fd.append('files', file, file.name);

  const xhr       = new XMLHttpRequest();
  const startTime = Date.now();
  btn.addEventListener('click', () => xhr.abort());

  xhr.upload.addEventListener('progress', ev => {
    if (!ev.lengthComputable) return;
    const pct     = ev.loaded / ev.total;
    bar.style.width = Math.round(pct * 100) + '%';
    const elapsed = (Date.now() - startTime) / 1000;
    if (elapsed > 0.5 && pct > 0) {
      const speed    = ev.loaded / elapsed;          // bytes/s
      const remaining = (ev.total - ev.loaded) / speed; // seconds
      const speedStr  = fmtSize(speed) + '/s';
      const etaStr    = remaining > 1
        ? (remaining < 60 ? Math.round(remaining) + 's' : Math.round(remaining / 60) + 'm')
        : '';
      status.textContent = speedStr + (etaStr ? '  ' + etaStr : '');
    }
  });

  xhr.addEventListener('load', () => {
    activeUploads--;
    if (xhr.status < 400) {
      bar.style.width      = '100%';
      bar.style.background = '#4caf50';
      status.textContent   = '✓';
      status.className     = 'q-status q-ok';
      btn.remove();
      anySucceeded = true;
      checkDone();
    } else {
      markFailed(row, bar, status, btn, file, xhr.status);
    }
  });

  xhr.addEventListener('error', () => {
    activeUploads--;
    markFailed(row, bar, status, btn, file);
  });

  xhr.addEventListener('abort', () => {
    activeUploads--;
    status.textContent = 'cancelled';
    status.className   = 'q-status q-dim';
    btn.remove();
    checkDone();
  });

  xhr.open('POST', form.action);
  if (autoBearerToken) xhr.setRequestHeader('Authorization', 'Bearer ' + autoBearerToken);
  xhr.send(fd);
}

// ── Chunked upload (large files, fetch + Blob.slice + Content-Range) ─────────
function uploadFileChunked(file) {
  activeUploads++;

  const row = document.createElement('div');
  row.className = 'q-row';
  row.innerHTML =
    `<span class="q-name" title="${escHtml(file.name)}">${escHtml(file.name)}</span>` +
    `<span class="q-size">${fmtSize(file.size)}</span>` +
    `<div class="q-bar-wrap"><div class="q-bar"></div></div>` +
    `<span class="q-status">uploading…</span>` +
    `<button class="q-btn" title="Cancel">✕</button>`;
  queue.hidden = false;
  queue.appendChild(row);

  const bar    = row.querySelector('.q-bar');
  const status = row.querySelector('.q-status');
  const btn    = row.querySelector('.q-btn');

  const controller = new AbortController();
  btn.addEventListener('click', () => {
    controller.abort();
    activeUploads--;
    status.textContent = 'cancelled';
    status.className   = 'q-status q-dim';
    btn.remove();
    removeChunkedProgress(file.name);
    checkDone();
  });

  const startTime = Date.now();

  (async function sendChunks() {
    try {
      // Check for a previous partial upload to resume from
      let offset = loadChunkedProgress(file.name);
      if (offset > 0) {
        // Verify with server that the .part file still exists at that offset
        const headRes = await fetch(form.action + '?file=' + encodeURIComponent(file.name), {
          method: 'HEAD',
          headers: { ...authHeaders(), 'X-CSRF-Token': csrfToken },
          signal: controller.signal
        });
        if (headRes.ok) {
          const serverBytes = parseInt(headRes.headers.get('X-Bytes-Received') || '0', 10);
          offset = serverBytes; // trust the server's count
        } else {
          offset = 0; // .part file gone — start over
        }
      }

      while (offset < file.size) {
        const end   = Math.min(offset + CHUNK_SIZE, file.size);
        const chunk = file.slice(offset, end);
        const rangeHeader = `bytes ${offset}-${end - 1}/${file.size}`;

        const res = await fetch(form.action, {
          method: 'POST',
          headers: {
            ...authHeaders(),
            'Content-Type': 'application/octet-stream',
            'X-Content-Range': rangeHeader,
            'X-Upload-Filename': file.name,
            'X-CSRF-Token': csrfToken
          },
          body: chunk,
          signal: controller.signal
        });

        if (!res.ok) {
          // Save progress for resume and mark as failed
          saveChunkedProgress(file.name, offset);
          activeUploads--;
          markFailed(row, bar, status, btn, file, res.status);
          return;
        }

        offset = end;
        saveChunkedProgress(file.name, offset);

        // Update progress bar
        const pct = offset / file.size;
        bar.style.width = Math.round(pct * 100) + '%';
        const elapsed = (Date.now() - startTime) / 1000;
        if (elapsed > 0.5 && pct > 0) {
          const speed     = offset / elapsed;
          const remaining = (file.size - offset) / speed;
          const speedStr  = fmtSize(speed) + '/s';
          const etaStr    = remaining > 1
            ? (remaining < 60 ? Math.round(remaining) + 's' : Math.round(remaining / 60) + 'm')
            : '';
          status.textContent = speedStr + (etaStr ? '  ' + etaStr : '');
        }
      }

      // Upload complete
      activeUploads--;
      bar.style.width      = '100%';
      bar.style.background = '#4caf50';
      status.textContent   = '✓';
      status.className     = 'q-status q-ok';
      btn.remove();
      removeChunkedProgress(file.name);
      anySucceeded = true;
      checkDone();
    } catch (err) {
      if (err.name === 'AbortError') return; // cancel already handled
      saveChunkedProgress(file.name, loadChunkedProgress(file.name));
      activeUploads--;
      markFailed(row, bar, status, btn, file);
    }
  })();
}

// ── Chunked upload progress persistence (localStorage) ───────────────────────
const PROGRESS_KEY = 'fb-chunked-uploads';

function loadAllChunkedProgress() {
  try { return JSON.parse(localStorage.getItem(PROGRESS_KEY) || '{}'); }
  catch { return {}; }
}

function saveChunkedProgress(fileName, bytesUploaded) {
  try {
    const all = loadAllChunkedProgress();
    all[fileName] = { bytes: bytesUploaded, ts: Date.now(), action: form?.action || '' };
    localStorage.setItem(PROGRESS_KEY, JSON.stringify(all));
  } catch { /* localStorage full or unavailable */ }
}

function loadChunkedProgress(fileName) {
  const all = loadAllChunkedProgress();
  const entry = all[fileName];
  if (!entry) return 0;
  // Expire entries older than 1 hour (matches server-side .part cleanup)
  if (Date.now() - entry.ts > 3600000) {
    removeChunkedProgress(fileName);
    return 0;
  }
  return entry.bytes || 0;
}

function removeChunkedProgress(fileName) {
  try {
    const all = loadAllChunkedProgress();
    delete all[fileName];
    localStorage.setItem(PROGRESS_KEY, JSON.stringify(all));
  } catch { /* ignore */ }
}

// ── Resume interrupted chunked uploads on page load ──────────────────────────
(function checkPendingResumes() {
  if (!form) return;
  const all = loadAllChunkedProgress();
  const currentAction = form.action;
  for (const [fileName, entry] of Object.entries(all)) {
    if (!entry.bytes || entry.bytes <= 0) { removeChunkedProgress(fileName); continue; }
    if (Date.now() - entry.ts > 3600000) { removeChunkedProgress(fileName); continue; }
    if (entry.action && entry.action !== currentAction) continue; // different upload path
    // Show a resume prompt in the upload queue
    const row = document.createElement('div');
    row.className = 'q-row';
    row.innerHTML =
      `<span class="q-name" title="${escHtml(fileName)}">${escHtml(fileName)}</span>` +
      `<span class="q-size">${fmtSize(entry.bytes)} uploaded</span>` +
      `<div class="q-bar-wrap"><div class="q-bar" style="width:0%;background:#ff9800"></div></div>` +
      `<span class="q-status q-dim">interrupted</span>` +
      `<button class="q-btn" title="Resume">↺</button>`;
    queue.hidden = false;
    queue.appendChild(row);
    const resumeBtn = row.querySelector('.q-btn');
    resumeBtn.addEventListener('click', () => {
      row.remove();
      // User must re-select the file (browser security prevents reading files from a previous session)
      const picker = document.createElement('input');
      picker.type = 'file';
      picker.addEventListener('change', () => {
        if (picker.files.length === 0) return;
        const f = picker.files[0];
        if (f.name !== fileName) {
          alert('Please select the same file: ' + fileName);
          return;
        }
        uploadFileChunked(f);
      });
      picker.click();
    });
  }
})();

function markFailed(row, bar, status, btn, file, httpStatus = 0) {
  pendingRetries++;
  bar.style.background = '#f44336';
  status.textContent   = httpStatus === 507 ? 'disk full' : 'failed';
  status.className     = 'q-status q-err';
  const newBtn = btn.cloneNode(false);
  newBtn.textContent = '↺';
  newBtn.title       = 'Retry';
  btn.replaceWith(newBtn);
  newBtn.addEventListener('click', () => {
    pendingRetries--;
    row.remove();
    uploadFile(file);
  });
  checkDone();
}

function checkDone() {
  if (activeUploads === 0 && pendingRetries === 0 && anySucceeded)
    setTimeout(() => window.location.reload(), 2500);
}

// Upload form / drop-zone event listeners (null-guarded: absent in ReadOnly mode)
if (dz) {
  dz.addEventListener('dragover',  e => { e.preventDefault(); dz.classList.add('dragover'); });
  dz.addEventListener('dragleave', () => dz.classList.remove('dragover'));
  dz.addEventListener('drop', e => {
    e.preventDefault();
    dz.classList.remove('dragover');
    startUploads(e.dataTransfer.files);
  });
}
if (inp) {
  inp.addEventListener('change', () => {
    if (inp.files.length === 0) return;
    startUploads(inp.files);
    inp.value = '';
  });
}
if (form) {
  form.addEventListener('submit', e => {
    e.preventDefault();
    if (inp.files.length === 0) return;
    startUploads(inp.files);
    inp.value = '';
  });
}

// ── Table-level drop zone (covers the whole directory listing) ────────────────
const _table = document.querySelector('table');
if (_table) {
  _table.addEventListener('dragover', e => {
    e.preventDefault();
    _table.classList.add('drag-active');
  });
  _table.addEventListener('dragleave', e => {
    if (!_table.contains(e.relatedTarget)) _table.classList.remove('drag-active');
  });
  _table.addEventListener('drop', e => {
    e.preventDefault();
    _table.classList.remove('drag-active');

    if (!form) {
      alert('ReadOnly mode — uploads are disabled.');
      return;
    }

    // Detect directory items
    const items = [...(e.dataTransfer.items || [])];
    const hasDir = items.some(it => it.kind === 'file' && it.webkitGetAsEntry?.()?.isDirectory);
    if (hasDir) {
      alert('Directory upload is not supported. Please select individual files.');
      return;
    }

    startUploads(e.dataTransfer.files);
  });
}

// ── Clipboard paste to upload ─────────────────────────────────────────────────
document.addEventListener('paste', e => {
  if (!form) return; // ReadOnly mode — uploads disabled

  const items = [...(e.clipboardData?.items || [])];
  const imageItem = items.find(it => it.kind === 'file' && it.type.startsWith('image/'));

  if (!imageItem) {
    // Non-image content — silently ignore (text pasting is expected in inputs)
    return;
  }

  e.preventDefault();
  const blob = imageItem.getAsFile();
  if (!blob) return;

  const now = new Date();
  const ts  = now.getFullYear() + '-' +
              String(now.getMonth() + 1).padStart(2,'0') + '-' +
              String(now.getDate()).padStart(2,'0') + '-' +
              String(now.getHours()).padStart(2,'0') +
              String(now.getMinutes()).padStart(2,'0') +
              String(now.getSeconds()).padStart(2,'0');
  const ext     = blob.type.split('/')[1]?.replace('jpeg','jpg') || 'png';
  const defName = `paste-${ts}.${ext}`;

  const name = prompt('Save pasted image as:', defName);
  if (!name || !name.trim()) return;

  const namedFile = new File([blob], name.trim(), { type: blob.type });
  uploadFile(namedFile);
});

// ── File actions (delete / rename) ───────────────────────────────────────────
function fbDelete(url, name) {
  if (!confirm(`Delete "${name}"? This cannot be undone.`)) return;
  const f = document.createElement('form');
  f.method = 'post'; f.action = navUrl(url);
  const csrf = document.createElement('input');
  csrf.type = 'hidden'; csrf.name = '_csrf'; csrf.value = csrfToken;
  f.appendChild(csrf);
  document.body.appendChild(f);
  f.submit();
}

function fbRename(url, currentName) {
  const newName = prompt('Rename to:', currentName);
  if (!newName || newName === currentName) return;
  const f = document.createElement('form');
  f.method = 'post'; f.action = navUrl(url);
  const csrf = document.createElement('input');
  csrf.type = 'hidden'; csrf.name = '_csrf'; csrf.value = csrfToken;
  f.appendChild(csrf);
  const inp = document.createElement('input');
  inp.type = 'hidden'; inp.name = 'newname'; inp.value = newName;
  f.appendChild(inp);
  document.body.appendChild(f);
  f.submit();
}

// ── File preview panel ────────────────────────────────────────────────────────
const _previewPanel    = document.getElementById('preview-panel');
const _previewContent  = document.getElementById('preview-content');
const _previewTitle    = document.getElementById('preview-title');
const _previewDownload = document.getElementById('preview-download');

const IMG_EXTS   = new Set(['.jpg','.jpeg','.png','.gif','.webp','.svg']);
const TEXT_EXTS  = new Set(['.txt','.md','.log','.csv','.json','.xml','.yaml','.yml','.toml','.ini','.sh','.bat','.ps1','.cs','.js','.ts','.py','.go','.rs','.html','.css']);
const VIDEO_EXTS = new Set(['.mp4','.webm']);
const PDF_EXTS   = new Set(['.pdf']);

function fbPreview(url, name, ext) {
  _previewTitle.textContent = name;
  _previewDownload.href     = url;
  _previewContent.innerHTML = '';

  if (IMG_EXTS.has(ext)) {
    const img = document.createElement('img');
    img.src = url; img.alt = name;
    _previewContent.appendChild(img);
  } else if (VIDEO_EXTS.has(ext)) {
    const v = document.createElement('video');
    v.src = url; v.controls = true;
    _previewContent.appendChild(v);
  } else if (PDF_EXTS.has(ext)) {
    const fr = document.createElement('iframe');
    fr.src = url;
    _previewContent.appendChild(fr);
  } else if (TEXT_EXTS.has(ext)) {
    fetch(url, { headers: authHeaders() }).then(r => r.text()).then(text => {
      const pre = document.createElement('pre');
      pre.textContent = text;
      _previewContent.appendChild(pre);
    }).catch(() => {
      _previewContent.innerHTML = '<p style="color:#888">Could not load file.</p>';
    });
  } else {
    _previewContent.innerHTML =
      `<p style="color:#888">Preview not available. <a href="${url}">Download</a> the file instead.</p>`;
  }
  _previewPanel.hidden = false;
}

function _closePreview() {
  _previewPanel.hidden = true;
  _previewContent.innerHTML = '';
  _previewContent.style.justifyContent = '';
  if (_previewDownload) _previewDownload.hidden = false;
}

async function fbInfo(url, name) {
  _previewTitle.textContent = name;
  if (_previewDownload) _previewDownload.hidden = true;
  _previewContent.style.justifyContent = 'flex-start';
  _previewContent.innerHTML = '<p style="color:#888;font-size:0.9rem">Computing…</p>';
  _previewPanel.hidden = false;
  try {
    const res = await fetch(url, { headers: authHeaders() });
    if (!res.ok) { _previewContent.innerHTML = '<p style="color:#e06c75">Could not load file info (status ' + res.status + ').</p>'; return; }
    const { name: n, size, modified, mimeType, sha256 } = await res.json();
    const tdL = 'style="color:#888;padding:0.4rem 1rem 0.4rem 0;white-space:nowrap;vertical-align:top"';
    const tdR = 'style="word-break:break-all"';
    _previewTitle.textContent = n;
    _previewContent.innerHTML =
      '<table style="border-collapse:collapse;width:100%;font-size:0.9rem">' +
        '<tr><td ' + tdL + '>Name</td><td ' + tdR + '>' + escHtml(n) + '</td></tr>' +
        '<tr><td ' + tdL + '>Size</td><td ' + tdR + '>' + escHtml(size) + '</td></tr>' +
        '<tr><td ' + tdL + '>Modified</td><td ' + tdR + '>' + escHtml(modified) + '</td></tr>' +
        '<tr><td ' + tdL + '>Type</td><td ' + tdR + '>' + escHtml(mimeType) + '</td></tr>' +
        '<tr><td ' + tdL + '>SHA-256</td><td ' + tdR + '><code style="font-size:0.8rem">' + escHtml(sha256) + '</code></td></tr>' +
      '</table>';
  } catch (e) { _previewContent.innerHTML = '<p style="color:#e06c75">Error loading file info: ' + escHtml(e.message) + '</p>'; }
}
document.getElementById('preview-close').addEventListener('click', _closePreview);
document.getElementById('preview-backdrop').addEventListener('click', _closePreview);
document.addEventListener('keydown', e => { if (e.key === 'Escape') _closePreview(); });

// ── Share link ────────────────────────────────────────────────────────────────
async function fbShare(url) {
  const fd = new FormData();
  fd.append('_csrf', csrfToken);
  try {
    const res = await fetch(url, { method: 'POST', body: fd, headers: authHeaders() });
    if (!res.ok) { alert('Failed to create share link (status ' + res.status + ')'); return; }
    const { url: shareUrl, expiresIn } = await res.json();
    const full = location.origin + shareUrl;
    const hours = Math.round(expiresIn / 3600 * 10) / 10;
    prompt(`Share link (expires in ${hours}h):`, full);
  } catch (e) {
    alert('Error creating share link: ' + e.message);
  }
}

// ── New folder ────────────────────────────────────────────────────────────────
function fbMkDir(pathPrefix) {
  const name = prompt('New folder name:');
  if (!name || !name.trim()) return;
  const folderName = name.trim();
  const base = pathPrefix ? pathPrefix + '/' : '';
  const url = '/mkdir/' + base + encodeURIComponent(folderName);
  const f = document.createElement('form');
  f.method = 'post'; f.action = navUrl(url);
  const csrf = document.createElement('input');
  csrf.type = 'hidden'; csrf.name = '_csrf'; csrf.value = csrfToken;
  f.appendChild(csrf);
  document.body.appendChild(f);
  f.submit();
}

// ── Disk space indicator ──────────────────────────────────────────────────────
const _diskAbort = new AbortController();
(async function loadDiskInfo() {
  const el = document.getElementById('disk-info');
  if (!el) return;
  try {
    const r = await fetch('/disk-space', { signal: _diskAbort.signal, headers: authHeaders() });
    if (!r.ok) return; // 204 No Content (virtual/network drive) — hide silently
    const { availableBytes, totalBytes } = await r.json();
    if (!totalBytes) return;
    const usedPct = (totalBytes - availableBytes) / totalBytes;
    const color   = usedPct > 0.9 ? '#f44336' : usedPct > 0.8 ? '#ff9800' : '#4caf50';
    const fmtGB   = b => (b / 1073741824).toFixed(1) + ' GB';
    el.innerHTML =
      `<span>${fmtGB(availableBytes)} free</span>` +
      `<div id="disk-track"><div id="disk-fill" style="width:${Math.round(usedPct * 100)}%;background:${color}"></div></div>` +
      `<span style="color:${color}">${Math.round(usedPct * 100)}%</span>`;
  } catch { /* ignore — disk info is best-effort */ }
})();

// ── Live reload via Server-Sent Events ────────────────────────────────────────
// EventSource cannot set custom headers; pass the bearer token as a query param instead.
let _sseSource = null;
(function connectSSE() {
  const eventsUrl = autoBearerToken
    ? '/events?_bearer=' + encodeURIComponent(autoBearerToken)
    : '/events';
  const es = _sseSource = new EventSource(eventsUrl);
  es.onmessage = (e) => {
    if (e.data === 'reload') window.location.reload();
  };
  es.onerror = () => {
    // Connection dropped (e.g. server restarted) — retry after 3 s
    es.close();
    setTimeout(connectSSE, 3000);
  };
})();

// Close persistent connections when navigating away so the browser's
// HTTP/1.1 connection pool (≈6 per host) is freed for the next page.
window.addEventListener('pagehide', () => {
  _diskAbort.abort();
  if (_sseSource) { _sseSource.close(); _sseSource = null; }
});

// ── Bulk select + download/delete ─────────────────────────────────────────────
(function () {
  const table   = document.querySelector('table[data-bulk-dl]');
  if (!table) return;                                   // not a directory view

  const bulkDlEndpoint  = table.dataset.bulkDl  || '';
  const bulkDelEndpoint = table.dataset.bulkDel || '';
  const toolbar    = document.getElementById('bulk-toolbar');
  const countLabel = document.getElementById('bulk-count-n');
  const dlBtn      = document.getElementById('bulk-dl-btn');
  const delBtn     = document.getElementById('bulk-del-btn');
  const selectAll  = document.getElementById('select-all');

  // Hide delete button when the endpoint is absent (non-admin)
  if (!bulkDelEndpoint && delBtn) delBtn.style.display = 'none';

  function getChecked() {
    return Array.from(table.querySelectorAll('.file-select:checked'));
  }

  function updateToolbar() {
    const checked = getChecked();
    const n = checked.length;
    countLabel.textContent = n;
    toolbar.classList.toggle('active', n > 0);

    // Update select-all indeterminate state
    const all = table.querySelectorAll('.file-select');
    if (all.length === 0) {
      selectAll.indeterminate = false;
      selectAll.checked = false;
    } else if (n === 0) {
      selectAll.indeterminate = false;
      selectAll.checked = false;
    } else if (n === all.length) {
      selectAll.indeterminate = false;
      selectAll.checked = true;
    } else {
      selectAll.indeterminate = true;
    }
  }

  // Delegate checkbox changes inside the table
  table.addEventListener('change', function (e) {
    if (e.target.classList.contains('file-select') || e.target.id === 'select-all') {
      if (e.target.id === 'select-all') {
        const checked = e.target.checked;
        table.querySelectorAll('.file-select').forEach(cb => { cb.checked = checked; });
      }
      updateToolbar();
    }
  });

  // Bulk download as ZIP
  if (dlBtn) dlBtn.addEventListener('click', async function () {
    const paths = getChecked().map(cb => cb.dataset.path);
    if (!paths.length) return;
    try {
      const res = await fetch(bulkDlEndpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken, ...authHeaders() },
        body: JSON.stringify({ paths })
      });
      if (!res.ok) { alert('Download failed: ' + res.status); return; }
      const blob = await res.blob();
      const url  = URL.createObjectURL(blob);
      const a    = document.createElement('a');
      a.href = url; a.download = 'selection.zip';
      document.body.appendChild(a); a.click();
      setTimeout(() => { document.body.removeChild(a); URL.revokeObjectURL(url); }, 1000);
    } catch (err) {
      alert('Download error: ' + err.message);
    }
  });

  // Bulk delete
  if (delBtn && bulkDelEndpoint) delBtn.addEventListener('click', async function () {
    const paths = getChecked().map(cb => cb.dataset.path);
    if (!paths.length) return;
    if (!confirm(`Delete ${paths.length} file(s)? This cannot be undone.`)) return;
    try {
      const res = await fetch(bulkDelEndpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken, ...authHeaders() },
        body: JSON.stringify({ paths })
      });
      if (!res.ok) { alert('Delete request failed: ' + res.status); return; }
      const data = await res.json();
      if (data.failed > 0) {
        alert(`Deleted ${data.deleted} file(s). ${data.failed} failed:\n` + data.errors.join('\n'));
      }
      if (data.deleted > 0) window.location.reload();
    } catch (err) {
      alert('Delete error: ' + err.message);
    }
  });
})();
