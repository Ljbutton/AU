// public/js/minigames/basic.js
// Hold-to-fill, progress and single-action consoles.

import { el, bar, canvasEl, localPoint } from './index.js';
import { sfx } from '../sound.js';

/** Generic "hold the lever / button" console. */
function holdGame({ title, label, seconds, hint, color = '#35d18b', release = false }) {
  return (ctx) => {
    const progress = bar(0);
    let held = false, value = 0, raf = 0, last = performance.now();
    const btn = el('button', { class: 'mg-btn', style: { width: '260px', height: '86px' } }, label);
    const status = el('p', { class: 'mg-hint' }, hint);

    const down = (e) => { e.preventDefault(); held = true; btn.classList.add('on'); };
    const up = () => {
      held = false;
      btn.classList.remove('on');
      if (release) { value = 0; progress.set(0); }
    };
    btn.addEventListener('pointerdown', down);
    window.addEventListener('pointerup', up);
    btn.addEventListener('pointerleave', up);

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      if (held) {
        value = Math.min(1, value + dt / seconds);
        progress.set(value);
        if (value >= 1) { cleanup(); ctx.done(); return; }
      }
      raf = requestAnimationFrame(loop);
    };
    const cleanup = () => { cancelAnimationFrame(raf); window.removeEventListener('pointerup', up); };

    return {
      title,
      el: el('div', { class: 'mg' }, progress.el, btn, status),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: cleanup,
    };
  };
}

