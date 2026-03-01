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

  const xhr = new XMLHttpRequest();
  btn.addEventListener('click', () => xhr.abort());

  xhr.upload.addEventListener('progress', ev => {
    if (!ev.lengthComputable) return;
    bar.style.width = Math.round(ev.loaded / ev.total * 100) + '%';
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
      markFailed(row, bar, status, btn, file);
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

function markFailed(row, bar, status, btn, file) {
  pendingRetries++;
  bar.style.background = '#f44336';
  status.textContent   = 'failed';
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
    setTimeout(() => window.location.reload(), 800);
}

dz.addEventListener('dragover',  e => { e.preventDefault(); dz.classList.add('dragover'); });
dz.addEventListener('dragleave', () => dz.classList.remove('dragover'));
dz.addEventListener('drop', e => {
  e.preventDefault();
  dz.classList.remove('dragover');
  startUploads(e.dataTransfer.files);
});

form.addEventListener('submit', e => {
  e.preventDefault();
  if (inp.files.length === 0) return;
  startUploads(inp.files);
  inp.value = '';
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
