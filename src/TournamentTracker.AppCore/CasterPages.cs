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
:root{--bg:#0b0e13;--fg:#f3f5f7;--muted:#9aa6b2;--good:#46c28b;--bad:#ff6b6b;--line:#252c36}
*{box-sizing:border-box}
html,body{margin:0;height:100%;background:var(--bg);color:var(--fg);font:15px/1.4 ""Segoe UI"",system-ui,sans-serif}
body{display:grid;grid-template-rows:auto 1fr auto auto}
header,footer{padding:10px 16px}
header b{color:#fff}
iframe{border:0;width:100%;height:100%;background:#000}
footer{color:var(--muted);font-size:13px;display:flex;gap:14px}
.ok{color:var(--good)}.bad{color:var(--bad)}
#voice{padding:10px 16px;border-top:1px solid var(--line);display:flex;flex-wrap:wrap;gap:10px 22px;align-items:center;font-size:13.5px}
#voice .src{display:flex;align-items:center;gap:8px}
#voice .meter{width:90px;height:8px;border-radius:4px;background:#1b222b;overflow:hidden}
#voice .meter i{display:block;height:100%;width:0;background:var(--good);transition:width .15s}
#voice input[type=range]{width:110px}
#voice .st{font-weight:600}
</style></head><body>
<header id=""howto"">Sending your game to the caster. Below, press the share button and pick <b>Entire screen</b>. Then leave this tab open while you play.</header>
<iframe id=""v"" allow=""camera;microphone;display-capture;autoplay;fullscreen;clipboard-write""></iframe>
<div id=""voice"">
  <span class=""st"" id=""vstate"">Lobby voice: starting…</span>
  <span class=""src"">Discord <span class=""meter""><i id=""mv""></i></span><input type=""range"" id=""lv"" min=""0"" max=""150"" title=""Lobby voice level""></span>
  <span class=""src"">Among Us <span class=""meter""><i id=""mg""></i></span><input type=""range"" id=""lg"" min=""0"" max=""150"" title=""Game sound level""></span>
  <label class=""src""><input type=""checkbox"" id=""mic""> Include my microphone</label>
  <label class=""src""><input type=""checkbox"" id=""von""> Send lobby voice</label>
</div>
<footer><span id=""link"">Connecting to Among Us…</span><span id=""data""></span></footer>
<script src=""https://unpkg.com/@vdoninja/sdk/vdoninja-sdk.min.js""></script>
<script>
const token=new URLSearchParams(location.search).get('token')||'';
const q=p=>p+(p.includes('?')?'&':'?')+'token='+encodeURIComponent(token);
const v=document.getElementById('v');
let pushUrl=null,since=null,sent=0,lastOk=0,lobby=null;
async function info(){
  try{
    const r=await (await fetch(q('/app/sendinfo'),{cache:'no-store'})).json();
    if(r.pushUrl&&r.pushUrl!==pushUrl){pushUrl=r.pushUrl;v.src=pushUrl;publishVoice();}
    document.getElementById('link').textContent=pushUrl?'Video link ready.':'Waiting for Among Us with ""Send my game to the caster"" on.';
  }catch(e){}
}
function send(items){ if(v.contentWindow) v.contentWindow.postMessage({sendData:{tt:items},type:'pcs'},'*'); }
async function pump(){
  try{
    const d=await (await fetch(q('/app/sendfeed?since='+(since??0)),{cache:'no-store'})).json();
    if(d.last<0)return;
    if(since===null||d.last<since){since=d.last;return;}   // start from now (and again if Among Us restarted)
    since=d.last;
    if(d.items&&d.items.length&&v.contentWindow){
      // VDO.Ninja's iframe API: sends to everyone viewing this stream (only the caster has the password).
      for(const it of d.items) if(it.lobby) lobby=it.lobby;
      send(d.items);
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
  if(got&&got.ttn&&typeof got.ttn==='object'){fetch(q('/app/names'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({names:got.ttn})}).catch(()=>{});return;}
  if(typeof cmd!=='string'||!/^spec [a-z]+( [a-z0-9.]+)?$/.test(cmd))return;
  fetch(q('/app/command'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:cmd})}).catch(()=>{});
});

// ---- Lobby voice (Part 11) -------------------------------------------------------------------
// The Button captures Discord's sound (the lobby as you hear it, never your own voice) and Among Us's
// sound separately; this page mixes them (plus your microphone if you want) and sends the mix to the
// caster as its own stream next to your screen. Nothing is played back here or to the players.
let ctx=null,node=null,dest=null,micNode=null,micStream=null,vdo=null,publishing=null,voiceProblem='',VS=null,noticeSent=false;
const WORKLET=`class TTMix extends AudioWorkletProcessor{constructor(){super();this.q=[];this.off=0;this.buffered=0;this.started=false;
  this.port.onmessage=e=>{this.q.push(e.data);this.buffered+=e.data.length/2;while(this.buffered>48000*0.6&&this.q.length>1){const d=this.q.shift();this.buffered-=(d.length-this.off)/2;this.off=0;}};}
  process(_,outs){const L=outs[0][0],R=outs[0][1]||outs[0][0];
    if(!this.started){if(this.buffered<48000*0.12){L.fill(0);R.fill(0);return true;}this.started=true;}
    for(let i=0;i<L.length;i++){
      if(!this.q.length){L[i]=R[i]=0;this.started=false;continue;}
      const d=this.q[0];L[i]=d[this.off]/32768;R[i]=d[this.off+1]/32768;this.off+=2;this.buffered--;
      if(this.off>=d.length){this.q.shift();this.off=0;}}
    return true;}}
registerProcessor('tt-mix',TTMix);`;
async function audio(){
  if(ctx)return;
  ctx=new AudioContext({sampleRate:48000});
  await ctx.audioWorklet.addModule(URL.createObjectURL(new Blob([WORKLET],{type:'text/javascript'})));
  node=new AudioWorkletNode(ctx,'tt-mix',{numberOfInputs:0,outputChannelCount:[2]});
  dest=ctx.createMediaStreamDestination();
  node.connect(dest);                       // only to the stream: never to the speakers
  pull();
}
async function pull(){
  for(;;){
    try{
      if(VS&&VS.on){
        const b=await (await fetch(q('/app/voice/pcm'),{cache:'no-store'})).arrayBuffer();
        if(b.byteLength>=4) node.port.postMessage(new Int16Array(b));
      }
    }catch(e){}
    await new Promise(r=>setTimeout(r,40));
  }
}
async function mic(on){
  if(on&&!micNode){
    try{ micStream=await navigator.mediaDevices.getUserMedia({audio:{echoCancellation:true,noiseSuppression:true}}); micNode=ctx.createMediaStreamSource(micStream); micNode.connect(dest); }
    catch(e){ voiceProblem='Microphone blocked: '+e.message; document.getElementById('mic').checked=false; }
  } else if(!on&&micNode){ micNode.disconnect(); micStream.getTracks().forEach(t=>t.stop()); micNode=null; micStream=null; }
}
async function publishVoice(){
  if(!pushUrl||!VS||!VS.on||publishing)return;
  const u=new URL(pushUrl),id=u.searchParams.get('push'),pw=u.searchParams.get('password');
  if(!id||!pw)return;
  publishing=(async()=>{
    try{
      await audio();
      if(ctx.state!=='running') await ctx.resume().catch(()=>{});
      if(!window.VDONinjaSDK) throw new Error('the VDO.Ninja SDK did not load');
      if(vdo){ try{ await vdo.disconnect(); }catch(e){} }
      vdo=new VDONinjaSDK({password:pw});
      await vdo.connect();
      await vdo.publish(dest.stream,{streamID:id+'v',label:'Lobby voice'});
      voiceProblem='';
      if(!noticeSent){ noticeSent=true; fetch(q('/app/command'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:'voicenotice'})}).catch(()=>{}); }
    }catch(e){ voiceProblem='Lobby voice not sent: '+e.message; vdo=null; }
    publishing=null;
  })();
}
const pct=db=>Math.max(0,Math.min(100,(db+60)/60*100));
const STATE={capturing:'on','not running':'not open',unsupported:'needs Windows 10 2004 or later',off:'off'};
async function voiceTick(){
  try{
    const r=await (await fetch(q('/app/voice'),{cache:'no-store'})).json();
    const first=!VS; VS=r;
    const s=r.state;
    if(first){ document.getElementById('lv').value=Math.round(s.voiceLevel*100); document.getElementById('lg').value=Math.round(s.gameLevel*100); document.getElementById('mic').checked=r.mic; document.getElementById('von').checked=r.on; }
    document.getElementById('mv').style.width=pct(s.voice.level)+'%';
    document.getElementById('mg').style.width=pct(s.game.level)+'%';
    const working=r.on&&s.supported&&s.voice.state==='capturing';
    const st=document.getElementById('vstate');
    st.textContent=!r.on?'Lobby voice: off':!s.supported?'Lobby voice needs Windows 10 2004 or later':voiceProblem||`Lobby voice: Discord ${STATE[s.voice.state]||s.voice.state} · Among Us ${STATE[s.game.state]||s.game.state}${vdo?' · sending':''}`;
    st.className='st '+(voiceProblem||!working&&r.on?'bad':vdo?'ok':'');
    // With The Button sending the game sound, the screen share must not carry it too.
    document.getElementById('howto').innerHTML=r.on&&s.supported
      ?'Sending your game to the caster. Below, press the share button, pick <b>Entire screen</b> and leave <b>Share system audio</b> unticked: The Button sends the game sound and the lobby voice itself. Then leave this tab open while you play.'
      :'Sending your game to the caster. Below, press the share button, pick <b>Entire screen</b> and tick <b>Share system audio</b> (so the caster gets the game sound). Then leave this tab open while you play.';
    if(ctx) await mic(r.on&&r.mic);
    if(r.on&&!vdo) publishVoice();
    // The caster's tab shows this lobby's voice status.
    if(lobby) send([{type:'voice',lobby,t:Date.now(),on:r.on,sending:!!vdo,problem:voiceProblem||null,discord:s.voice.state,game:s.game.state,voiceDb:s.voice.level,gameDb:s.game.level,mic:!!micNode}]);
  }catch(e){}
}
function save(body){ fetch(q('/app/voice'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}).then(voiceTick); }
document.getElementById('lv').oninput=e=>save({voiceLevel:e.target.value/100});
document.getElementById('lg').oninput=e=>save({gameLevel:e.target.value/100});
document.getElementById('mic').onchange=e=>save({mic:String(e.target.checked)});
document.getElementById('von').onchange=e=>{ if(!e.target.checked&&vdo){ try{vdo.disconnect();}catch(x){} vdo=null; } save({on:String(e.target.checked)}); };
// Browsers start audio only after a click on the page.
addEventListener('pointerdown',()=>{ if(ctx&&ctx.state!=='running') ctx.resume(); },{capture:true});
info();setInterval(info,5000);setInterval(pump,500);voiceTick();setInterval(voiceTick,1000);
</script></body></html>";
    
        /// <summary>
        /// A stand-in for a lobby's voice in simulation mode (?lobby=LJ): soft blips at a pitch of
        /// its own, so in OBS you can hear the voice follow the picture without real games.
        /// </summary>
        public const string SimVoice = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>Simulated lobby voice</title></head><body style=""margin:0;background:transparent"">