/** Automatic progress once started (download / upload / scan). */
function progressGame({ title, label, seconds, hint, autoStart = false }) {
  return (ctx) => {
    const progress = bar(0);
    let running = autoStart, value = 0, raf = 0, last = performance.now();
    const pct = el('div', { style: { fontSize: '26px', fontWeight: '800' } }, '0%');
    const btn = el('button', { class: 'mg-btn', style: { width: '240px' } }, label);
    btn.addEventListener('click', () => { running = true; btn.disabled = true; sfx.click(); });

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000);
      last = now;
      if (running) {
        value = Math.min(1, value + dt / seconds);
        progress.set(value);
        pct.textContent = `${Math.round(value * 100)}%`;
        if (value >= 1) { cancelAnimationFrame(raf); ctx.done(); return; }
      }
      raf = requestAnimationFrame(loop);
    };

    return {
      title,
      el: el('div', { class: 'mg' }, pct, progress.el, autoStart ? null : btn, el('p', { class: 'mg-hint' }, hint)),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Swipe the ID card at a steady speed. */
function swipeGame() {
  return (ctx) => {
    const W = 520, H = 240;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const status = el('p', { class: 'mg-hint' }, 'Drag the card through the reader — not too fast, not too slow.');
    let cardX = 40, dragging = false, startX = 0, startT = 0, msg = '';
    let msgTimer = 0, raf = 0;

    const draw = () => {
      g.fillStyle = '#111827';
      g.fillRect(0, 0, W, H);
      // reader
      g.fillStyle = '#28324a';
      g.fillRect(0, 96, W, 74);
      g.fillStyle = '#1b2438';
      g.fillRect(0, 118, W, 30);
      g.fillStyle = msg === 'ACCEPTED' ? '#35d18b' : msg ? '#ff5a5a' : '#8fa3c4';
      g.font = '700 22px Trebuchet MS, sans-serif';
      g.textAlign = 'center';
      g.fillText(msg || 'SWIPE CARD', W / 2, 58);
      // card
      g.save();
      g.translate(cardX, 133);
      g.fillStyle = '#e8c85a';
      g.beginPath(); g.roundRect ? g.roundRect(-70, -44, 140, 88, 8) : g.rect(-70, -44, 140, 88); g.fill();
      g.fillStyle = '#0d1422';
      g.fillRect(-58, -30, 60, 18);
      g.fillStyle = '#b79a2f';
      g.fillRect(-58, 6, 100, 8);
      g.fillRect(-58, 20, 70, 8);
      g.restore();
    };

    const onDown = (e) => {
      const p = localPoint(canvas, e);
      if (Math.abs(p.x - cardX) < 80 && Math.abs(p.y - 133) < 60) {
        dragging = true; startX = p.x; startT = performance.now();
        canvas.setPointerCapture(e.pointerId);
      }
    };
    const onMove = (e) => {
      if (!dragging) return;
      const p = localPoint(canvas, e);
      cardX = Math.max(40, Math.min(W - 40, p.x));
    };
    const onUp = () => {
      if (!dragging) return;
      dragging = false;
      const travel = cardX - startX;
      const seconds = (performance.now() - startT) / 1000;
      const speed = travel / Math.max(0.03, seconds);
      if (travel < 260) msg = 'BAD READ';
      else if (speed < 140) msg = 'TOO SLOW';
      else if (speed > 2600) msg = 'TOO FAST';
      else msg = 'ACCEPTED';
      draw();
      if (msg === 'ACCEPTED') {
        ctx.done();
      } else {
        msgTimer = 1.4;
        sfx.deny();
        cardX = 40;
      }
    };
    canvas.addEventListener('pointerdown', onDown);
    canvas.addEventListener('pointermove', onMove);
    canvas.addEventListener('pointerup', onUp);

    const loop = () => {
      if (msgTimer > 0) { msgTimer -= 1 / 60; if (msgTimer <= 0) msg = ''; }
      draw();
      raf = requestAnimationFrame(loop);
    };

    return {
      title: 'Swipe Card',
      el: el('div', { class: 'mg' }, canvas, status),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Divert power: throw one breaker. */
function divertGame() {
  return (ctx) => {
    const rooms = ['Upper Engine', 'Lower Engine', 'Security', 'Shields', 'Weapons', 'O2', 'Navigation', 'Comms'];
    const target = Math.floor(Math.random() * rooms.length);
    const grid = el('div', { class: 'mg-grid', style: { gridTemplateColumns: 'repeat(4, 1fr)' } });
    rooms.forEach((name, i) => {
      const b = el('button', { class: 'mg-btn', style: { fontSize: '14px' } }, name);
      b.addEventListener('click', () => {
        if (i === target) { b.classList.add('on'); ctx.done(); }
        else { sfx.deny(); b.animate([{ transform: 'translateX(-4px)' }, { transform: 'translateX(4px)' }, { transform: 'none' }], 160); }
      });
      grid.append(b);
    });
    return {
      title: 'Divert Power',
      el: el('div', { class: 'mg' },
        el('p', { class: 'mg-hint' }, `Route power to: `, el('b', {}, rooms[target])),
        grid),
    };
  };
}

export const games = {
  download: progressGame({ title: 'Download Data', label: 'Start Download', seconds: 8, hint: 'Keep the console open until the transfer finishes.' }),
  upload: progressGame({ title: 'Upload Data', label: 'Start Upload', seconds: 8, hint: 'Keep the console open until the transfer finishes.' }),
  scan: progressGame({ title: 'Submit Scan', label: 'Begin Scan', seconds: 10, hint: 'Stand on the pad. Everyone nearby can see you do this.' }),
  garbage: holdGame({ title: 'Empty Garbage', label: 'PULL LEVER', seconds: 2.6, hint: 'Hold the lever down until the chute clears.', release: true }),
  fuelpick: holdGame({ title: 'Fill Fuel Can', label: 'HOLD TO FILL', seconds: 2.6, hint: 'Hold until the can is full.', release: true }),
  fuel: holdGame({ title: 'Fuel Engine', label: 'HOLD TO POUR', seconds: 2.6, hint: 'Hold to empty the can into the engine.', release: true }),
  accept: holdGame({ title: 'Accept Diverted Power', label: 'PULL BREAKER', seconds: 1.6, hint: 'Hold the breaker until power is accepted.', release: true }),
  swipe: swipeGame(),
  divert: divertGame(),
};