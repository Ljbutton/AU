namespace TournamentTracker.App
{
    /// <summary>
    /// The caster's OBS pages for the hosts' game video (sent through VDO.Ninja):
    /// "/video" is the lobby being cast, full frame, switching the moment another lobby is
    /// picked (every lobby stays connected in the background so there's no wait), and
    /// "/multiview" is every lobby at once, RedZone style. Both read "/feeds".
    /// </summary>
    public static class CasterPages
    {
        private const string Head = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>Caster video</title>
<style>
:root{--bg:#0b0e13;--fg:#f3f5f7;--muted:#9aa6b2;--line:rgba(255,255,255,.14);--accent:#5eead4;--hot:#ff5a4e}
*{box-sizing:border-box}
html,body{margin:0;height:100%;background:var(--bg);color:var(--fg);font:600 16px/1.3 ""Segoe UI"",system-ui,-apple-system,sans-serif;overflow:hidden}
iframe{border:0;width:100%;height:100%;display:block;background:#000}
.wait{position:absolute;inset:0;display:grid;place-items:center;text-align:center;color:var(--muted);font-size:22px}
.wait b{display:block;color:var(--fg);font-size:30px;margin-bottom:6px}
[hidden]{display:none!important}
</style>";

        /// <summary>The lobby being cast, full frame. OBS Browser source 1920×1080.</summary>
        public const string Video = Head + @"
<style>
.frame{position:absolute;inset:0;opacity:0;pointer-events:none;transition:opacity .15s}
.frame.on{opacity:1}
</style></head><body>
<div class=""wait"" id=""wait""><div><b>Waiting for the game</b><span id=""wait-line""></span></div></div>
<script>
// Every lobby's video stays connected so switching is instant; only the cast one shows.
const frames={};
async function tick(){
  let d;
  try{d=await (await fetch('/feeds',{cache:'no-store'})).json();}catch(e){return;}
  const seen=new Set();
  for(const l of d.lobbies||[]){
    if(!l.video)continue;
    seen.add(l.label);
    let f=frames[l.label];
    if(!f||f.dataset.src!==l.video){
      if(f)f.remove();
      f=document.createElement('iframe');
      f.className='frame';f.allow='autoplay;fullscreen';f.dataset.src=l.video;f.src=l.video;
      document.body.appendChild(f);frames[l.label]=f;
    }
  }
  for(const k of Object.keys(frames))if(!seen.has(k)){frames[k].remove();delete frames[k];}
  for(const [k,f] of Object.entries(frames))f.classList.toggle('on',k===d.cast);
  const on=d.cast&&frames[d.cast];
  document.getElementById('wait').hidden=!!on;
  document.getElementById('wait-line').textContent=d.cast?d.cast+"" isn't sending their game yet."":""Pick a lobby in The Button's Organiser tab."";
}
tick();setInterval(tick,700);
</script></body></html>";

        /// <summary>Every lobby at once. OBS Browser source 1920×1080.</summary>
        public const string Multiview = Head + @"
<style>
.grid{display:grid;gap:6px;padding:6px;height:100%}
.tile{position:relative;border:3px solid var(--line);border-radius:10px;overflow:hidden;background:#000;min-height:0}
.tile.cast{border-color:var(--accent)}
.tile.hot{border-color:var(--hot)}
.tile .bar{position:absolute;left:0;right:0;bottom:0;display:flex;gap:10px;align-items:center;padding:6px 10px;background:linear-gradient(transparent,rgba(0,0,0,.85));text-shadow:0 1px 2px #000;font-size:18px}
.tile .bar .ph{font-size:13px;color:var(--muted);text-transform:uppercase;letter-spacing:.05em}
.tile .bar .tag{margin-left:auto;font-size:13px;padding:2px 8px;border-radius:99px;background:var(--hot);color:#fff}
.tile .bar .tag.cast{background:var(--accent);color:#06201c}
.tile .none{position:absolute;inset:0;display:grid;place-items:center;color:var(--muted)}
</style></head><body>
<div class=""wait"" id=""wait""><div><b>No lobbies yet</b>Hosts appear here once their game is running.</div></div>
<div class=""grid"" id=""grid""></div>
<script>
const PH={Lobby:'Lobby',Tasks:'Playing',Meeting:'Meeting',GameOver:'Game over',Menu:'Offline'};
const tiles={};
const esc=s=>String(s).replace(/[&<>""]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;'}[c]));
async function tick(){
  let d;
  try{d=await (await fetch('/feeds',{cache:'no-store'})).json();}catch(e){return;}
  const list=(d.lobbies||[]);
  const grid=document.getElementById('grid');
  document.getElementById('wait').hidden=list.length>0;
  const n=Math.max(1,list.length),cols=n<=1?1:n<=4?2:n<=9?3:4,rows=Math.ceil(n/cols);
  grid.style.gridTemplateColumns=`repeat(${cols},minmax(0,1fr))`;grid.style.gridTemplateRows=`repeat(${rows},minmax(0,1fr))`;
  const seen=new Set();
  list.forEach((l,i)=>{
    seen.add(l.label);
    let t=tiles[l.label];
    if(!t){t=document.createElement('div');t.className='tile';t.innerHTML='<div class=""none"">No video</div><div class=""bar""></div>';grid.appendChild(t);tiles[l.label]=t;}
    const f=t.querySelector('iframe');
    if(l.video&&(!f||f.dataset.src!==l.video)){
      if(f)f.remove();
      const nf=document.createElement('iframe');nf.allow='autoplay';nf.dataset.src=l.video;nf.src=l.video;t.prepend(nf);
    }else if(!l.video&&f)f.remove();
    t.querySelector('.none').hidden=!!l.video;
    t.style.order=i;
    t.classList.toggle('cast',l.label===d.cast);
    t.classList.toggle('hot',!!l.hot&&l.label!==d.cast);
    t.querySelector('.bar').innerHTML=`<span>${esc(l.label)}</span><span class=""ph"">${esc(PH[l.phase]||l.phase||'')}${!l.total?'':l.phase==='Tasks'||l.phase==='Meeting'?` · ${l.alive}/${l.total} alive`:` · ${l.total} players`}</span>`+
      (l.label===d.cast?'<span class=""tag cast"">On air</span>':l.hot?`<span class=""tag"">${esc(l.hot)}</span>`:'');
  });
  for(const k of Object.keys(tiles))if(!seen.has(k)){tiles[k].remove();delete tiles[k];}
}
tick();setInterval(tick,1000);
</script></body></html>";
    }
}
