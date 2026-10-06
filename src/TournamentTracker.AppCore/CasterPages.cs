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

        /// <summary>
        /// The lobby being cast, full frame, with its game sound. OBS Browser source 1920×1080
        /// (tick "Control audio via OBS" to mix it). ?sound=0 for picture only.
        /// </summary>
        public const string Video = Head + @"
<style>
.frame{position:absolute;inset:0;opacity:0;pointer-events:none;transition:opacity .15s}
.frame.on{opacity:1}
.sound{position:absolute;left:0;top:0;width:2px;height:2px;opacity:0;pointer-events:none}
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
  playSound(d);
  document.getElementById('wait-line').textContent=d.cast?d.cast+"" isn't sending their game yet."":""Pick a lobby in The Button's Organiser tab."";
}
tick();setInterval(tick,700);
</script>
<script>
// The game sound of the lobby on air, and only that one.
const soundOn=new URLSearchParams(location.search).get('sound')!=='0';
let soundFrame=null;
function playSound(d){
  const cast=(d.lobbies||[]).find(l=>l.label===d.cast);
  const src=soundOn&&cast&&cast.sound||null;
  if((soundFrame&&soundFrame.dataset.src)===src)return;
  if(soundFrame){soundFrame.remove();soundFrame=null;}
  if(!src)return;
  soundFrame=document.createElement('iframe');
  soundFrame.className='sound';soundFrame.allow='autoplay';soundFrame.dataset.src=src;soundFrame.src=src;
  document.body.appendChild(soundFrame);
}
</script></body></html>";

        /// <summary>Every lobby at once, silent (?sound=1 plays the lobby on air). OBS Browser source 1920×1080.</summary>
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
.sound{position:absolute;left:0;top:0;width:2px;height:2px;opacity:0;pointer-events:none}
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
  if(soundOn)playSound(d);
}
tick();setInterval(tick,1000);
</script>
<script>
// Silent unless ?sound=1: the video page normally carries the sound, and two would double it.
const soundOn=new URLSearchParams(location.search).get('sound')==='1';
let soundFrame=null;
function playSound(d){
  const cast=(d.lobbies||[]).find(l=>l.label===d.cast);
  const src=cast&&cast.sound||null;
  if((soundFrame&&soundFrame.dataset.src)===src)return;
  if(soundFrame){soundFrame.remove();soundFrame=null;}
  if(!src)return;
  soundFrame=document.createElement('iframe');
  soundFrame.className='sound';soundFrame.allow='autoplay';soundFrame.dataset.src=src;soundFrame.src=src;
  document.body.appendChild(soundFrame);
}
</script></body></html>";
    
        /// <summary>
        /// The host's "send to the caster" page, opened by The Button in their browser: VDO.Ninja
        /// shares the screen inside it, and the lobby's live data (events and snapshots from the
        /// mod) rides along on the same private connection, so only the caster, who has the
        /// password, ever receives it. Needs The Button's token (in the link it opens).
        /// </summary>
        public const string Send = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>Sending to the caster</title>
<style>
:root{--bg:#0b0e13;--fg:#f3f5f7;--muted:#9aa6b2;--good:#46c28b;--bad:#ff6b6b}
*{box-sizing:border-box}
html,body{margin:0;height:100%;background:var(--bg);color:var(--fg);font:15px/1.4 ""Segoe UI"",system-ui,sans-serif}
body{display:grid;grid-template-rows:auto 1fr auto}
header,footer{padding:10px 16px}
header b{color:#fff}
iframe{border:0;width:100%;height:100%;background:#000}
footer{color:var(--muted);font-size:13px;display:flex;gap:14px}
.ok{color:var(--good)}.bad{color:var(--bad)}
</style></head><body>
<header>Sending your game to the caster. Below, press the share button, pick <b>Entire screen</b> and tick <b>Share system audio</b>. Then leave this tab open while you play.</header>
<iframe id=""v"" allow=""camera;microphone;display-capture;autoplay;fullscreen;clipboard-write""></iframe>
<footer><span id=""link"">Connecting to Among Us…</span><span id=""data""></span></footer>
<script>
const token=new URLSearchParams(location.search).get('token')||'';
const v=document.getElementById('v');
let pushUrl=null,since=null,sent=0,lastOk=0;
async function info(){
  try{
    const r=await (await fetch('/app/sendinfo?token='+encodeURIComponent(token),{cache:'no-store'})).json();
    if(r.pushUrl&&r.pushUrl!==pushUrl){pushUrl=r.pushUrl;v.src=pushUrl;}
    document.getElementById('link').textContent=pushUrl?'Video link ready.':'Waiting for Among Us with ""Send my game to the caster"" on.';
  }catch(e){}
}
async function pump(){
  try{
    const d=await (await fetch('/app/sendfeed?since='+(since??0)+'&token='+encodeURIComponent(token),{cache:'no-store'})).json();
    if(d.last<0)return;
    if(since===null||d.last<since){since=d.last;return;}   // start from now (and again if Among Us restarted)
    since=d.last;
    if(d.items&&d.items.length&&v.contentWindow){
      // VDO.Ninja's iframe API: sends to everyone viewing this stream (only the caster has the password).
      v.contentWindow.postMessage({sendData:{tt:d.items},type:'pcs'},'*');
      sent+=d.items.length;lastOk=Date.now();
    }
  }catch(e){}
  const el=document.getElementById('data');
  el.textContent=lastOk?`Live data: ${sent} sent`:'';el.className=lastOk&&Date.now()-lastOk<5000?'ok':'';
}
// The caster switches this lobby's spectator view (lit map, vision, ""!"", eye): only ""spec …"" commands are taken.
addEventListener('message',e=>{
  if(e.source!==v.contentWindow)return;
  const got=e.data&&e.data.dataReceived;const cmd=got&&got.ttc;
  // Roster names for this lobby's players, for the referee's nameplates.
  if(got&&got.ttn&&typeof got.ttn==='object'){fetch('/app/names?token='+encodeURIComponent(token),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({names:got.ttn})}).catch(()=>{});return;}
  if(typeof cmd!=='string'||!/^spec [a-z]+( [a-z0-9.]+)?$/.test(cmd))return;
  fetch('/app/command?token='+encodeURIComponent(token),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:cmd})}).catch(()=>{});
});
info();setInterval(info,5000);setInterval(pump,500);
</script></body></html>";
    
        /// <summary>
        /// A stand-in for a lobby's video in simulation mode (?lobby=LJ): big lobby name, a clock
        /// and moving crewmates, so switching in OBS can be checked without real games.
        /// </summary>
        public const string SimFeed = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>Simulated lobby</title>