<script>
const lobby=new URLSearchParams(location.search).get('lobby')||'?';
let h=0;for(const c of lobby)h=(h*31+c.charCodeAt(0))%1000;
const base=220+h%5*55;
const ctx=new AudioContext();
function blip(){
  const o=ctx.createOscillator(),g=ctx.createGain();
  o.frequency.value=base*(1+Math.floor(Math.random()*4)/4);
  g.gain.setValueAtTime(0,ctx.currentTime);g.gain.linearRampToValueAtTime(0.08,ctx.currentTime+0.03);g.gain.linearRampToValueAtTime(0,ctx.currentTime+0.25);
  o.connect(g).connect(ctx.destination);o.start();o.stop(ctx.currentTime+0.3);
}
setInterval(()=>{ if(ctx.state!=='running')ctx.resume(); if(Math.random()<0.6)blip(); },400);
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
.badge.montage{background:#ffc15a;color:#16120a}
.badge.montage .dot{background:#16120a}
.dot{width:16px;height:16px;border-radius:50%;background:#fff;animation:p 1.1s infinite}
@keyframes p{50%{opacity:.25}}
.what{background:rgba(5,7,11,.72);padding:10px 18px;border-radius:12px;font-weight:600;font-size:30px;text-shadow:0 2px 6px #000}
.sw{display:inline-block;width:.8em;height:.8em;border-radius:4px;margin:0 .3em -.06em 0;border:2px solid rgba(255,255,255,.55)}
.by{background:rgba(5,7,11,.72);padding:8px 16px;border-radius:12px;font-size:22px;font-weight:600;color:#cfd6e2;display:flex;align-items:center;gap:12px;letter-spacing:.06em}
.by b{color:#fff;font-size:28px;letter-spacing:0}
.by img{height:44px;max-width:180px;object-fit:contain}
</style></head><body>
<div class=""tag"" id=""tag""><div class=""badge"" id=""badge""><span class=""dot""></span><span id=""label"">REPLAY</span></div><div class=""what"" id=""what""></div><div class=""by"" id=""by"" hidden></div></div>
<script>
const CREW=['#c51111','#132ed1','#117f2d','#ed54ba','#ef7d0d','#f5f557','#3f474e','#d6e0f0','#6b2fbb','#71491e','#38fedc','#50ef39','#5f1d2e','#ecc0d3','#f0e7a8','#758593','#918877','#d76464'];
const esc=s=>String(s??'').replace(/[&<>""]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;'}[c]));
const rich=s=>esc(s).replace(/\[\[(\d+)\|([^\]]*)\]\]/g,(m,c,n)=>`<i class=""sw"" style=""background:${CREW[+c]||'#888'}""></i>${n}`);
async function tick(){
  try{
    const d=await (await fetch('/replaynow',{cache:'no-store'})).json();
    document.getElementById('tag').classList.toggle('on',!!d.on);
    if(!d.on)return;
    const kind=d.kind||'replay';
    document.getElementById('label').textContent=kind==='montage'?'MONTAGE':kind==='killcam'?'KILL CAM':'REPLAY';
    document.getElementById('badge').className='badge '+kind;
    document.getElementById('what').innerHTML=kind==='montage'?rich(d.title):(d.lobby?esc(d.lobby)+' · ':'')+rich(d.title);
    const by=document.getElementById('by');
    by.hidden=!d.sponsor;
    if(d.sponsor) by.innerHTML=`${kind==='montage'?'PRESENTED BY':'PRESENTED BY'} ${d.sponsorLogo?`<img src=""${esc(d.sponsorLogo)}"" alt="""">`:''}<b>${esc(d.sponsor)}</b>`;
  }catch(e){}
}
tick();setInterval(tick,400);
</script></body></html>";
    }
}
