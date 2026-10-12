using System.Linq;

namespace TournamentTracker.App
{
    /// <summary>The host's side of the broadcast feed (docs/broadcast-protocol.md).</summary>
    public static class SendPage
    {
        /// <summary>
        /// VDO.Ninja's SDK for the lobby voice and the player camera, at a fixed version (the one this page
        /// was used with), so a new release can't change the page under a host in the middle of an event.
        /// </summary>
        public const string SdkUrl = "https://unpkg.com/@vdoninja/sdk@1.6.2/vdoninja-sdk.min.js";

        /// <summary>Share the Among Us window (the default) or the entire screen.</summary>
        public const string ShareWindow = "window", ShareScreen = "screen";

        /// <summary>Lower quality: 720p at 30 fps, and never more than this (kbps) to anyone watching.</summary>
        public const int LowQualityKbps = 2500;

        // The link's options this page sets itself (anything the mod put in for them is replaced).
        private static readonly string[] OwnOptions = { "displaysurface", "selfbrowsersurface", "surfaceswitching", "systemaudio", "maxvideobitrate", "mvb", "maxbitrate" };

        /// <summary>
        /// The mod's VDO.Ninja push link with what to share. VDO.Ninja's iframe does the screen share itself
        /// (getDisplayMedia, inside its page), so this page can't look at what was picked; these documented
        /// options of VDO.Ninja's (passed on to the browser's picker) do the next best thing:
        /// <list type="bullet">
        /// <item>window: the picker opens on its Window tab (displaysurface=window); this tab can't be picked
        /// (selfbrowsersurface=exclude); no "share system audio" box (systemaudio=exclude: a window share
        /// can't carry sound in Chrome anyway, and The Button's lobby voice sends the game sound).</item>
        /// <item>screen: the picker opens on Entire screen, where "Share system audio" can be ticked (for game
        /// sound when the lobby voice is off).</item>
        /// </list>
        /// Both let the host switch what's shared from the browser's bar (surfaceswitching=include).
        /// Lower quality: 720p (quality=1), 30 fps and at most <see cref="LowQualityKbps"/> (maxvideobitrate:
        /// VDO.Ninja's own cap on what it sends, whatever a viewer asks for), for a slow upload.
        /// </summary>
        public static string ShareUrl(string pushUrl, string? mode, bool lowQuality)
        {
            int q = pushUrl.IndexOf('?');
            if (q < 0) return pushUrl;
            var keep = pushUrl.Substring(q + 1).Split('&', System.StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    string name = p.Split('=')[0].ToLowerInvariant();
                    return !OwnOptions.Contains(name) && !(lowQuality && (name == "quality" || name == "q" || name == "maxframerate" || name == "mfr"));
                }).ToList();
            bool window = mode != ShareScreen;
            keep.Add("displaysurface=" + (window ? "window" : "monitor"));
            keep.Add("selfbrowsersurface=exclude");
            keep.Add("surfaceswitching=include");
            if (window) keep.Add("systemaudio=exclude");
            if (lowQuality) keep.AddRange(new[] { "quality=1", "maxframerate=30", "maxvideobitrate=" + LowQualityKbps });
            return pushUrl.Substring(0, q + 1) + string.Join("&", keep);
        }