<style>
html,body{margin:0;height:100%;overflow:hidden;background:#05070b;color:#fff;font:700 16px ""Segoe UI"",system-ui,sans-serif}
.grid{position:absolute;inset:0;background-image:linear-gradient(rgba(255,255,255,.05) 1px,transparent 1px),linear-gradient(90deg,rgba(255,255,255,.05) 1px,transparent 1px);background-size:120px 120px}
.name{position:absolute;left:0;right:0;top:38%;text-align:center;font-size:180px;letter-spacing:.04em;text-shadow:0 6px 30px rgba(0,0,0,.6)}
.sub{position:absolute;left:0;right:0;top:64%;text-align:center;font-size:40px;color:#9aa6b2}
.dot{position:absolute;width:70px;height:90px;border-radius:40px 40px 18px 18px}
</style></head><body><div class=""grid""></div><div class=""name"" id=""n""></div><div class=""sub"" id=""t""></div>
<script>
const lobby=new URLSearchParams(location.search).get('lobby')||'Lobby';
document.getElementById('n').textContent=lobby;
const cols=['#c51111','#132ed1','#117f2d','#ed54ba','#ef7d0d','#f5f557','#3f474e','#d6e0f0','#6b2fbb','#71491e'];
let seed=[...lobby].reduce((a,c)=>a*31+c.charCodeAt(0),7);const r=()=>(seed=seed*16807%2147483647)/2147483647;
const dots=cols.map(c=>{const d=document.createElement('div');d.className='dot';d.style.background=c;document.body.appendChild(d);return {d,x:r()*1800,y:r()*950,vx:(r()-.5)*4,vy:(r()-.5)*4};});
function f(){for(const o of dots){o.x+=o.vx;o.y+=o.vy;if(o.x<0||o.x>1850)o.vx*=-1;if(o.y<0||o.y>990)o.vy*=-1;o.d.style.transform=`translate(${o.x}px,${o.y}px)`;}
document.getElementById('t').textContent='SIMULATED · '+new Date().toLocaleTimeString();requestAnimationFrame(f);}
f();
</script></body></html>";
    
        /// <summary>
        /// The REPLAY tag over replays in OBS (TT Replay scene, transparent): a pulsing REPLAY badge
        /// and what happened. Reads /replaynow.
        /// </summary>
        public const string ReplayTag = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>Replay tag</title>
<style>
html,body{margin:0;height:100%;background:transparent;overflow:hidden;font:700 34px/1.2 ""Segoe UI"",system-ui,sans-serif;color:#fff}
.tag{position:absolute;left:48px;top:40px;display:flex;align-items:center;gap:18px;opacity:0;transform:translateX(-30px);transition:opacity .35s,transform .35s}
.tag.on{opacity:1;transform:none}
.badge{background:#ff2d55;padding:10px 22px 10px 18px;border-radius:12px;letter-spacing:.12em;display:flex;align-items:center;gap:12px;box-shadow:0 6px 24px rgba(0,0,0,.45)}
.dot{width:16px;height:16px;border-radius:50%;background:#fff;animation:p 1.1s infinite}
@keyframes p{50%{opacity:.25}}
.what{background:rgba(5,7,11,.72);padding:10px 18px;border-radius:12px;font-weight:600;font-size:30px;text-shadow:0 2px 6px #000}
</style></head><body>
<div class=""tag"" id=""tag""><div class=""badge""><span class=""dot""></span>REPLAY</div><div class=""what"" id=""what""></div></div>
<script>
async function tick(){
  try{
    const d=await (await fetch('/replaynow',{cache:'no-store'})).json();
    document.getElementById('tag').classList.toggle('on',!!d.on);
    document.getElementById('what').textContent=d.on?`${d.lobby} · ${d.title}`:'';
    document.getElementById('what').hidden=!d.on;
  }catch(e){}
}
tick();setInterval(tick,400);
</script></body></html>";
    }
}
