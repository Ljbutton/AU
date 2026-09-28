namespace TournamentTracker.Overlay
{
    /// <summary>
    /// The OBS browser-source page. Transparent background; pick panels with
    /// ?show=players,standings,feed and add &amp;full=1 for roles, kills and tasks (delayed
    /// streams only: it gives the game away to anyone watching live).
    /// </summary>
    public static class OverlayPage
    {
        public const string Html = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>Tournament overlay</title>
<style>
:root{--panel:rgba(12,16,22,.78);--line:rgba(255,255,255,.12);--fg:#f3f5f7;--muted:#9aa6b2;--imp:#ff5a4e;--good:#4ade80;--accent:#5eead4}
*{box-sizing:border-box}
html,body{margin:0;background:transparent;color:var(--fg);font:600 16px/1.3 ""Segoe UI"",system-ui,-apple-system,sans-serif;text-shadow:0 1px 2px rgba(0,0,0,.6)}
.wrap{display:flex;flex-direction:column;gap:10px;width:330px;padding:12px}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:10px 12px;backdrop-filter:blur(4px)}
.head{font-size:13px;color:var(--muted);letter-spacing:.04em;text-transform:uppercase}
.head b{color:var(--fg)}
.phase{display:inline-block;margin-top:4px;font-size:12px;padding:2px 8px;border-radius:99px;background:rgba(94,234,212,.16);color:var(--accent)}
.players{display:grid;gap:5px}
.p{display:grid;grid-template-columns:18px 1fr auto;gap:8px;align-items:center}
.bean{width:16px;height:18px;border-radius:8px 8px 5px 5px;border:1.5px solid rgba(0,0,0,.55);position:relative}
.bean::after{content:"""";position:absolute;right:-1px;top:4px;width:8px;height:5px;border-radius:3px;background:#bfe3f2;border:1px solid rgba(0,0,0,.45)}
.p.dead{opacity:.45}.p.dead .name{text-decoration:line-through}
.name{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.tag{font-size:11px;padding:1px 6px;border-radius:6px;background:rgba(255,90,78,.2);color:var(--imp)}
.bar{height:4px;border-radius:2px;background:rgba(255,255,255,.12);grid-column:2/4;overflow:hidden}.bar i{display:block;height:100%;background:var(--good)}
table{width:100%;border-collapse:collapse;font-size:14px}
td{padding:2px 0}td.n{text-align:right;font-variant-numeric:tabular-nums;color:var(--muted);width:2.4em;padding-right:8px}td.pts{text-align:right;font-variant-numeric:tabular-nums;width:3.5em}
tr.cut td{border-top:1px dashed var(--accent)}
.feed{display:grid;gap:4px;font-size:13.5px;font-weight:500}
.feed .t{color:var(--muted);font-variant-numeric:tabular-nums;margin-right:6px}
.title{font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.05em;margin-bottom:6px}
[hidden]{display:none!important}
</style></head><body>
<div class=""wrap"">
  <div class=""panel"" id=""top""><div class=""head"" id=""head"">Waiting for the game…</div><span class=""phase"" id=""phase"" hidden></span></div>
  <div class=""panel"" id=""players-panel""><div class=""title"">Players</div><div class=""players"" id=""players""></div></div>
  <div class=""panel"" id=""standings-panel""><div class=""title"" id=""st-title"">Standings</div><table id=""standings""></table></div>
  <div class=""panel"" id=""feed-panel""><div class=""title"">Latest</div><div class=""feed"" id=""feed""></div></div>
</div>
<script>
const COLORS=['#c51111','#132ed1','#117f2d','#ed54ba','#ef7d0d','#f5f557','#3f474e','#d6e0f0','#6b2fbb','#71491e','#38fedc','#50ef39','#5f1d2e','#ecc0d3','#fffebe','#708496','#928776','#ec7578'];
const q=new URLSearchParams(location.search);
const show=(q.get('show')||'players,standings,feed').split(',');
const full=q.get('full')==='1';
for(const k of ['players','standings','feed'])document.getElementById(k+'-panel').hidden=!show.includes(k);
const esc=s=>String(s).replace(/[&<>""]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;'}[c]));
const PHASES={Lobby:'In the lobby',Tasks:'Game in progress',Meeting:'Meeting',GameOver:'Game over',Menu:'Offline'};
async function tick(){
  try{
    const s=await (await fetch('/state'+(full?'?full=1':''),{cache:'no-store'})).json();
    if(!s.lobby&&!s.tournament)return;
    document.getElementById('head').innerHTML=[s.tournament&&`<b>${esc(s.tournament)}</b>`,s.round?`Round ${s.round}`:'',s.lobby&&`${esc(s.lobby)} lobby`,s.played!=null&&s.perRound?`Game ${Math.min(s.played+(s.phase==='Tasks'||s.phase==='Meeting'?1:0),s.perRound)} of ${s.perRound}`:''].filter(Boolean).join(' · ');
    const ph=document.getElementById('phase');ph.hidden=!s.phase;ph.textContent=PHASES[s.phase]||s.phase;
    document.getElementById('players').innerHTML=(s.players||[]).map(p=>`<div class=""p${p.dead?' dead':''}""><span class=""bean"" style=""background:${COLORS[p.color]||'#888'}""></span><span class=""name"">${esc(p.name)}</span>${p.impostor?'<span class=""tag"">IMP</span>':'<span></span>'}${p.tasks?`<div class=""bar""><i style=""width:${Math.round(100*p.tasks[0]/Math.max(1,p.tasks[1]))}%""></i></div>`:''}</div>`).join('');
    document.getElementById('st-title').textContent=s.standingsTitle||'Standings';
    document.getElementById('standings').innerHTML=(s.standings||[]).map((r,i)=>`<tr class=""${s.advance&&i===s.advance?'cut':''}""><td class=""n"">${i+1}.</td><td>${esc(r.name)}</td><td class=""pts"">${r.points}</td></tr>`).join('');
    document.getElementById('feed').innerHTML=(s.feed||[]).slice(-6).map(e=>`<div><span class=""t"">${e.at}</span>${esc(e.text)}</div>`).join('');
  }catch(e){}
}
tick();setInterval(tick,1000);
</script></body></html>";
    }
}
