// public/js/minigames/wires.js
// Wire-dragging consoles: Fix Wiring, the lights breaker and comms tuning.

import { el, canvasEl, localPoint } from './index.js';
import { sfx } from '../sound.js';

const WIRE_COLORS = ['#e23b3b', '#3f7de0', '#ffd23f', '#e0459f', '#3fd07a'];

function wiresGame() {
  return (ctx) => {
    const W = 560, H = 360;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const count = 4;
    const order = [0, 1, 2, 3].sort(() => Math.random() - 0.5);
    const right = [0, 1, 2, 3].sort(() => Math.random() - 0.5);
    const connected = new Array(count).fill(false);
    let dragFrom = null, dragPos = null, raf = 0;

    const leftY = (i) => 54 + i * 84;
    const rightY = (i) => 54 + i * 84;

    const draw = () => {
      g.fillStyle = '#0d1422';
      g.fillRect(0, 0, W, H);
      g.fillStyle = '#1b2438';
      g.fillRect(0, 0, 70, H);
      g.fillRect(W - 70, 0, 70, H);

      for (let i = 0; i < count; i++) {
        const color = WIRE_COLORS[order[i]];
        // left terminal
        g.fillStyle = color;
        g.fillRect(20, leftY(i) - 16, 62, 32);
        // right terminal
        g.fillStyle = WIRE_COLORS[right[i]];
        g.fillRect(W - 82, rightY(i) - 16, 62, 32);
      }
      // completed wires
      g.lineWidth = 16;
      g.lineCap = 'round';
      for (let i = 0; i < count; i++) {
        if (!connected[i]) continue;
        const target = right.indexOf(order[i]);
        g.strokeStyle = WIRE_COLORS[order[i]];
        g.beginPath();
        g.moveTo(80, leftY(i));
        g.lineTo(W - 80, rightY(target));
        g.stroke();
      }
      if (dragFrom !== null && dragPos) {
        g.strokeStyle = WIRE_COLORS[order[dragFrom]];
        g.beginPath();
        g.moveTo(80, leftY(dragFrom));
        g.lineTo(dragPos.x, dragPos.y);
        g.stroke();
      }
      g.fillStyle = '#8fa3c4';
      g.font = '600 15px Trebuchet MS, sans-serif';
      g.textAlign = 'center';
      g.fillText('connect matching colours', W / 2, H - 12);
    };

    const onDown = (e) => {
      const p = localPoint(canvas, e);
      if (p.x > 130) return;
      for (let i = 0; i < count; i++) {
        if (connected[i]) continue;
        if (Math.abs(p.y - leftY(i)) < 34) { dragFrom = i; dragPos = p; canvas.setPointerCapture(e.pointerId); return; }
      }
    };
    const onMove = (e) => { if (dragFrom !== null) { dragPos = localPoint(canvas, e); } };
    const onUp = (e) => {
      if (dragFrom === null) return;
      const p = localPoint(canvas, e);
      if (p.x > W - 140) {
        for (let i = 0; i < count; i++) {
          if (Math.abs(p.y - rightY(i)) < 34 && right[i] === order[dragFrom]) {
            connected[dragFrom] = true;
            ctx.progress();
            break;
          }
        }
      }
      dragFrom = null; dragPos = null;
      if (connected.every(Boolean)) ctx.done();
    };
    canvas.addEventListener('pointerdown', onDown);
    canvas.addEventListener('pointermove', onMove);
    canvas.addEventListener('pointerup', onUp);

    const loop = () => { draw(); raf = requestAnimationFrame(loop); };
    return {
      title: 'Fix Wiring',
      el: el('div', { class: 'mg' }, canvas, el('p', { class: 'mg-hint' }, 'Drag each wire to the matching colour on the right.')),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Lights sabotage: every breaker must be up. */
function lightsGame() {
  return (ctx) => {
    const n = 5;
    const states = Array.from({ length: n }, () => Math.random() < 0.6);
    if (states.every(Boolean)) states[0] = false;
    const row = el('div', { class: 'mg-grid', style: { gridTemplateColumns: `repeat(${n}, 1fr)` } });
    const buttons = [];
    const refresh = () => {
      states.forEach((on, i) => {
        buttons[i].textContent = on ? 'ON' : 'OFF';
        buttons[i].classList.toggle('on', on);
      });
      if (states.every(Boolean)) ctx.done();
    };
    for (let i = 0; i < n; i++) {
      const b = el('button', { class: 'mg-btn', style: { height: '120px' } }, 'OFF');
      b.addEventListener('click', () => { states[i] = !states[i]; sfx.click(); refresh(); });
      buttons.push(b);
      row.append(b);
    }
    setTimeout(refresh, 0);
    return {
      title: 'Restore Lights',
      el: el('div', { class: 'mg' }, row, el('p', { class: 'mg-hint' }, 'Flip every breaker to ON.')),
    };
  };
}

/** Comms sabotage: line up two frequency dials. */
function commsGame() {
  return (ctx) => {
    const W = 520, H = 260;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const target = 0.25 + Math.random() * 0.5;
    let value = Math.random() < 0.5 ? target - 0.35 : target + 0.35;
    value = Math.max(0.02, Math.min(0.98, value));
    let dragging = false, raf = 0, matched = 0;

    const draw = () => {
      g.fillStyle = '#0d1422';
      g.fillRect(0, 0, W, H);
      const drawWave = (amp, phase, color, y, freq) => {
        g.strokeStyle = color;
        g.lineWidth = 3;
        g.beginPath();
        for (let x = 0; x <= W; x += 4) {
          const yy = y + Math.sin((x / W) * Math.PI * freq + phase) * amp;
          x === 0 ? g.moveTo(x, yy) : g.lineTo(x, yy);
        }
        g.stroke();
      };
      drawWave(42, 0, 'rgba(90,220,255,0.55)', 90, 4 + target * 12);
      drawWave(42, 0, '#ffd65a', 90, 4 + value * 12);
      // slider
      g.fillStyle = '#28324a';
      g.fillRect(40, 196, W - 80, 14);
      const x = 40 + (W - 80) * value;
      g.fillStyle = Math.abs(value - target) < 0.035 ? '#35d18b' : '#d8e4f7';
      g.beginPath(); g.arc(x, 203, 18, 0, Math.PI * 2); g.fill();
      g.fillStyle = '#8fa3c4';
      g.font = '600 15px Trebuchet MS, sans-serif';
      g.textAlign = 'center';
      g.fillText(matched > 0 ? `signal locked ${(matched * 100 / 1.2).toFixed(0)}%` : 'match the yellow wave to the blue one', W / 2, 240);
    };

    const setFromEvent = (e) => {
      const p = localPoint(canvas, e);
      value = Math.max(0, Math.min(1, (p.x - 40) / (W - 80)));
    };
    canvas.addEventListener('pointerdown', (e) => { dragging = true; setFromEvent(e); canvas.setPointerCapture(e.pointerId); });
    canvas.addEventListener('pointermove', (e) => { if (dragging) setFromEvent(e); });
    canvas.addEventListener('pointerup', () => { dragging = false; });

    let last = performance.now();
    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      if (Math.abs(value - target) < 0.035) {
        matched += dt;
        if (matched > 1.2) { cancelAnimationFrame(raf); ctx.done(); return; }
      } else matched = 0;
      draw();
      raf = requestAnimationFrame(loop);
    };

    return {
      title: 'Restore Comms',
      el: el('div', { class: 'mg' }, canvas, el('p', { class: 'mg-hint' }, 'Slide the dial until the waves overlap, then hold steady.')),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

export const games = {
  wires: wiresGame(),
  lightsfix: lightsGame(),
  commsfix: commsGame(),
};
