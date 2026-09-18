// public/js/minigames/dexterity.js
// Aim, drag and timing consoles.

import { el, canvasEl, localPoint } from './index.js';
import { sfx } from '../sound.js';

/** Align Engine Output: drag the slider onto the centre line. */
function alignGame() {
  return (ctx) => {
    const W = 460, H = 320;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    let y = 60 + Math.random() * 200;
    let dragging = false, hold = 0, raf = 0, last = performance.now();

    const draw = () => {
      g.fillStyle = '#0d1422'; g.fillRect(0, 0, W, H);
      g.strokeStyle = 'rgba(255,255,255,0.12)';
      for (let i = 0; i < 12; i++) {
        g.beginPath(); g.moveTo(90, 30 + i * 24); g.lineTo(W - 90, 30 + i * 24); g.stroke();
      }
      const centre = H / 2;
      g.strokeStyle = '#35d18b'; g.lineWidth = 3;
      g.setLineDash([10, 8]);
      g.beginPath(); g.moveTo(60, centre); g.lineTo(W - 60, centre); g.stroke();
      g.setLineDash([]);
      const ok = Math.abs(y - centre) < 12;
      g.fillStyle = ok ? '#35d18b' : '#ffd65a';
      g.fillRect(90, y - 12, W - 180, 24);
      g.fillStyle = '#0d1422';
      g.fillRect(W / 2 - 4, y - 12, 8, 24);
      g.fillStyle = '#8fa3c4';
      g.font = '600 15px Trebuchet MS, sans-serif'; g.textAlign = 'center';
      g.fillText(ok ? 'hold it there…' : 'drag the bar onto the dashed line', W / 2, H - 14);
    };

    const setY = (e) => { y = Math.max(40, Math.min(H - 50, localPoint(canvas, e).y)); };
    canvas.addEventListener('pointerdown', (e) => { dragging = true; setY(e); canvas.setPointerCapture(e.pointerId); });
    canvas.addEventListener('pointermove', (e) => { if (dragging) setY(e); });
    canvas.addEventListener('pointerup', () => { dragging = false; });

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      if (Math.abs(y - H / 2) < 12) {
        hold += dt;
        if (hold > 1) { cancelAnimationFrame(raf); ctx.done(); return; }
      } else hold = 0;
      draw();
      raf = requestAnimationFrame(loop);
    };
    return {
      title: 'Align Engine Output',
      el: el('div', { class: 'mg' }, canvas),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Stabilize Steering: pull the drifting reticle back to centre. */
function steeringGame() {
  return (ctx) => {
    const W = 460, H = 340;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    let px = W / 2 + (Math.random() - 0.5) * 240;
    let py = H / 2 + (Math.random() - 0.5) * 180;
    let dragging = false, hold = 0, raf = 0, last = performance.now(), drift = Math.random() * 6;

    const draw = () => {
      g.fillStyle = '#060c18'; g.fillRect(0, 0, W, H);
      for (let i = 0; i < 40; i++) {
        const sx = (i * 97) % W, sy = (i * 61 + (performance.now() / 40)) % H;
        g.fillStyle = 'rgba(255,255,255,0.35)';
        g.fillRect(sx, sy, 2, 2);
      }
      g.strokeStyle = 'rgba(120,200,255,0.4)';
      g.lineWidth = 2;
      g.beginPath(); g.arc(W / 2, H / 2, 34, 0, Math.PI * 2); g.stroke();
      g.beginPath(); g.moveTo(W / 2 - 60, H / 2); g.lineTo(W / 2 + 60, H / 2); g.stroke();
      g.beginPath(); g.moveTo(W / 2, H / 2 - 60); g.lineTo(W / 2, H / 2 + 60); g.stroke();
      const ok = Math.hypot(px - W / 2, py - H / 2) < 26;
      g.strokeStyle = ok ? '#35d18b' : '#ffd65a';
      g.lineWidth = 4;
      g.beginPath(); g.arc(px, py, 18, 0, Math.PI * 2); g.stroke();
      g.beginPath(); g.moveTo(px - 26, py); g.lineTo(px + 26, py); g.stroke();
      g.beginPath(); g.moveTo(px, py - 26); g.lineTo(px, py + 26); g.stroke();
    };

    const setPos = (e) => { const p = localPoint(canvas, e); px = p.x; py = p.y; };
    canvas.addEventListener('pointerdown', (e) => { dragging = true; setPos(e); canvas.setPointerCapture(e.pointerId); });
    canvas.addEventListener('pointermove', (e) => { if (dragging) setPos(e); });
    canvas.addEventListener('pointerup', () => { dragging = false; });

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      if (!dragging) { drift += dt * 2; px += Math.cos(drift) * 40 * dt; py += Math.sin(drift * 1.3) * 34 * dt; }
      if (Math.hypot(px - W / 2, py - H / 2) < 26) {
        hold += dt;
        if (hold > 1.1) { cancelAnimationFrame(raf); ctx.done(); return; }
      } else hold = 0;
      draw();
      raf = requestAnimationFrame(loop);
    };
    return {
      title: 'Stabilize Steering',
      el: el('div', { class: 'mg' }, canvas, el('p', { class: 'mg-hint' }, 'Drag the reticle into the ring and hold it steady.')),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Calibrate Distributor: stop each spinning dial at the top. */
function calibrateGame() {
  return (ctx) => {
    const W = 520, H = 220;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const dials = [0, 1, 2].map((i) => ({ a: Math.random() * Math.PI * 2, speed: 2.4 + i * 0.5, done: false }));
    let current = 0, raf = 0, last = performance.now(), flash = 0;

    const draw = () => {
      g.fillStyle = '#0d1422'; g.fillRect(0, 0, W, H);
      dials.forEach((d, i) => {
        const cx = 110 + i * 150, cy = H / 2;
        g.strokeStyle = d.done ? '#35d18b' : (i === current ? '#ffd65a' : 'rgba(255,255,255,0.25)');
        g.lineWidth = 6;
        g.beginPath(); g.arc(cx, cy, 52, 0, Math.PI * 2); g.stroke();
        // target notch
        g.fillStyle = '#35d18b';
        g.fillRect(cx - 5, cy - 62, 10, 18);
        // needle
        g.strokeStyle = d.done ? '#35d18b' : '#d8e4f7';
        g.lineWidth = 5;
        g.beginPath();
        g.moveTo(cx, cy);
        g.lineTo(cx + Math.cos(d.a - Math.PI / 2) * 46, cy + Math.sin(d.a - Math.PI / 2) * 46);
        g.stroke();
      });
      g.fillStyle = flash > 0 ? '#ff5a5a' : '#8fa3c4';
      g.font = '600 15px Trebuchet MS, sans-serif'; g.textAlign = 'center';
      g.fillText(flash > 0 ? 'missed — try again' : 'click when the needle points at the green notch', W / 2, H - 12);
    };

    const attempt = () => {
      const d = dials[current];
      if (!d || d.done) return;
      const a = ((d.a % (Math.PI * 2)) + Math.PI * 2) % (Math.PI * 2);
      if (a < 0.35 || a > Math.PI * 2 - 0.35) {
        d.done = true;
        ctx.progress();
        current++;
        if (current >= dials.length) { cancelAnimationFrame(raf); ctx.done(); }
      } else { flash = 0.7; sfx.deny(); }
    };
    canvas.addEventListener('pointerdown', attempt);

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      if (flash > 0) flash -= dt;
      for (const d of dials) if (!d.done) d.a += d.speed * dt;
      draw();
      raf = requestAnimationFrame(loop);
    };
    return {
      title: 'Calibrate Distributor',
      el: el('div', { class: 'mg' }, canvas),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Clear Asteroids: shoot them all. */
function asteroidsGame() {
  return (ctx) => {
    const W = 560, H = 340;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const rocks = [];
    let score = 0, raf = 0, last = performance.now(), shots = [];
    const NEED = 16;
    const spawn = () => {
      const edge = Math.random() * Math.PI * 2;
      rocks.push({
        x: W / 2 + Math.cos(edge) * 420, y: H / 2 + Math.sin(edge) * 300,
        vx: -Math.cos(edge) * (40 + Math.random() * 40), vy: -Math.sin(edge) * (30 + Math.random() * 30),
        r: 16 + Math.random() * 12, spin: Math.random() * 3,
      });
    };
    for (let i = 0; i < 6; i++) spawn();

    const draw = () => {
      g.fillStyle = '#04070f'; g.fillRect(0, 0, W, H);
      for (let i = 0; i < 50; i++) {
        g.fillStyle = 'rgba(255,255,255,0.3)';
        g.fillRect((i * 83) % W, (i * 47) % H, 2, 2);
      }
      for (const r of rocks) {
        g.save();
        g.translate(r.x, r.y);
        g.rotate(r.spin);
        g.fillStyle = '#6a6156';
        g.beginPath();
        for (let k = 0; k < 7; k++) {
          const a = (k / 7) * Math.PI * 2;
          const rr = r.r * (0.78 + ((k * 37) % 10) / 34);
          k === 0 ? g.moveTo(Math.cos(a) * rr, Math.sin(a) * rr) : g.lineTo(Math.cos(a) * rr, Math.sin(a) * rr);
        }
        g.closePath(); g.fill();
        g.strokeStyle = '#3b352d'; g.lineWidth = 3; g.stroke();
        g.restore();
      }
      for (const s of shots) {
        g.strokeStyle = `rgba(120,240,255,${s.life})`;
        g.lineWidth = 3;
        g.beginPath(); g.moveTo(W / 2, H); g.lineTo(s.x, s.y); g.stroke();
      }
      g.fillStyle = '#8fa3c4';
      g.font = '700 16px Trebuchet MS, sans-serif'; g.textAlign = 'center';
      g.fillText(`${score} / ${NEED}`, W / 2, 24);
    };

    canvas.addEventListener('pointerdown', (e) => {
      const p = localPoint(canvas, e);
      shots.push({ x: p.x, y: p.y, life: 1 });
      sfx.click();
      for (let i = rocks.length - 1; i >= 0; i--) {
        if (Math.hypot(rocks[i].x - p.x, rocks[i].y - p.y) < rocks[i].r + 8) {
          rocks.splice(i, 1);
          score++;
          spawn();
          if (score >= NEED) { cancelAnimationFrame(raf); ctx.done(); }
          break;
        }
      }
    });

    const loop = (now) => {
      const dt = Math.min(0.05, (now - last) / 1000); last = now;
      for (const r of rocks) {
        r.x += r.vx * dt; r.y += r.vy * dt; r.spin += dt;
        if (r.x < -60 || r.x > W + 60 || r.y < -60 || r.y > H + 60) {
          const edge = Math.random() * Math.PI * 2;
          r.x = W / 2 + Math.cos(edge) * 420; r.y = H / 2 + Math.sin(edge) * 300;
          r.vx = -Math.cos(edge) * 60; r.vy = -Math.sin(edge) * 45;
        }
      }
      shots = shots.filter((s) => (s.life -= dt * 4) > 0);
      draw();
      raf = requestAnimationFrame(loop);
    };
    return {
      title: 'Clear Asteroids',
      el: el('div', { class: 'mg' }, canvas, el('p', { class: 'mg-hint' }, 'Tap the asteroids to shoot them down.')),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

/** Clean O2 Filter: drag the leaves into the chute. */
function leavesGame() {
  return (ctx) => {
    const W = 520, H = 340;
    const canvas = canvasEl(W, H);
    const g = canvas.getContext('2d');
    const leaves = Array.from({ length: 6 }, () => ({
      x: 90 + Math.random() * 260, y: 60 + Math.random() * 220,
      a: Math.random() * Math.PI, gone: false,
    }));
    let drag = null, raf = 0;

    const draw = () => {
      g.fillStyle = '#0d1422'; g.fillRect(0, 0, W, H);
      // filter chamber
      g.strokeStyle = 'rgba(140,200,255,0.4)'; g.lineWidth = 4;
      g.strokeRect(50, 30, 320, 280);
      // chute
      g.fillStyle = '#1d2a3f';
      g.fillRect(W - 120, 30, 100, 280);
      g.fillStyle = '#35d18b';
      g.font = '700 14px Trebuchet MS, sans-serif'; g.textAlign = 'center';
      g.fillText('CHUTE', W - 70, 170);
      for (const l of leaves) {
        if (l.gone) continue;
        g.save(); g.translate(l.x, l.y); g.rotate(l.a);
        g.fillStyle = '#4e8f3d';
        g.beginPath(); g.ellipse(0, 0, 24, 12, 0, 0, Math.PI * 2); g.fill();
        g.strokeStyle = '#2d5a24'; g.lineWidth = 3; g.stroke();
        g.beginPath(); g.moveTo(-22, 0); g.lineTo(22, 0); g.stroke();
        g.restore();
      }
      const left = leaves.filter((l) => !l.gone).length;
      g.fillStyle = '#8fa3c4';
      g.font = '600 15px Trebuchet MS, sans-serif';
      g.fillText(`${left} leaves left — drag them to the chute`, W / 2, H - 12);
    };

    canvas.addEventListener('pointerdown', (e) => {
      const p = localPoint(canvas, e);
      for (const l of leaves) {
        if (!l.gone && Math.hypot(l.x - p.x, l.y - p.y) < 30) { drag = l; canvas.setPointerCapture(e.pointerId); break; }
      }
    });
    canvas.addEventListener('pointermove', (e) => {
      if (!drag) return;
      const p = localPoint(canvas, e);
      drag.x = p.x; drag.y = p.y;
    });
    canvas.addEventListener('pointerup', () => {
      if (drag && drag.x > W - 130) {
        drag.gone = true;
        ctx.progress();
        if (leaves.every((l) => l.gone)) { cancelAnimationFrame(raf); ctx.done(); }
      }
      drag = null;
    });

    const loop = () => { draw(); raf = requestAnimationFrame(loop); };
    return {
      title: 'Clean O2 Filter',
      el: el('div', { class: 'mg' }, canvas),
      start: () => { raf = requestAnimationFrame(loop); },
      destroy: () => cancelAnimationFrame(raf),
    };
  };
}

export const games = {
  align: alignGame(),
  steering: steeringGame(),
  calibrate: calibrateGame(),
  asteroids: asteroidsGame(),
  leaves: leavesGame(),
};