        /// <summary>
        /// What the host does on the send page, in one sentence or two, for Home and the toast when sending
        /// starts: which share to pick, and where the game sound comes from.
        /// </summary>
        public static string Instructions(string? mode, bool voiceOn)
        {
            if (mode == ShareScreen)
                return voiceOn
                    ? "In the page that opened, press the share button, pick Entire screen and leave Share system audio unticked (The Button sends the game sound and lobby voice itself). Everything on that screen is sent, so the Among Us window is the private choice. Leave the page open while you play."
                    : "In the page that opened, press the share button, pick Entire screen and tick Share system audio, so the caster gets the game sound. Everything on that screen is sent. Leave the page open while you play.";
            return voiceOn
                ? "In the page that opened, press the share button, open the Window tab and pick Among Us. The Button sends the game sound and lobby voice itself. Leave the page open while you play."
                : "In the page that opened, press the share button, open the Window tab and pick Among Us. A window share has no sound: turn on Send lobby voice on that page for the game sound (or share Entire screen with system audio instead). Leave the page open while you play.";
        }

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
body{display:flex;flex-direction:column}
header,footer{padding:10px 16px}
header b{color:#fff}
iframe{border:0;width:100%;flex:1 1 0;min-height:0;background:#000}
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
#nopic{display:none;padding:9px 16px;background:#5a1f24;color:#fff;font-weight:600}
#nopic.on{display:block}
#gone{display:none;padding:16px;background:#8a1520;color:#fff;font-weight:700;font-size:18px;text-align:center}
#gone.on{display:block}
#retired{display:none;padding:16px;background:#1d2633;color:#fff;font-weight:700;font-size:18px;text-align:center}
#retired.on{display:block}
header{display:flex;flex-direction:column;gap:6px}
.share{display:flex;flex-wrap:wrap;gap:6px 16px;align-items:center;font-size:13.5px;color:var(--muted)}
.share label{display:flex;align-items:center;gap:5px;cursor:pointer}
.share label.on{color:var(--fg)}
.share button{font:inherit;color:var(--fg);background:#1b222b;border:1px solid var(--line);border-radius:6px;padding:3px 10px;cursor:pointer}
.share button:hover{background:#252c36}
</style></head><body>
<div id=""retired"">This tab isn't sending any more: The Button opened a newer one (Open again), or sending was turned off. You can close this tab.</div>
<div id=""gone"">Live game data isn't reaching The Button. If The Button restarted (an update), click <u>Open again</u> in The Button (Home → Send my game to the caster), then close this tab. The video still goes out.</div>
<div id=""drop"">Disconnected from caster, reconnecting… Keep playing: nothing is lost, it's all sent when the link is back.</div>
<div id=""nopic"">No picture is going out to the caster. Restarted Among Us? Press <b>Pick again</b> below and choose the new <b>Among Us</b> window.</div>
<header><div id=""howto"">Sending your game to the caster. Below, press the share button, open the <b>Window</b> tab and pick <b>Among Us</b>. Then leave this tab open while you play.</div>
<div class=""share"" id=""share"">Share:
  <label><input type=""radio"" name=""sm"" value=""window""> The Among Us window (recommended)</label>
  <label><input type=""radio"" name=""sm"" value=""screen""> Entire screen</label>
  <label title=""720p at 30 fps, at most 2.5 Mbps: for a slow upload (Check my connection in The Button tells you)""><input type=""checkbox"" id=""low""> Lower quality (slow upload)</label>
  <button type=""button"" id=""again"" title=""Shared the wrong thing? Choose again (the caster's picture restarts)"">Pick again</button>
</div></header>
<iframe id=""v"" allow=""camera;microphone;display-capture;autoplay;fullscreen;clipboard-write""></iframe>
<div id=""voice"">
  <span class=""st"" id=""vstate"">Lobby voice: starting…</span>
  <span class=""src"">Discord <span class=""meter""><i id=""mv""></i></span><input type=""range"" id=""lv"" min=""0"" max=""150"" title=""Lobby voice level""></span>
  <span class=""src"">Among Us <span class=""meter""><i id=""mg""></i></span><input type=""range"" id=""lg"" min=""0"" max=""150"" title=""Game sound level""></span>
  <label class=""src""><input type=""checkbox"" id=""mic""> Include my microphone</label>
  <label class=""src""><input type=""checkbox"" id=""von""> Send lobby voice</label>
</div>
<footer><span id=""link"">Connecting to Among Us…</span><span id=""data""></span><span id=""cam""></span><span id=""up""></span></footer>
<script src=""" + SdkUrl + @"""></script>
<script>
const token=new URLSearchParams(location.search).get('token')||'';
// This tab's id, from the link The Button opened. When The Button opens a newer tab (Open again) or sending is
// turned off, it answers this one with 409: the tab stops sending, so two tabs never share the lobby sound.
const page=new URLSearchParams(location.search).get('page')||'';
const PROTOCOL=1;   // the broadcast feed's version (FeedProtocol.Version, docs/broadcast-protocol.md)
const q=p=>p+(p.includes('?')?'&':'?')+'token='+encodeURIComponent(token)+(page?'&page='+encodeURIComponent(page):'');
const v=document.getElementById('v');
let pushUrl=null,since=null,sent=0,lastOk=0,lobby=null,twitch=null;
// What VDO.Ninja is told to share (the push link with The Button's share options), and the host's choice:
// the Among Us window (recommended) or the entire screen, and lower quality for a slow upload.
let shareUrl=null,share={mode:'window',low:false};
// Reaching The Button: a 401 means it restarted with a new link (an old version), anything else that it's
// closed or busy. Never silent: the footer says so after 5 s, and the caster is told (health: data 'lost').
let feedFailSince=0,buttonGone=false,retired=false;
async function getJson(path){
  const r=await fetch(q(path),{cache:'no-store'});
  if(r.status===409){retire();throw new Error('This tab was replaced');}
  if(r.status===401||r.status===403){buttonGone=true;throw new Error('The Button restarted');}
  if(!r.ok)throw new Error('HTTP '+r.status);
  return r.json();
}
function feedOk(){feedFailSince=0;buttonGone=false;}
// ---- What to share ------------------------------------------------------------------------------
// VDO.Ninja's page below does the sharing, so this page can't see what was picked: it opens the browser's
// picker on the right tab (the Window tab for Among Us), and Pick again asks once more.
function syncShare(){
  document.querySelectorAll('input[name=sm]').forEach(e=>{e.checked=e.value===share.mode;e.parentElement.classList.toggle('on',e.checked);});
  document.getElementById('low').checked=!!share.low;
  howto();
}
function howto(){
  const voice=!!(VS&&VS.on&&VS.state&&VS.state.supported),unsupported=!!(VS&&VS.state&&!VS.state.supported);
  const start='Sending your game to the caster. Below, press the share button, ',end=' Then leave this tab open while you play.';
  document.getElementById('howto').innerHTML=share.mode==='screen'
    ?(voice?start+'pick <b>Entire screen</b> and leave <b>Share system audio</b> unticked: The Button sends the game sound and the lobby voice itself. Everything on that screen is sent, so <b>The Among Us window</b> is the private choice.'+end
      :start+'pick <b>Entire screen</b> and tick <b>Share system audio</b> (so the caster gets the game sound).'+end)
    :(voice?start+'open the <b>Window</b> tab and pick <b>Among Us</b>. The Button sends the game sound and the lobby voice itself.'+end
      :unsupported?start+'open the <b>Window</b> tab and pick <b>Among Us</b>. A window share has no sound, and the lobby voice needs Windows 10 2004 or later: for the game sound choose <b>Entire screen</b> above and tick <b>Share system audio</b>.'+end
      :start+'open the <b>Window</b> tab and pick <b>Among Us</b>. A window share has no sound: tick <b>Send lobby voice</b> below for the game sound (or choose <b>Entire screen</b> above and tick <b>Share system audio</b>).'+end);
}
function setShare(body){
  fetch(q('/app/share'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)})
    .then(r=>{if(r.status===409){retire();return;}return info();}).catch(()=>{});
}
document.querySelectorAll('input[name=sm]').forEach(e=>e.onchange=()=>{share.mode=e.value;syncShare();setShare({mode:e.value});});
document.getElementById('low').onchange=e=>{share.low=e.target.checked;setShare({low:String(e.target.checked)});};
document.getElementById('again').onclick=()=>{if(retired||!shareUrl)return;load('about:blank');setTimeout(()=>{if(!retired)load(shareUrl);},300);};
function feedFailed(){if(!feedFailSince)feedFailSince=Date.now();}
const feedLost=()=>buttonGone||(feedFailSince&&Date.now()-feedFailSince>5000);
// Runs fn every ms, and right away; never again while the last one is still waiting for an answer (two pumps
// at once would send the same messages twice), and not at all once this tab is retired.
function every(fn,ms){let busy=false;const run=async()=>{if(busy||retired)return;busy=true;try{await fn();}catch(e){}finally{busy=false;}};run();setInterval(run,ms);}
async function info(){
  try{
    const r=await getJson('/app/sendinfo');
    if(retired)return;
    feedOk();
    twitch=r.twitch||null;
    if(r.share){share=r.share;syncShare();}
    // A new link, or the share choice changed: VDO.Ninja's page opens again and asks what to share.
    const want=r.shareUrl||r.pushUrl;
    if(want&&want!==shareUrl){shareUrl=want;load(want);}
    if(r.pushUrl&&r.pushUrl!==pushUrl){pushUrl=r.pushUrl;publishVoice();}
    const wantCam=!!(r.cam&&pushUrl);
    if(wantCam!==camOn){camOn=wantCam;if(!camOn)stopCam();}
    if(camOn)publishCam();
    const ce=document.getElementById('cam');ce.textContent=!camOn?'':camProblem||(camVdo?`Player camera: sending (${camFrames} pictures)`:'Player camera: starting…');ce.className=camProblem?'bad':camVdo?'ok':'';
    document.getElementById('link').textContent=pushUrl?'Video link ready.':'Waiting for Among Us with ""Send my game to the caster"" on.';
  }catch(e){if(!retired)feedFailed();}
}
function send(items){ if(v.contentWindow&&!retired) v.contentWindow.postMessage({sendData:{tt:items},type:'pcs'},'*'); }
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
    // The first ask only learns where the feed is up to (older messages aren't sent), so it asks for nothing older.
    const d=await getJson('/app/sendfeed?since='+(since??Number.MAX_SAFE_INTEGER)+'&video='+video);
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
  }catch(e){if(retired)return;feedFailed();}
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
let frames=-1,stillFor=0,video='unknown',slowFor=0;
// VDO.Ninja's page starts over (a new link, the share changed, Pick again) and waits for the host to pick:
// nothing is going out yet, so the picture is 'unknown' (never a stale 'ok' or 'lost') until it says otherwise.
function load(src){video='unknown';frames=-1;stillFor=0;noPicture();v.src=src;}
// The picture has stalled for 10 s (the Among Us window it shared closed, say): tell the host how to pick again.
function noPicture(){document.getElementById('nopic').classList.toggle('on',!retired&&video==='lost'&&stillFor>=5);}
function askStats(){ if(v.contentWindow) v.contentWindow.postMessage({getStats:true},'*'); }
function gotStats(st){
  const out=st&&st.outbound?Object.values(st.outbound).filter(Boolean):[];
  if(!out.length){video='unknown';frames=-1;stillFor=0;noPicture();return;}
  const f=Math.max(...out.map(o=>o._framesEncoded||0)),kbps=Math.max(...out.map(o=>o.video_bitrate_kbps||0));
  // The browser lowers the picture when the upload can't keep up: said here after 6 s of it.
  slowFor=out.some(o=>o.quality_limitation_reason==='bandwidth')?slowFor+1:0;
  const up=document.getElementById('up');up.textContent=slowFor>=3?'Your upload can\'t keep up, so the picture is lowered: try Lower quality above, a wired connection, or closing other uploads.':'';up.className=slowFor>=3?'bad':'';
  if(f>frames||kbps>0){stillFor=0;video='ok';}else if(++stillFor>=2)video='lost';
  frames=f;
  noPicture();
}
function health(){ if(lobby) send([{v:PROTOCOL,type:'health',lobby,t:Date.now(),video,data:feedLost()?'lost':'ok',queued:queue.length},{v:PROTOCOL,type:'host',lobby,t:Date.now(),twitch}]); }
setInterval(()=>{if(retired)return;askStats();health();},2000);
// The caster switches this lobby's spectator view (lit map, vision, ""!"", eye): only ""spec …"" commands are taken.
addEventListener('message',e=>{
  if(retired||e.source!==v.contentWindow)return;
  if(e.data&&e.data.stats){gotStats(e.data.stats);return;}
  const got=e.data&&e.data.dataReceived;const cmd=got&&got.ttc;
  if(got&&got.ttack){ack(got.ttack);return;}
  // Roster names for this lobby's players, for the referee's nameplates.
  if(got&&got.ttn&&typeof got.ttn==='object'){fetch(q('/app/names'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({names:got.ttn})}).catch(()=>{});return;}
  if(typeof cmd!=='string'||!/^spec [a-z]+( [a-z0-9.]+)?$/.test(cmd))return;
  fetch(q('/app/command'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:cmd})}).catch(()=>{});
});
// VDO.Ninja's SDK reconnects by itself after a drop (and says it will): it's only let go, and a new one
// started, when it won't or gave up, so two never send the same stream at once.
function watchSdk(sdk,current,problem,gone){
  if(!sdk.addEventListener)return;
  const giveUp=()=>{try{sdk.disconnect();}catch(x){}gone();};
  sdk.addEventListener('disconnected',e=>{const d=(e&&e.detail)||{};if(d.intentional||current()!==sdk)return;if(d.willReconnect)problem(true);else giveUp();});
  sdk.addEventListener('reconnected',()=>{if(current()===sdk)problem(false);});
  sdk.addEventListener('reconnectFailed',()=>{if(current()===sdk)giveUp();});
}
// Replaced by a newer tab, or sending turned off in The Button: stop sharing the screen, the lobby voice and the
// player camera, and say so.
function retire(){
  if(retired)return;
  retired=true;
  document.getElementById('nopic').classList.remove('on');
  try{v.src='about:blank';}catch(e){}
  if(vdo){try{vdo.disconnect();}catch(e){}vdo=null;}
  stopCam();
  if(pullWorker)pullWorker.postMessage({on:false});
  if(micStream){try{micStream.getTracks().forEach(t=>t.stop());}catch(e){}}
  if(ctx){try{ctx.close().catch(()=>{});}catch(e){}}
  document.getElementById('retired').classList.add('on');
  document.getElementById('gone').classList.remove('on');
  document.getElementById('drop').classList.remove('on');
  document.getElementById('link').textContent='Not sending.';
  document.getElementById('data').textContent='';
  document.getElementById('vstate').textContent='Lobby voice: not sending';
}

// ---- Lobby voice (Part 11) -------------------------------------------------------------------
// The Button captures Discord's sound (the lobby as you hear it, never your own voice) and Among Us's
// sound separately; this page mixes them (plus your microphone if you want) and sends the mix to the
// caster as its own stream next to your screen. Nothing is played back here or to the players.
let an=null,anBuf=null,ctx=null,node=null,dest=null,micNode=null,micStream=null,vdo=null,publishing=null,voiceProblem='',VS=null,noticeSent=false,retryAt=0,retryWait=5000,pullWorker=null;
const WORKLET=`class TTMix extends AudioWorkletProcessor{constructor(){super();this.q=[];this.off=0;this.buffered=0;this.started=false;
  this.port.onmessage=e=>{if(e.data&&e.data.port){e.data.port.onmessage=m=>this.add(m.data);return;}this.add(e.data);};}
  add(d){this.q.push(d);this.buffered+=d.length/2;while(this.buffered>48000*0.6&&this.q.length>1){const x=this.q.shift();this.buffered-=(x.length-this.off)/2;this.off=0;}}
  process(_,outs){const L=outs[0][0],R=outs[0][1]||outs[0][0];
    if(!this.started){if(this.buffered<48000*0.12){L.fill(0);R.fill(0);return true;}this.started=true;}
    for(let i=0;i<L.length;i++){
      if(!this.q.length){L[i]=R[i]=0;this.started=false;continue;}
      const d=this.q[0];L[i]=d[this.off]/32768;R[i]=d[this.off+1]/32768;this.off+=2;this.buffered--;
      if(this.off>=d.length){this.q.shift();this.off=0;}}
    return true;}}
registerProcessor('tt-mix',TTMix);`;
// The sound is fetched by a worker and handed straight to the worklet: this page's own timers run at most once a
// second while the tab is in the background (behind Among Us), and The Button keeps only half a second.
const PULL_WORKER=`let on=false,running=false,base='',token='',page='',port=null;
onmessage=e=>{const d=e.data;if(d.base){base=d.base;token=d.token;page=d.page;}if(d.port)port=d.port;if('on' in d){on=d.on;if(on&&!running)loop();}};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
async function loop(){
  running=true;
  while(on){
    try{
      const r=await fetch(base+'/app/voice/pcm?token='+encodeURIComponent(token)+(page?'&page='+encodeURIComponent(page):''),{cache:'no-store'});
      if(r.status===409){on=false;postMessage({retired:true});break;}
      if(r.ok){const b=await r.arrayBuffer();if(b.byteLength>=4&&port){const a=new Int16Array(b);port.postMessage(a,[a.buffer]);}}
    }catch(err){}
    await wait(40);
  }
  running=false;
}`;
async function audio(){
  if(ctx)return;
  ctx=new AudioContext({sampleRate:48000});
  await ctx.audioWorklet.addModule(URL.createObjectURL(new Blob([WORKLET],{type:'text/javascript'})));
  node=new AudioWorkletNode(ctx,'tt-mix',{numberOfInputs:0,outputChannelCount:[2]});
  dest=ctx.createMediaStreamDestination();
  node.connect(dest);                       // only to the stream: never to the speakers
  // What actually leaves this page (for Red Alert: sound sent but silent in OBS means OBS's side).
  an=ctx.createAnalyser();an.fftSize=2048;anBuf=new Float32Array(an.fftSize);node.connect(an);
  const ch=new MessageChannel();
  node.port.postMessage({port:ch.port1},[ch.port1]);
  pullWorker=new Worker(URL.createObjectURL(new Blob([PULL_WORKER],{type:'text/javascript'})));
  pullWorker.onmessage=e=>{if(e.data&&e.data.retired)retire();};
  pullWorker.postMessage({base:location.origin,token,page,port:ch.port2},[ch.port2]);
  pullWorker.postMessage({on:!!(VS&&VS.on)});
}
async function mic(on){
  if(on&&!micNode){
    try{ micStream=await navigator.mediaDevices.getUserMedia({audio:{echoCancellation:true,noiseSuppression:true}}); micNode=ctx.createMediaStreamSource(micStream); micNode.connect(dest); }
    catch(e){ voiceProblem='Microphone blocked: '+e.message; document.getElementById('mic').checked=false; }
  } else if(!on&&micNode){ micNode.disconnect(); micStream.getTracks().forEach(t=>t.stop()); micNode=null; micStream=null; }
}
async function publishVoice(){
  if(retired||!pushUrl||!VS||!VS.on||publishing||Date.now()<retryAt)return;
  const u=new URL(pushUrl),id=u.searchParams.get('push'),pw=u.searchParams.get('password');
  if(!id||!pw)return;
  publishing=(async()=>{
    let sdk=null;
    try{
      await audio();
      if(ctx.state!=='running') await ctx.resume().catch(()=>{});
      if(!window.VDONinjaSDK) throw new Error('the VDO.Ninja SDK did not load');
      if(vdo){ try{ await vdo.disconnect(); }catch(e){} vdo=null; }
      sdk=new VDONinjaSDK({password:pw});
      await sdk.connect();
      await sdk.publish(dest.stream,{streamID:id+'v',label:'Lobby voice'});
      if(retired){ try{sdk.disconnect();}catch(x){} return; }
      vdo=sdk;voiceProblem='';retryWait=5000;
      // Dropped later: VDO.Ninja tries again itself; if it gives up, a new one is started (5 s, 10 s … 1 min apart).
      try{ watchSdk(sdk,()=>vdo,on=>{voiceProblem=on?'Lobby voice dropped: reconnecting…':'';},()=>{vdo=null;voiceProblem='Lobby voice dropped: reconnecting…';}); }catch(x){}
      if(!noticeSent){ noticeSent=true; fetch(q('/app/command'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({command:'voicenotice'})}).catch(()=>{}); }
    }catch(e){
      // A half-made connection is let go, not left trying in the background.
      if(sdk){ try{sdk.disconnect();}catch(x){} }
      voiceProblem='Lobby voice not sent: '+e.message+' (trying again)'; vdo=null; retryAt=Date.now()+retryWait; retryWait=Math.min(60000,retryWait*2);
    }
    finally{ publishing=null; }
  })();
}
const pct=db=>Math.max(0,Math.min(100,(db+60)/60*100));
const STATE={capturing:'on','not running':'not open',unsupported:'needs Windows 10 2004 or later',off:'off'};
let ticking=false;
async function voiceTick(){
  // One at a time (a slider or a tick box asks for one too): two at once could open the microphone twice.
  if(ticking||retired)return;
  ticking=true;
  try{
    const res=await fetch(q('/app/voice'),{cache:'no-store'});
    if(res.status===409){retire();return;}
    const r=await res.json();
    if(retired)return;
    const first=!VS; VS=r;
    const s=r.state;
    if(first){ document.getElementById('lv').value=Math.round(s.voiceLevel*100); document.getElementById('lg').value=Math.round(s.gameLevel*100); document.getElementById('mic').checked=r.mic; document.getElementById('von').checked=r.on; }
    if(pullWorker) pullWorker.postMessage({on:!!r.on});
    document.getElementById('mv').style.width=pct(s.voice.level)+'%';
    document.getElementById('mg').style.width=pct(s.game.level)+'%';
    const working=r.on&&s.supported&&s.voice.state==='capturing';
    const st=document.getElementById('vstate');
    const held=ctx&&ctx.state!=='running';
    st.textContent=held&&r.on?'Click anywhere on this page to start the lobby sound (the browser holds it back until you do).':!r.on?'Lobby voice: off':!s.supported?'Lobby voice needs Windows 10 2004 or later':voiceProblem||`Lobby voice: Discord ${STATE[s.voice.state]||s.voice.state} · Among Us ${STATE[s.game.state]||s.game.state}${vdo?' · sending':''}`;
    st.className='st '+(voiceProblem||held&&r.on||!working&&r.on?'bad':vdo?'ok':'');
    // With The Button sending the game sound, the screen share must not carry it too.
    howto();
    if(ctx) await mic(r.on&&r.mic);
    if(r.on&&!vdo) publishVoice();
    // The caster's tab shows this lobby's voice status.
    let outDb=-100;
    if(an&&ctx&&ctx.state==='running'){an.getFloatTimeDomainData(anBuf);let pk=0;for(let i=0;i<anBuf.length;i++){const a=Math.abs(anBuf[i]);if(a>pk)pk=a;}outDb=pk>0?Math.max(-100,Math.round(20*Math.log10(pk))):-100;}
    if(lobby) send([{v:PROTOCOL,type:'voice',lobby,t:Date.now(),on:r.on,sending:!!vdo,problem:voiceProblem||(held&&r.on?'The host\'s send page is waiting for a click to start the sound':null),discord:s.voice.state,game:s.game.state,voiceDb:s.voice.level,gameDb:s.game.level,outDb,mic:!!micNode}]);
  }catch(e){}
  finally{ ticking=false; }
}
function save(body){ fetch(q('/app/voice'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}).then(()=>voiceTick()).catch(()=>{}); }
// A level slider being dragged: saved once it rests for a moment (or is let go), not for every step.
let levelBody=null,levelTimer=0;
function saveLevel(body){ levelBody=Object.assign(levelBody||{},body); clearTimeout(levelTimer); levelTimer=setTimeout(saveLevelNow,150); }
function saveLevelNow(){ clearTimeout(levelTimer); if(!levelBody)return; const b=levelBody; levelBody=null; save(b); }
document.getElementById('lv').oninput=e=>saveLevel({voiceLevel:e.target.value/100});
document.getElementById('lg').oninput=e=>saveLevel({gameLevel:e.target.value/100});
document.getElementById('lv').onchange=saveLevelNow;
document.getElementById('lg').onchange=saveLevelNow;
document.getElementById('mic').onchange=e=>save({mic:String(e.target.checked)});
document.getElementById('von').onchange=e=>{ if(!e.target.checked&&vdo){ try{vdo.disconnect();}catch(x){} vdo=null; } save({on:String(e.target.checked)}); };
// ---- The player camera ----------------------------------------------------------------------------
// Red Alert can turn on a second picture from your game that follows one player up close (you don't
// pick who, the caster does). A worker gets each picture from The Button (workers aren't slowed down
// while this tab is in the background) and it goes to the caster as its own stream. Nothing shows here.
let camOn=false,camVdo=null,camPublishing=null,camWorker=null,camWriter=null,camCanvas=null,camTracks=[],camRetryAt=0,camProblem='',camFrames=0;
const CAM_WORKER=`let on=false,running=false,after=0,base='',token='',page='';
onmessage=e=>{const d=e.data;if(d.base){base=d.base;token=d.token;page=d.page;}if('on' in d){on=d.on;if(on&&!running)loop();}};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
async function loop(){
  running=true;
  while(on){
    const asked=Date.now();
    try{
      const r=await fetch(base+'/app/cam?token='+encodeURIComponent(token)+(page?'&page='+encodeURIComponent(page):'')+'&after='+after,{cache:'no-store'});
      if(r.status===200){
        const b=await r.arrayBuffer();
        after=Number(new DataView(b).getBigInt64(0,true));
        const bmp=await createImageBitmap(new Blob([b.slice(8)],{type:'image/jpeg'}));
        postMessage(bmp,[bmp]);   // handed over, not copied
      }else if(r.status===409){on=false;postMessage({retired:true});}
      else if(r.status!==204)await wait(1000);
      // No picture: the mod waits up to a quarter of a second for one, so an answer straight away means
      // there's nothing to wait on (Among Us closed): don't ask again at once.
      else if(Date.now()-asked<100)await wait(250);
    }catch(err){await wait(1000);}
  }
  running=false;
}`;
function camStream(){
  endCamStream();
  if(window.MediaStreamTrackGenerator&&window.VideoFrame){
    const gen=new MediaStreamTrackGenerator({kind:'video'});camWriter=gen.writable.getWriter();camCanvas=null;camTracks=[gen];
    return new MediaStream([gen]);
  }
  camWriter=null;camCanvas=document.createElement('canvas');camCanvas.width=960;camCanvas.height=540;
  const s=camCanvas.captureStream(30);camTracks=s.getTracks();
  return s;
}
// The camera's own picture track, ended when the camera stops or a new one is made.
function endCamStream(){
  if(camWriter){try{camWriter.close().catch(()=>{});}catch(x){}}
  camTracks.forEach(t=>{try{t.stop();}catch(x){}});
  camTracks=[];camWriter=null;camCanvas=null;
}
function gotCam(e){
  if(e.data&&e.data.retired){retire();return;}
  const bmp=e.data;camFrames++;lastCamAt=Date.now();
  try{
    if(camWriter){const f=new VideoFrame(bmp,{timestamp:Math.round(performance.now()*1000)});camWriter.write(f).catch(()=>{});}
    else if(camCanvas)camCanvas.getContext('2d').drawImage(bmp,0,0,camCanvas.width,camCanvas.height);
  }catch(x){}
  bmp.close();
}
// No new picture for a moment (a meeting, the game over: the game stops drawing the camera): a clean
// card goes out instead of the last picture sitting frozen on stream.
let lastCamAt=0,pausedCard=null;
function pausedPicture(){
  if(pausedCard)return pausedCard;
  const c=document.createElement('canvas');c.width=960;c.height=540;const g=c.getContext('2d');
  g.fillStyle='#0b0e13';g.fillRect(0,0,960,540);g.textAlign='center';
  g.fillStyle='#5eead4';g.font='600 40px Segoe UI, system-ui, sans-serif';g.fillText('Player camera paused',480,262);
  g.fillStyle='#9aa6b2';g.font='24px Segoe UI, system-ui, sans-serif';g.fillText('Back when play resumes',480,306);
  return pausedCard=c;
}
setInterval(()=>{
  if(!camVdo||Date.now()-lastCamAt<1500)return;
  try{
    if(camWriter){const f=new VideoFrame(pausedPicture(),{timestamp:Math.round(performance.now()*1000)});camWriter.write(f).catch(()=>{});}
    else if(camCanvas)camCanvas.getContext('2d').drawImage(pausedPicture(),0,0,camCanvas.width,camCanvas.height);
  }catch(x){}
},500);
function publishCam(){
  if(retired||!pushUrl||!camOn||camVdo||camPublishing||Date.now()<camRetryAt)return;
  const u=new URL(pushUrl),id=u.searchParams.get('push'),pw=u.searchParams.get('password');
  if(!id||!pw)return;
  camPublishing=(async()=>{
    let sdk=null;
    try{
      if(!window.VDONinjaSDK) throw new Error('the VDO.Ninja SDK did not load');
      if(!camWorker){
        camWorker=new Worker(URL.createObjectURL(new Blob([CAM_WORKER],{type:'text/javascript'})));
        camWorker.onmessage=gotCam;camWorker.postMessage({base:location.origin,token,page});
      }
      const stream=camStream();
      sdk=new VDONinjaSDK({password:pw});
      await sdk.connect();
      await sdk.publish(stream,{streamID:id+'c',label:'Player camera'});
      if(retired||!camOn){ try{sdk.disconnect();}catch(x){} endCamStream(); return; }
      camVdo=sdk;camProblem='';
      camWorker.postMessage({on:true});
      try{ watchSdk(sdk,()=>camVdo,on=>{camProblem=on?'Player camera dropped: reconnecting…':'';},()=>{camVdo=null;camProblem='Player camera dropped: reconnecting…';}); }catch(x){}
    }catch(e){
      if(sdk){ try{sdk.disconnect();}catch(x){} }
      endCamStream();
      camProblem='Player camera not sent: '+e.message+' (trying again)'; camVdo=null; camRetryAt=Date.now()+15000;
    }
    finally{ camPublishing=null; }
  })();
}
function stopCam(){
  if(camWorker)camWorker.postMessage({on:false});
  if(camVdo){try{camVdo.disconnect();}catch(x){}camVdo=null;}
  endCamStream();camProblem='';
}
// Browsers start audio only after a click on the page.
addEventListener('pointerdown',()=>{ if(ctx&&!retired&&ctx.state!=='running') ctx.resume().catch(()=>{}); },{capture:true});
every(info,5000);every(pump,500);every(voiceTick,1000);
</script></body></html>";
    }
}
