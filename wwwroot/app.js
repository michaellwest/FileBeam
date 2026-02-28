// ── Upload drop-zone ──────────────────────────────────────────────────────────
const dz   = document.getElementById('drop-zone');
const inp  = document.getElementById('file-input');
const list = document.getElementById('file-list');

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
});

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
