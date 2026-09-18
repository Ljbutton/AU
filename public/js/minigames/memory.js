// public/js/minigames/memory.js
// Sequence, pattern and inspection consoles.

import { el, canvasEl, localPoint, bar } from './index.js';
import { sfx } from '../sound.js';

/** Start Reactor: repeat the light sequence, growing each round. */
function simonGame() {
  return (ctx) => {
    const COLORS = ['#e23b3b', '#3f7de0', '#ffd23f', '#3fd07a', '#e0459f'];
    const ROUNDS = 5;
    const sequence = [];
    let showing = true, showIndex = 0, inputIndex = 0, round = 0, flashing = -1, timer = 0, raf = 0, last = performance.now();
    const buttons = [];
    const status = el('p', { class: 'mg-hint' }, 'Watch the pattern…');

    const grid = el('div', { class: 'mg-grid', style: { gridTemplateColumns: 'repeat(5, 1fr)' } });
    COLORS.forEach((c, i) => {
      const b = el('button', { class: 'mg-btn', style: { height: '110px', background: c, opacity: '0.45' } });
      b.addEventListener('click', () => press(i));
      buttons.push(b);
      grid.append(b);
    });

    const light = (i) => {
      flashing = i;
      timer = 0.34;
      buttons[i].style.opacity = '1';
      sfx.click();
    };
    const nextRound = () => {
      round++;
      sequence.push(Math.floor(Math.random() * COLORS.length));
      showing = true; showIndex = 0; inputIndex = 0; timer = 0.5;
      status.textContent = `Watch the pattern… (${round}/${ROUNDS})`;
    };
    const press = (i) => {
      if (showing) return;
      if (sequence[inputIndex] === i) {
        light(i);
        inputIndex++;
        if (inputIndex >= sequence.length) {
          ctx.progress();
          if (round >= ROUNDS) { cancelAnimationFrame(raf); ctx.done(); return; }
          nextRound();
        }
      } else {
        sfx.deny();
        status.textContent = 'Wrong — starting over.';
        sequence.length = 0; round = 0;
        nextRound();
      }
    };

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      timer -= dt;
      if (flashing >= 0 && timer <= 0.05) { buttons[flashing].style.opacity = '0.45'; flashing = -1; }
      if (showing && timer <= 0) {
        if (showIndex < sequence.length) { light(sequence[showIndex]); showIndex++; timer = 0.55; }
        else { showing = false; status.textContent = 'Your turn.'; }
      }
      raf = requestAnimationFrame(loop);
    };

    return {
      title: 'Start Reactor',
      el: el('div', { class: 'mg' }, grid, status),
      start: () => { nextRound(); raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Unlock Manifolds: press 01..10 in order. */
function manifoldsGame() {
  return (ctx) => {
    const order = Array.from({ length: 10 }, (_, i) => i + 1).sort(() => Math.random() - 0.5);
    let next = 1;
    const grid = el('div', { class: 'mg-grid', style: { gridTemplateColumns: 'repeat(5, 1fr)' } });
    const buttons = order.map((n) => {
      const b = el('button', { class: 'mg-btn', style: { height: '86px', fontSize: '24px' } }, String(n).padStart(2, '0'));
      b.addEventListener('click', () => {
        if (n !== next) { sfx.deny(); return; }
        b.classList.add('done');
        b.disabled = true;
        next++;
        ctx.progress();
        if (next > 10) ctx.done();
      });
      grid.append(b);
      return b;
    });
    return {
      title: 'Unlock Manifolds',
      el: el('div', { class: 'mg' }, grid, el('p', { class: 'mg-hint' }, 'Press the numbers in order, 01 to 10.')),
    };
  };
}

/** Prime Shields: light up every hexagon. */
function shieldsGame() {
  return (ctx) => {
    const W = 420, H = 380;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const cells = [];
    for (let i = 0; i < 7; i++) {
      const a = (i / 7) * Math.PI * 2 - Math.PI / 2;
      cells.push({ x: W / 2 + Math.cos(a) * 108, y: H / 2 + Math.sin(a) * 108, on: Math.random() < 0.35 });
    }
    cells.push({ x: W / 2, y: H / 2, on: false });
    let raf = 0;

    const hex = (x, y, r) => {
      g.beginPath();
      for (let k = 0; k < 6; k++) {
        const a = (k / 6) * Math.PI * 2 - Math.PI / 2;
        const px = x + Math.cos(a) * r, py = y + Math.sin(a) * r;
        k === 0 ? g.moveTo(px, py) : g.lineTo(px, py);
      }
      g.closePath();
    };
    const draw = () => {
      g.fillStyle = '#0d1422'; g.fillRect(0, 0, W, H);
      for (const c of cells) {
        hex(c.x, c.y, 46);
        g.fillStyle = c.on ? 'rgba(90,220,255,0.75)' : 'rgba(80,110,160,0.25)';
        g.fill();
        g.strokeStyle = c.on ? '#9ceaff' : 'rgba(170,200,240,0.5)';
        g.lineWidth = 3;
        g.stroke();
      }
    };
    canvas.addEventListener('pointerdown', (e) => {
      const p = localPoint(canvas, e);
      for (const c of cells) {
        if (Math.hypot(c.x - p.x, c.y - p.y) < 44 && !c.on) {
          c.on = true;
          ctx.progress();
          if (cells.every((q) => q.on)) { cancelAnimationFrame(raf); ctx.done(); }
          break;
        }
      }
    });
    const loop = () => { draw(); raf = requestAnimationFrame(loop); };
    return {
      title: 'Prime Shields',
      el: el('div', { class: 'mg' }, canvas, el('p', { class: 'mg-hint' }, 'Tap every dim hexagon to bring the shields online.')),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Chart Course: trace the route node by node. */
function chartGame() {
  return (ctx) => {
    const W = 560, H = 320;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const nodes = [];
    const n = 6;
    for (let i = 0; i < n; i++) {
      nodes.push({ x: 70 + (i / (n - 1)) * (W - 140), y: 80 + Math.sin(i * 1.4) * 80 + Math.random() * 40 });
    }
    let next = 0, raf = 0;
    const draw = () => {
      g.fillStyle = '#050a16'; g.fillRect(0, 0, W, H);
      for (let i = 0; i < 60; i++) { g.fillStyle = 'rgba(255,255,255,0.28)'; g.fillRect((i * 71) % W, (i * 53) % H, 2, 2); }
      g.strokeStyle = 'rgba(120,200,255,0.35)';
      g.setLineDash([8, 8]); g.lineWidth = 2;
      g.beginPath();
      nodes.forEach((p, i) => (i === 0 ? g.moveTo(p.x, p.y) : g.lineTo(p.x, p.y)));
      g.stroke(); g.setLineDash([]);
      g.strokeStyle = '#35d18b'; g.lineWidth = 4;
      g.beginPath();
      for (let i = 0; i < next; i++) (i === 0 ? g.moveTo(nodes[i].x, nodes[i].y) : g.lineTo(nodes[i].x, nodes[i].y));
      g.stroke();
      nodes.forEach((p, i) => {
        g.fillStyle = i < next ? '#35d18b' : (i === next ? '#ffd65a' : 'rgba(180,210,255,0.4)');
        g.beginPath(); g.arc(p.x, p.y, i === next ? 16 : 11, 0, Math.PI * 2); g.fill();
      });
      g.fillStyle = '#8fa3c4';
      g.font = '600 15px Trebuchet MS, sans-serif'; g.textAlign = 'center';
      g.fillText('tap the yellow waypoint to plot the course', W / 2, H - 12);
    };
    canvas.addEventListener('pointerdown', (e) => {
      const p = localPoint(canvas, e);
      const target = nodes[next];
      if (target && Math.hypot(target.x - p.x, target.y - p.y) < 30) {
        next++;
        ctx.progress();
        if (next >= nodes.length) { cancelAnimationFrame(raf); ctx.done(); }
      } else sfx.deny();
    });
    const loop = () => { draw(); raf = requestAnimationFrame(loop); };
    return {
      title: 'Chart Course',
      el: el('div', { class: 'mg' }, canvas),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Inspect Sample: run the centrifuge, then pick the odd tube out. */
function sampleGame() {
  return (ctx) => {
    const TUBES = 5;
    const odd = Math.floor(Math.random() * TUBES);
    let phase = 'idle', t = 0, raf = 0, last = performance.now();
    const progress = bar(0);
    const status = el('p', { class: 'mg-hint' }, 'Start the sequence, then identify the anomalous sample.');
    const row = el('div', { class: 'mg-grid', style: { gridTemplateColumns: `repeat(${TUBES}, 1fr)` } });
    const tubes = [];
    for (let i = 0; i < TUBES; i++) {
      const b = el('button', { class: 'mg-btn', style: { height: '150px' } }, '?');
      b.disabled = true;
      b.addEventListener('click', () => {
        if (phase !== 'pick') return;
        if (i === odd) { b.classList.add('on'); cancelAnimationFrame(raf); ctx.done(); }
        else { sfx.deny(); b.classList.add('done'); b.disabled = true; }
      });
      tubes.push(b);
      row.append(b);
    }
    const startBtn = el('button', { class: 'mg-btn', style: { width: '220px' } }, 'Start Analysis');
    startBtn.addEventListener('click', () => {
      if (phase !== 'idle') return;
      phase = 'running'; t = 0; startBtn.disabled = true;
      status.textContent = 'Analysing samples…';
    });

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      if (phase === 'running') {
        t += dt;
        progress.set(t / 9);
        if (t >= 9) {
          phase = 'pick';
          status.textContent = 'One sample is contaminated. Pick it.';
          tubes.forEach((b, i) => {
            b.disabled = false;
            b.textContent = i === odd ? '⚠' : '✓';
            b.style.background = i === odd ? '#7a2b2b' : '#253049';
          });
        }
      }
      raf = requestAnimationFrame(loop);
    };
    return {
      title: 'Inspect Sample',
      el: el('div', { class: 'mg' }, progress.el, row, startBtn, status),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** O2 sabotage: type the code shown on the readout. */
function o2Game() {
  return (ctx) => {
    const code = String(Math.floor(1000 + Math.random() * 9000));
    let typed = '';
    const display = el('div', { style: { fontSize: '34px', letterSpacing: '10px', fontWeight: '800', minHeight: '44px' } }, '____');
    const codeEl = el('div', { style: { fontSize: '26px', letterSpacing: '8px', color: '#ffd65a', fontWeight: '800' } }, code);
    const pad = el('div', { class: 'mg-grid', style: { gridTemplateColumns: 'repeat(3, 1fr)', width: '260px' } });
    const refresh = () => { display.textContent = typed.padEnd(4, '_'); };
    for (const key of ['1', '2', '3', '4', '5', '6', '7', '8', '9', 'C', '0', '<']) {
      const b = el('button', { class: 'mg-btn' }, key);
      b.addEventListener('click', () => {
        sfx.click();
        if (key === 'C') typed = '';
        else if (key === '<') typed = typed.slice(0, -1);
        else if (typed.length < 4) typed += key;
        refresh();
        if (typed.length === 4) {
          if (typed === code) ctx.done();
          else { sfx.deny(); typed = ''; refresh(); }
        }
      });
      pad.append(b);
    }
    refresh();
    return {
      title: 'Restore Oxygen',
      el: el('div', { class: 'mg' },
        el('p', { class: 'mg-hint' }, 'Enter the code on the readout:'),
        codeEl, display, pad),
    };
  };
}

/** Reactor sabotage: hold your hand on the scanner. */
function reactorPadGame() {
  return (ctx) => {
    const progress = bar(0);
    const pad = el('button', { class: 'mg-btn', style: { width: '220px', height: '220px', borderRadius: '18px', fontSize: '15px' } }, 'HOLD HAND HERE');
    const status = el('p', { class: 'mg-hint' }, 'Both reactor pads must be held at the same time.');
    let held = false, value = 0, raf = 0, last = performance.now(), beat = 0;

    const down = (e) => { e.preventDefault(); held = true; pad.classList.add('on'); ctx.data.onHold?.(true); };
    const up = () => { held = false; pad.classList.remove('on'); value = 0; progress.set(0); ctx.data.onHold?.(false); };
    pad.addEventListener('pointerdown', down);
    window.addEventListener('pointerup', up);
    pad.addEventListener('pointerleave', up);

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      if (held) {
        value = Math.min(1, value + dt / 1.2);
        progress.set(value);
        beat -= dt;
        if (beat <= 0) { beat = 0.35; ctx.data.beat?.(); }
      }
      raf = requestAnimationFrame(loop);
    };
    return {
      title: 'Reactor Meltdown',
      el: el('div', { class: 'mg' }, progress.el, pad, status),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => { cancelAnimationFrame(raf); window.removeEventListener('pointerup', up); ctx.data.onHold?.(false); },
    };
  };
}

export const games = {
  simon: simonGame(),
  manifolds: manifoldsGame(),
  shields: shieldsGame(),
  chart: chartGame(),
  sample: sampleGame(),
  o2fix: o2Game(),
  reactorfix: reactorPadGame(),
};
