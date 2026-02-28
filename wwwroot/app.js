// ── Upload drop-zone ──────────────────────────────────────────────────────────
const dz       = document.getElementById('drop-zone');
const inp      = document.getElementById('file-input');
const list     = document.getElementById('file-list');
const form     = document.getElementById('upload-form');
const progWrap = document.getElementById('progress-wrap');
const progBar  = document.getElementById('progress-bar');

function updateList(files) {
  list.textContent = [...files].map(f => f.name).join(', ');
}

inp.addEventListener('change', () => updateList(inp.files));

dz.addEventListener('dragover', e => { e.preventDefault(); dz.classList.add('dragover'); });
dz.addEventListener('dragleave', () => dz.classList.remove('dragover'));
dz.addEventListener('drop', e => {
  e.preventDefault();
  dz.classList.remove('dragover');
  inp.files = e.dataTransfer.files;
  updateList(inp.files);
  form.requestSubmit(); // auto-submit immediately after drop
});

// ── XHR upload with progress bar ─────────────────────────────────────────────
form.addEventListener('submit', e => {
  e.preventDefault();
  if (inp.files.length === 0) return;

  const xhr = new XMLHttpRequest();
  progWrap.hidden = false;
  progBar.style.width = '0%';

  xhr.upload.addEventListener('progress', ev => {
    if (!ev.lengthComputable) return;
    progBar.style.width = Math.round((ev.loaded / ev.total) * 100) + '%';
  });

  xhr.addEventListener('load', () => window.location.reload());
  xhr.addEventListener('error', () => {
    progWrap.hidden = true;
    alert('Upload failed. Please try again.');
  });

  xhr.open('POST', form.action);
  xhr.send(new FormData(form));
});

// ── File actions (delete / rename) ───────────────────────────────────────────
function fbDelete(url, name) {
  if (!confirm(`Delete "${name}"? This cannot be undone.`)) return;
  const f = document.createElement('form');
  f.method = 'post'; f.action = url;
  document.body.appendChild(f);
  f.submit();
}

function fbRename(url, currentName) {
  const newName = prompt('Rename to:', currentName);
  if (!newName || newName === currentName) return;
  const f = document.createElement('form');
  f.method = 'post'; f.action = url;
  const inp = document.createElement('input');
  inp.type = 'hidden'; inp.name = 'newname'; inp.value = newName;
  f.appendChild(inp);
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
