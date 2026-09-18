// public/js/sound.js - tiny WebAudio synth. No audio files, everything is
// generated: blips, alarms, stings and a couple of jingles.

let ctx = null;
let master = null;
let enabled = true;

function ac() {
  if (!ctx) {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    ctx = new AC();
    master = ctx.createGain();
    master.gain.value = 0.32;
    master.connect(ctx.destination);
  }
  if (ctx.state === 'suspended') ctx.resume();
  return ctx;
}

export function unlockAudio() { ac(); }
export function setMuted(m) { enabled = !m; if (master) master.gain.value = m ? 0 : 0.32; }
export function isMuted() { return !enabled; }

function tone({ freq = 440, dur = 0.15, type = 'sine', gain = 0.3, slideTo = null, delay = 0, attack = 0.01 }) {
  const c = ac();
  if (!c || !enabled) return;
  const t0 = c.currentTime + delay;
  const osc = c.createOscillator();
  const g = c.createGain();
  osc.type = type;
  osc.frequency.setValueAtTime(freq, t0);
  if (slideTo) osc.frequency.exponentialRampToValueAtTime(Math.max(20, slideTo), t0 + dur);
  g.gain.setValueAtTime(0.0001, t0);
  g.gain.exponentialRampToValueAtTime(gain, t0 + attack);
  g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
  osc.connect(g).connect(master);
  osc.start(t0);
  osc.stop(t0 + dur + 0.05);
}

function noise({ dur = 0.2, gain = 0.2, delay = 0, filter = 900, type = 'lowpass' }) {
  const c = ac();
  if (!c || !enabled) return;
  const t0 = c.currentTime + delay;
  const len = Math.floor(c.sampleRate * dur);
  const buf = c.createBuffer(1, len, c.sampleRate);
  const data = buf.getChannelData(0);
  for (let i = 0; i < len; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / len);
  const src = c.createBufferSource();
  src.buffer = buf;
  const f = c.createBiquadFilter();
  f.type = type;
  f.frequency.value = filter;
  const g = c.createGain();
  g.gain.value = gain;
  src.connect(f).connect(g).connect(master);
  src.start(t0);
}

export const sfx = {
  click: () => tone({ freq: 660, dur: 0.06, type: 'square', gain: 0.12 }),
  confirm: () => { tone({ freq: 520, dur: 0.09, type: 'triangle' }); tone({ freq: 780, dur: 0.12, type: 'triangle', delay: 0.07 }); },
  deny: () => tone({ freq: 180, dur: 0.16, type: 'sawtooth', gain: 0.18, slideTo: 90 }),
  taskDone: () => { tone({ freq: 880, dur: 0.1, type: 'sine' }); tone({ freq: 1320, dur: 0.16, type: 'sine', delay: 0.08 }); },
  taskStep: () => tone({ freq: 740, dur: 0.08, type: 'sine', gain: 0.18 }),
  kill: () => {
    noise({ dur: 0.35, gain: 0.5, filter: 400 });
    tone({ freq: 140, dur: 0.5, type: 'sawtooth', gain: 0.32, slideTo: 40 });
    tone({ freq: 1200, dur: 0.18, type: 'square', gain: 0.12, slideTo: 300, delay: 0.02 });
  },
  death: () => { tone({ freq: 400, dur: 0.6, type: 'sine', gain: 0.3, slideTo: 60 }); noise({ dur: 0.5, gain: 0.25, filter: 500 }); },
  report: () => {
    for (let i = 0; i < 3; i++) tone({ freq: 880, dur: 0.16, type: 'square', gain: 0.22, delay: i * 0.2 });
    tone({ freq: 220, dur: 0.8, type: 'sawtooth', gain: 0.18, delay: 0.1 });
  },
  meeting: () => {
    tone({ freq: 330, dur: 0.5, type: 'sawtooth', gain: 0.22 });
    tone({ freq: 440, dur: 0.6, type: 'square', gain: 0.16, delay: 0.18 });
  },
  vote: () => tone({ freq: 620, dur: 0.1, type: 'triangle', gain: 0.24 }),
  eject: () => { noise({ dur: 1.2, gain: 0.3, filter: 1600, type: 'highpass' }); tone({ freq: 300, dur: 1.4, type: 'sine', gain: 0.18, slideTo: 60 }); },
  vent: () => { noise({ dur: 0.22, gain: 0.35, filter: 2400, type: 'highpass' }); tone({ freq: 160, dur: 0.22, type: 'square', gain: 0.14 }); },
  sabotage: () => {
    for (let i = 0; i < 2; i++) {
      tone({ freq: 500, dur: 0.35, type: 'sawtooth', gain: 0.24, slideTo: 260, delay: i * 0.4 });
    }
  },
  alarm: () => tone({ freq: 720, dur: 0.3, type: 'square', gain: 0.2, slideTo: 420 }),
  doors: () => { noise({ dur: 0.3, gain: 0.3, filter: 700 }); tone({ freq: 90, dur: 0.35, type: 'square', gain: 0.2 }); },
  win: () => [0, 0.14, 0.28, 0.5].forEach((d, i) => tone({ freq: [523, 659, 784, 1047][i], dur: 0.35, type: 'triangle', gain: 0.26, delay: d })),
  lose: () => [0, 0.16, 0.34].forEach((d, i) => tone({ freq: [392, 330, 196][i], dur: 0.5, type: 'sawtooth', gain: 0.22, delay: d })),
  step: () => noise({ dur: 0.05, gain: 0.05, filter: 380 }),
};
