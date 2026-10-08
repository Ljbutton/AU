namespace TournamentTracker.App
{
    /// <summary>The host's side of the broadcast feed (docs/broadcast-protocol.md).</summary>
    public static class SendPage
    {
        /// <summary>
        /// The host's "send to the caster" page, opened by The Button in their browser: VDO.Ninja
        /// shares the screen inside it, and the lobby's live data (events and snapshots from the
        /// mod) rides along on the same private connection, so only the caster, who has the
        /// password, ever receives it. Needs The Button's token (in the link it opens).
        /// </summary>
        public const string Html = @"<!doctype html>
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
#drop{display:none;padding:9px 16px;background:#5a1f24;color:#fff;font-weight:600}
#drop.on{display:block}
#gone{display:none;padding:16px;background:#8a1520;color:#fff;font-weight:700;font-size:18px;text-align:center}
#gone.on{display:block}
body.dropped{grid-template-rows:auto auto 1fr auto auto}
</style></head><body>
<div id=""gone"">Live game data isn't reaching The Button. If The Button restarted (an update), click <u>Open again</u> in The Button (Home → Send my game to the caster), then close this tab. The video still goes out.</div>
<div id=""drop"">Disconnected from caster, reconnecting… Keep playing: nothing is lost, it's all sent when the link is back.</div>
<header id=""howto"">Sending your game to the caster. Below, press the share button and pick <b>Entire screen</b>. Then leave this tab open while you play.</header>
<iframe id=""v"" allow=""camera;microphone;display-capture;autoplay;fullscreen;clipboard-write""></iframe>
<div id=""voice"">
  <span class=""st"" id=""vstate"">Lobby voice: starting…</span>
  <span class=""src"">Discord <span class=""meter""><i id=""mv""></i></span><input type=""range"" id=""lv"" min=""0"" max=""150"" title=""Lobby voice level""></span>
  <span class=""src"">Among Us <span class=""meter""><i id=""mg""></i></span><input type=""range"" id=""lg"" min=""0"" max=""150"" title=""Game sound level""></span>
  <label class=""src""><input type=""checkbox"" id=""mic""> Include my microphone</label>
  <label class=""src""><input type=""checkbox"" id=""von""> Send lobby voice</label>
</div>
<footer><span id=""link"">Connecting to Among Us…</span><span id=""data""></span><span id=""cam""></span></footer>
<script src=""https://unpkg.com/@vdoninja/sdk/vdoninja-sdk.min.js""></script>
<script>
const token=new URLSearchParams(location.search).get('token')||'';
const PROTOCOL=1;   // the broadcast feed's version (FeedProtocol.Version, docs/broadcast-protocol.md)
const q=p=>p+(p.includes('?')?'&':'?')+'token='+encodeURIComponent(token);
const v=document.getElementById('v');
let pushUrl=null,since=null,sent=0,lastOk=0,lobby=null,twitch=null;
// Reaching The Button: a 401 means it restarted with a new link (an old version), anything else that it's
// closed or busy. Never silent: the footer says so after 5 s, and the caster is told (health: data 'lost').
let feedFailSince=0,buttonGone=false;
async function getJson(path){
  const r=await fetch(q(path),{cache:'no-store'});
  if(r.status===401||r.status===403){buttonGone=true;throw new Error('The Button restarted');}
  if(!r.ok)throw new Error('HTTP '+r.status);
  return r.json();
}
function feedOk(){feedFailSince=0;buttonGone=false;}
function feedFailed(){if(!feedFailSince)feedFailSince=Date.now();}
const feedLost=()=>buttonGone||(feedFailSince&&Date.now()-feedFailSince>5000);
async function info(){
  try{
    const r=await getJson('/app/sendinfo');
    feedOk();
    twitch=r.twitch||null;
    if(r.pushUrl&&r.pushUrl!==pushUrl){pushUrl=r.pushUrl;v.src=pushUrl;publishVoice();}
    const wantCam=!!(r.cam&&pushUrl);
    if(wantCam!==camOn){camOn=wantCam;if(!camOn)stopCam();}
    if(camOn)publishCam();
    const ce=document.getElementById('cam');ce.textContent=!camOn?'':camProblem||(camVdo?`Player camera: sending (${camFrames} pictures)`:'Player camera: starting…');ce.className=camProblem?'bad':camVdo?'ok':'';
    document.getElementById('link').textContent=pushUrl?'Video link ready.':'Waiting for Among Us with ""Send my game to the caster"" on.';
  }catch(e){feedFailed();}
}
function send(items){ if(v.contentWindow) v.contentWindow.postMessage({sendData:{tt:items},type:'pcs'},'*'); }
// Part 22: every message from Among Us is kept until the caster says it arrived, and sent again (with its
// own time, marked as sent again) while it hasn't, less often the longer the link is down. Old snapshots and
// positions are no use late, so only a tiny stand-in for each is kept; the events all are.
let queue=[],acked=0,everAcked=false,resendAt=0,resendWait=1000;
function trim(){
  const now=Date.now();
  for(let i=0;i<queue.length;i++){const q=queue[i];if(!q.stub&&(q.it.type==='snap'||q.it.type==='track')&&now-q.first>15000){q.it={v:PROTOCOL,type:'skip',lobby:q.it.lobby,src:q.it.src,seq:q.it.seq,t:q.it.t};q.stub=true;}}
  if(queue.length>3000)queue.splice(0,queue.length-3000);
}
function resend(){
  const now=Date.now();
  if(!queue.length){resendWait=1000;return;}
  if(now<resendAt)return;
  trim();
  const old=queue.filter(q=>now-q.sent>1500);
  for(let i=0;i<old.length;i+=40){send(old.slice(i,i+40).map(q=>Object.assign({},q.it,{re:1})));}
  old.forEach(q=>q.sent=now);
  resendAt=now+resendWait;resendWait=Math.min(30000,resendWait*2);
}
async function pump(){
  try{
    const d=await getJson('/app/sendfeed?since='+(since??0));
    feedOk();
    if(d.last<0)return;
    if(since===null||d.last<since){since=d.last;return;}   // start from now (and again if Among Us restarted)
    since=d.last;
    if(d.items&&d.items.length&&v.contentWindow){
      // VDO.Ninja's iframe API: sends to everyone viewing this stream (only the caster has the password).
      const now=Date.now();
      for(const it of d.items){ if(it.lobby) lobby=it.lobby; if(it.src&&it.seq) queue.push({it,first:now,sent:now}); }
      send(d.items);
      sent+=d.items.length;lastOk=Date.now();
    }
  }catch(e){feedFailed();}
  resend();
  const lost=feedLost();
  document.getElementById('gone').classList.toggle('on',!!lost);
  // The banner: only after the caster has been connected, when something has waited over 5 seconds.
  const waiting=queue.length&&Date.now()-queue[0].first>5000;
  const down=everAcked&&waiting;
  document.getElementById('drop').classList.toggle('on',down);document.body.classList.toggle('dropped',down);
  const el=document.getElementById('data');
  el.textContent=lost?'Live data: not reaching The Button':!lastOk?'':down?`Live data: ${queue.length} waiting to send`:everAcked?`Live data: ${sent} sent`:`Live data: ${sent} sent · waiting for the caster to connect`;el.className=lost?'bad':lastOk&&!down&&Date.now()-lastOk<5000?'ok':down?'bad':'';
}
function ack(a){
  // {run of the mod: last message the caster has}
  if(!a||typeof a!=='object')return;
  const before=queue.length;
  queue=queue.filter(x=>!(x.it.src in a)||x.it.seq>a[x.it.src]);
  everAcked=true;acked=Date.now();
  if(queue.length<before){resendWait=1000;resendAt=0;}
}
// Is the screen share sending pictures? Asked of VDO.Ninja every 2 s; told to the caster with the rest.
let frames=-1,stillFor=0,video='unknown';
function askStats(){ if(v.contentWindow) v.contentWindow.postMessage({getStats:true},'*'); }
function gotStats(st){
  const out=st&&st.outbound?Object.values(st.outbound).filter(Boolean):[];
  if(!out.length){video='unknown';frames=-1;stillFor=0;return;}
  const f=Math.max(...out.map(o=>o._framesEncoded||0)),kbps=Math.max(...out.map(o=>o.video_bitrate_kbps||0));
  if(f>frames||kbps>0){stillFor=0;video='ok';}else if(++stillFor>=2)video='lost';
  frames=f;
}
function health(){ if(lobby) send([{v:PROTOCOL,type:'health',lobby,t:Date.now(),video,data:feedLost()?'lost':'ok',queued:queue.length},{v:PROTOCOL,type:'host',lobby,t:Date.now(),twitch}]); }
setInterval(()=>{askStats();health();},2000);
// The caster switches this lobby's spectator view (lit map, vision, ""!"", eye): only ""spec …"" commands are taken.
addEventListener('message',e=>{
  if(e.source!==v.contentWindow)return;
  if(e.data&&e.data.stats){gotStats(e.data.stats);return;}
  const got=e.data&&e.data.dataReceived;const cmd=got&&got.ttc;
  if(got&&got.ttack){ack(got.ttack);return;}
  // Roster names for this lobby's players, for the referee's nameplates.
  if(got&&got.ttn&&typeof got.ttn==='object'){fetch(q('/app/names'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({names:got.ttn})}).catch(()=>{});return;}
  if(typeof cmd!=='string'||!/^spec [a-z]+( [a-z0-9.]+)?$/.test(cmd))return;
  fetch(q('/app/command'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:cmd})}).catch(()=>{});
});

