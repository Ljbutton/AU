// What both apps' pages share: The Button (ui/index.html) and Red Alert (ui/caster.html) load this
// before their own script, which uses these helpers.
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const head = c => `/crew/${c >= 0 && c < 18 ? c : 15}.png`;
function toast(text) { const t = $('toast'); t.textContent = text; t.classList.add('show'); clearTimeout(t._h); t._h = setTimeout(() => t.classList.remove('show'), 4200); }

// ---- Window: the header is the title bar (drag, double-click to maximise) --------------------
(() => {
  const host = window.chrome && window.chrome.webview;
  if (!host) return;
  $('winctl').hidden = false;
  document.querySelector('header').addEventListener('mousedown', e => {
    if (e.button !== 0 || e.target.closest('button,a,input,select,textarea,.alert')) return;
    e.preventDefault();
    host.postMessage('drag');
  });
  $('winctl').addEventListener('click', e => { const b = e.target.closest('[data-win]'); if (b) host.postMessage(b.dataset.win); });
  host.addEventListener('message', e => {
    if (typeof e.data === 'string' && e.data.startsWith('maximized:')) {
      const on = e.data === 'maximized:true';
      document.body.classList.toggle('maxed', on);
      document.querySelector('[data-win="max"]').title = on ? 'Restore' : 'Maximise';
    }
  });
})();
