// ── Upload queue ──────────────────────────────────────────────────────────────
const dz    = document.getElementById('drop-zone');
const inp   = document.getElementById('file-input');
const queue = document.getElementById('upload-queue');
const form  = document.getElementById('upload-form');

// CSRF token embedded by the server — included in every state-changing request
const csrfToken = document.querySelector('meta[name="csrf-token"]')?.content ?? '';

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

function startUploads(files) {
  [...files].forEach(uploadFile);
}

function uploadFile(file) {
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
  xhr.send(fd);
}

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
  f.method = 'post'; f.action = url;
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
  f.method = 'post'; f.action = url;
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
    fetch(url).then(r => r.text()).then(text => {
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
}
document.getElementById('preview-close').addEventListener('click', _closePreview);
document.getElementById('preview-backdrop').addEventListener('click', _closePreview);
document.addEventListener('keydown', e => { if (e.key === 'Escape') _closePreview(); });

// ── Share link ────────────────────────────────────────────────────────────────
async function fbShare(url) {
  const fd = new FormData();
  fd.append('_csrf', csrfToken);
  try {
    const res = await fetch(url, { method: 'POST', body: fd });
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
  f.method = 'post'; f.action = url;
  const csrf = document.createElement('input');
  csrf.type = 'hidden'; csrf.name = '_csrf'; csrf.value = csrfToken;
  f.appendChild(csrf);
  document.body.appendChild(f);
  f.submit();
}

// ── Disk space indicator ──────────────────────────────────────────────────────
(async function loadDiskInfo() {
  const el = document.getElementById('disk-info');
  if (!el) return;
  try {
    const r = await fetch('/disk-space');
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
(function connectSSE() {
  const es = new EventSource('/events');
  es.onmessage = (e) => {
    if (e.data === 'reload') window.location.reload();
  };
  es.onerror = () => {
    // Connection dropped (e.g. server restarted) — retry after 3 s
    es.close();
    setTimeout(connectSSE, 3000);
  };
})();