// ---- Lobby voice (Part 11) -------------------------------------------------------------------
// The Button captures Discord's sound (the lobby as you hear it, never your own voice) and Among Us's
// sound separately; this page mixes them (plus your microphone if you want) and sends the mix to the
// caster as its own stream next to your screen. Nothing is played back here or to the players.
let ctx=null,node=null,dest=null,micNode=null,micStream=null,vdo=null,publishing=null,voiceProblem='',VS=null,noticeSent=false,retryAt=0,retryWait=5000;
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
  if(!pushUrl||!VS||!VS.on||publishing||Date.now()<retryAt)return;
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
      voiceProblem='';retryWait=5000;
      // Dropped later: try again (5 s, 10 s … 1 min apart).
      try{ vdo.addEventListener&&vdo.addEventListener('disconnected',()=>{ vdo=null; voiceProblem='Lobby voice dropped: reconnecting…'; }); }catch(x){}
      if(!noticeSent){ noticeSent=true; fetch(q('/app/command'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:'voicenotice'})}).catch(()=>{}); }
    }catch(e){ voiceProblem='Lobby voice not sent: '+e.message+' (trying again)'; vdo=null; retryAt=Date.now()+retryWait; retryWait=Math.min(60000,retryWait*2); }
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
    if(lobby) send([{v:PROTOCOL,type:'voice',lobby,t:Date.now(),on:r.on,sending:!!vdo,problem:voiceProblem||null,discord:s.voice.state,game:s.game.state,voiceDb:s.voice.level,gameDb:s.game.level,mic:!!micNode}]);
  }catch(e){}
}
function save(body){ fetch(q('/app/voice'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}).then(voiceTick); }
document.getElementById('lv').oninput=e=>save({voiceLevel:e.target.value/100});
document.getElementById('lg').oninput=e=>save({gameLevel:e.target.value/100});
document.getElementById('mic').onchange=e=>save({mic:String(e.target.checked)});
document.getElementById('von').onchange=e=>{ if(!e.target.checked&&vdo){ try{vdo.disconnect();}catch(x){} vdo=null; } save({on:String(e.target.checked)}); };
// ---- The player camera ----------------------------------------------------------------------------
// Red Alert can turn on a second picture from your game that follows one player up close (you don't
// pick who, the caster does). A worker gets each picture from The Button (workers aren't slowed down
// while this tab is in the background) and it goes to the caster as its own stream. Nothing shows here.
let camOn=false,camVdo=null,camPublishing=null,camWorker=null,camWriter=null,camCanvas=null,camRetryAt=0,camProblem='',camFrames=0;
const CAM_WORKER=`let on=false,after=0,base='',token='';
onmessage=e=>{const d=e.data;if(d.base){base=d.base;token=d.token;}if('on' in d){const was=on;on=d.on;if(on&&!was)loop();}};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
async function loop(){
  while(on){
    try{
      const r=await fetch(base+'/app/cam?token='+encodeURIComponent(token)+'&after='+after,{cache:'no-store'});
      if(r.status===200){
        const b=await r.arrayBuffer();
        after=Number(new DataView(b).getBigInt64(0,true));
        postMessage(await createImageBitmap(new Blob([b.slice(8)],{type:'image/jpeg'})));
      }else if(r.status!==204)await wait(1000);
    }catch(err){await wait(1000);}
  }
}`;
function camStream(){
  if(window.MediaStreamTrackGenerator&&window.VideoFrame){
    const gen=new MediaStreamTrackGenerator({kind:'video'});camWriter=gen.writable.getWriter();camCanvas=null;
    return new MediaStream([gen]);
  }
  camWriter=null;camCanvas=document.createElement('canvas');camCanvas.width=1280;camCanvas.height=720;
  return camCanvas.captureStream(30);
}
function gotCam(e){
  const bmp=e.data;camFrames++;
  try{
    if(camWriter){const f=new VideoFrame(bmp,{timestamp:Math.round(performance.now()*1000)});camWriter.write(f).catch(()=>{});}
    else if(camCanvas)camCanvas.getContext('2d').drawImage(bmp,0,0,camCanvas.width,camCanvas.height);
  }catch(x){}
  bmp.close();
}
function publishCam(){
  if(!pushUrl||!camOn||camVdo||camPublishing||Date.now()<camRetryAt)return;
  const u=new URL(pushUrl),id=u.searchParams.get('push'),pw=u.searchParams.get('password');
  if(!id||!pw)return;
  camPublishing=(async()=>{
    try{
      if(!window.VDONinjaSDK) throw new Error('the VDO.Ninja SDK did not load');
      if(!camWorker){
        camWorker=new Worker(URL.createObjectURL(new Blob([CAM_WORKER],{type:'text/javascript'})));
        camWorker.onmessage=gotCam;camWorker.postMessage({base:location.origin,token});
      }
      const stream=camStream();
      const sdk=new VDONinjaSDK({password:pw});
      await sdk.connect();
      await sdk.publish(stream,{streamID:id+'c',label:'Player camera'});
      camVdo=sdk;camProblem='';
      camWorker.postMessage({on:true});
      try{ sdk.addEventListener&&sdk.addEventListener('disconnected',()=>{ camVdo=null; camProblem='Player camera dropped: reconnecting…'; }); }catch(x){}
    }catch(e){ camProblem='Player camera not sent: '+e.message+' (trying again)'; camVdo=null; camRetryAt=Date.now()+15000; }
    camPublishing=null;
  })();
}
function stopCam(){
  if(camWorker)camWorker.postMessage({on:false});
  if(camVdo){try{camVdo.disconnect();}catch(x){}camVdo=null;}
  camWriter=null;camCanvas=null;camProblem='';
}
// Browsers start audio only after a click on the page.
addEventListener('pointerdown',()=>{ if(ctx&&ctx.state!=='running') ctx.resume(); },{capture:true});
info();setInterval(info,5000);setInterval(pump,500);voiceTick();setInterval(voiceTick,1000);
</script></body></html>";
    }
}
