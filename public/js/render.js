// public/js/render.js - all world drawing: the ship, its crew, and the dark.

import { ROOMS, HALLS, RECTS, WALLS, DOORS, DOOR_BY_ID, VENTS, EMERGENCY_BUTTON, ADMIN_TABLE, WORLD, FIX_POINTS } from '../../shared/map.js';
import { allConsolePositions, currentStep } from '../../shared/tasks.js';
import { visibilityPolygon, segmentsNear, clamp } from '../../shared/geom.js';
import { VISION, PLAYER_RADIUS } from '../../shared/constants.js';
import { drawCrewmate, drawCorpse, roundRect } from './sprites.js';
import { state } from './net.js';

const CONSOLES = allConsolePositions();
const FIX_LIST = Object.entries(FIX_POINTS).flatMap(([kind, pts]) => pts.map((p) => ({ ...p, kind })));

export const camera = { x: 1580, y: 360, scale: 1 };
const effects = [];
let time = 0;

export function addEffect(type, x, y, opts = {}) {
  effects.push({ type, x, y, life: 0, ttl: opts.ttl ?? 0.8, ...opts });
}

export function resize(canvas) {
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const w = canvas.clientWidth || window.innerWidth;
  const h = canvas.clientHeight || window.innerHeight;
  canvas.width = Math.floor(w * dpr);
  canvas.height = Math.floor(h * dpr);
  return { w, h, dpr };
}

function visionRadius() {
  const s = state.settings || {};
  const me = state.players.get(state.you);
  if (me && !me.alive) return 1e9;
  if (state.role === 'impostor') return VISION.impostor * (s.impostorVision ?? 1.5);
  const lightsOut = state.sabotage && state.sabotage.kind === 'lights';
  return VISION.crew * (s.crewVision ?? 1) * (lightsOut ? VISION.lightsOut : 1);
}

/** Wall segments that currently block sight/movement (walls + shut doors). */
function blockers(x, y, r) {
  const segs = segmentsNear(x, y, r, WALLS, []);
  for (const id of state.closedDoors) {
    const d = DOOR_BY_ID.get(id);
    if (d) segs.push(d);
  }
  return segs;
}

// ---------------------------------------------------------------------------
// Ship furniture
// ---------------------------------------------------------------------------

function floorPattern(ctx) {
  const c = document.createElement('canvas');
  c.width = c.height = 48;
  const g = c.getContext('2d');
  g.fillStyle = '#20293c';
  g.fillRect(0, 0, 48, 48);
  g.strokeStyle = 'rgba(255,255,255,0.04)';
  g.lineWidth = 2;
  g.strokeRect(1, 1, 46, 46);
  g.fillStyle = 'rgba(255,255,255,0.02)';
  g.fillRect(4, 4, 18, 18);
  g.fillRect(26, 26, 18, 18);
  return ctx.createPattern(c, 'repeat');
}
let pattern = null;

function drawFloors(ctx) {
  if (!pattern) pattern = floorPattern(ctx);
  ctx.save();
  ctx.fillStyle = pattern;
  for (const r of RECTS) ctx.fillRect(r.x1, r.y1, r.x2 - r.x1, r.y2 - r.y1);
  // rooms get a warmer wash than corridors so they read as places
  for (const r of ROOMS) {
    const g = ctx.createLinearGradient(r.x1, r.y1, r.x2, r.y2);
    g.addColorStop(0, 'rgba(90,120,180,0.10)');
    g.addColorStop(1, 'rgba(40,60,110,0.05)');
    ctx.fillStyle = g;
    ctx.fillRect(r.x1, r.y1, r.x2 - r.x1, r.y2 - r.y1);
  }
  ctx.restore();
}

function drawWalls(ctx) {
  ctx.save();
  ctx.lineCap = 'round';
  ctx.strokeStyle = '#0a0d16';
  ctx.lineWidth = 16;
  for (const s of WALLS) { ctx.beginPath(); ctx.moveTo(s.x1, s.y1); ctx.lineTo(s.x2, s.y2); ctx.stroke(); }
  ctx.strokeStyle = '#59708f';
  ctx.lineWidth = 6;
  for (const s of WALLS) { ctx.beginPath(); ctx.moveTo(s.x1, s.y1); ctx.lineTo(s.x2, s.y2); ctx.stroke(); }
  ctx.strokeStyle = 'rgba(190,220,255,0.28)';
  ctx.lineWidth = 2;
  for (const s of WALLS) { ctx.beginPath(); ctx.moveTo(s.x1, s.y1); ctx.lineTo(s.x2, s.y2); ctx.stroke(); }
  ctx.restore();
}

function drawDoors(ctx) {
  for (const d of DOORS) {
    const closed = state.closedDoors.has(d.id);
    const horiz = d.y1 === d.y2;
    const len = horiz ? d.x2 - d.x1 : d.y2 - d.y1;
    ctx.save();
    ctx.translate((d.x1 + d.x2) / 2, (d.y1 + d.y2) / 2);
    if (!horiz) ctx.rotate(Math.PI / 2);
    // rails
    ctx.fillStyle = 'rgba(120,150,190,0.25)';
    ctx.fillRect(-len / 2, -13, len, 4);
    ctx.fillRect(-len / 2, 9, len, 4);
    if (closed) {
      const g = ctx.createLinearGradient(0, -12, 0, 12);
      g.addColorStop(0, '#93a7c4');
      g.addColorStop(0.5, '#6d82a3');
      g.addColorStop(1, '#4a5c78');
      ctx.fillStyle = g;
      roundRect(ctx, -len / 2, -11, len, 22, 5);
      ctx.fill();
      ctx.strokeStyle = '#26303f';
      ctx.lineWidth = 3;
      ctx.stroke();
      ctx.fillStyle = 'rgba(255,90,90,0.9)';
      for (let i = -len / 2 + 14; i < len / 2 - 6; i += 26) ctx.fillRect(i, -3, 12, 6);
    } else {
      ctx.fillStyle = 'rgba(140,175,220,0.5)';
      roundRect(ctx, -len / 2, -11, 10, 22, 4); ctx.fill();
      roundRect(ctx, len / 2 - 10, -11, 10, 22, 4); ctx.fill();
    }
    ctx.restore();
  }
}

function panel(ctx, x, y, w, h, body, screen) {
  ctx.fillStyle = body;
  roundRect(ctx, x - w / 2, y - h / 2, w, h, 6);
  ctx.fill();
  ctx.strokeStyle = 'rgba(0,0,0,0.5)';
  ctx.lineWidth = 3;
  ctx.stroke();
  if (screen) {
    ctx.fillStyle = screen;
    roundRect(ctx, x - w / 2 + 6, y - h / 2 + 6, w - 12, h - 12, 4);
    ctx.fill();
  }
}

function drawRoomDecor(ctx, r) {
  const cx = (r.x1 + r.x2) / 2, cy = (r.y1 + r.y2) / 2;
  const t = time;
  switch (r.kind) {
    case 'reactor': {
      const pulse = 0.55 + Math.sin(t * 2) * 0.18;
      const g = ctx.createRadialGradient(cx, cy, 8, cx, cy, 92);
      g.addColorStop(0, `rgba(120,220,255,${pulse})`);
      g.addColorStop(0.5, 'rgba(60,140,220,0.35)');
      g.addColorStop(1, 'rgba(30,70,140,0)');
      ctx.fillStyle = g;
      ctx.beginPath(); ctx.arc(cx, cy, 92, 0, Math.PI * 2); ctx.fill();
      ctx.strokeStyle = '#7fd4ff'; ctx.lineWidth = 5;
      ctx.beginPath(); ctx.arc(cx, cy, 46, 0, Math.PI * 2); ctx.stroke();
      ctx.fillStyle = '#28354d';
      for (const [px, py] of [[r.x1 + 44, r.y1 + 44], [r.x2 - 44, r.y1 + 44], [r.x1 + 44, r.y2 - 44], [r.x2 - 44, r.y2 - 44]]) {
        ctx.beginPath(); ctx.arc(px, py, 20, 0, Math.PI * 2); ctx.fill();
      }
      // handprint pads
      for (const p of FIX_POINTS.reactor) panel(ctx, p.x, p.y, 54, 38, '#2c3a52', '#3f6fa5');
      break;
    }
    case 'engine': {
      for (const dy of [-1, 1]) {
        const y = cy + dy * 62;
        ctx.fillStyle = '#334259';
        roundRect(ctx, r.x1 + 40, y - 26, (r.x2 - r.x1) - 80, 52, 14); ctx.fill();
        ctx.fillStyle = `rgba(255,${140 + Math.sin(t * 6 + dy) * 40},60,0.75)`;
        ctx.beginPath(); ctx.ellipse(r.x1 + 52, y, 13, 18, 0, 0, Math.PI * 2); ctx.fill();
        ctx.strokeStyle = 'rgba(255,190,120,0.5)'; ctx.lineWidth = 3;
        ctx.beginPath(); ctx.moveTo(r.x1 + 70, y); ctx.lineTo(r.x2 - 56, y); ctx.stroke();
      }
      break;
    }
    case 'security': {
      for (let i = 0; i < 4; i++) {
        const x = r.x1 + 44 + (i % 2) * 74, y = r.y1 + 44 + Math.floor(i / 2) * 60;
        panel(ctx, x, y, 62, 46, '#222c3f', i % 2 ? '#1d4d55' : '#1d3d62');
        ctx.fillStyle = `rgba(120,230,220,${0.12 + 0.1 * Math.sin(t * 5 + i)})`;
        ctx.fillRect(x - 24, y - 16, 48, 32);
      }
      break;
    }
    case 'medbay': {
      for (let i = 0; i < 2; i++) {
        const x = r.x1 + 90 + i * 100;
        ctx.fillStyle = '#d7e4f5';
        roundRect(ctx, x - 26, r.y2 - 92, 52, 76, 10); ctx.fill();
        ctx.fillStyle = '#9db4d2';
        roundRect(ctx, x - 26, r.y2 - 92, 52, 22, 10); ctx.fill();
      }
      // scan pad
      ctx.strokeStyle = `rgba(120,230,160,${0.5 + 0.3 * Math.sin(t * 3)})`;
      ctx.lineWidth = 4;
      ctx.beginPath(); ctx.arc(960, 270, 32, 0, Math.PI * 2); ctx.stroke();
      break;
    }
    case 'electrical': {
      for (let i = 0; i < 5; i++) {
        const x = r.x1 + 46 + i * 62;
        panel(ctx, x, r.y1 + 40, 50, 46, '#2a3145', '#5c4a1f');
        ctx.strokeStyle = 'rgba(255,220,120,0.35)';
        ctx.lineWidth = 2;
        ctx.beginPath(); ctx.moveTo(x, r.y1 + 64); ctx.lineTo(x, r.y1 + 110); ctx.stroke();
      }
      ctx.fillStyle = '#3a4560';
      roundRect(ctx, r.x1 + 30, r.y2 - 96, 120, 70, 10); ctx.fill();
      ctx.fillStyle = '#26304a';
      roundRect(ctx, r.x2 - 150, r.y2 - 96, 120, 70, 10); ctx.fill();
      break;
    }
    case 'cafeteria': {
      // the big table with the emergency button in the middle
      ctx.fillStyle = '#39465f';
      ctx.beginPath(); ctx.ellipse(EMERGENCY_BUTTON.x, EMERGENCY_BUTTON.y, 150, 118, 0, 0, Math.PI * 2); ctx.fill();
      ctx.fillStyle = '#4b5b79';
      ctx.beginPath(); ctx.ellipse(EMERGENCY_BUTTON.x, EMERGENCY_BUTTON.y - 6, 140, 108, 0, 0, Math.PI * 2); ctx.fill();
      for (let i = 0; i < 8; i++) {
        const a = (i / 8) * Math.PI * 2;
        ctx.fillStyle = '#2e3a51';
        ctx.beginPath();
        ctx.arc(EMERGENCY_BUTTON.x + Math.cos(a) * 178, EMERGENCY_BUTTON.y + Math.sin(a) * 146, 20, 0, Math.PI * 2);
        ctx.fill();
      }
      const glow = 0.5 + Math.sin(t * 3) * 0.25;
      ctx.fillStyle = `rgba(220,50,60,${glow})`;
      ctx.beginPath(); ctx.arc(EMERGENCY_BUTTON.x, EMERGENCY_BUTTON.y, 40, 0, Math.PI * 2); ctx.fill();
      ctx.fillStyle = '#e03c3c';
      ctx.beginPath(); ctx.arc(EMERGENCY_BUTTON.x, EMERGENCY_BUTTON.y, 26, 0, Math.PI * 2); ctx.fill();
      ctx.strokeStyle = '#ffd9d9'; ctx.lineWidth = 4;
      ctx.beginPath(); ctx.arc(EMERGENCY_BUTTON.x, EMERGENCY_BUTTON.y, 26, 0, Math.PI * 2); ctx.stroke();
      // garbage chute
      panel(ctx, 1420, 520, 56, 44, '#37445c', '#1f2a3c');
      break;
    }
    case 'storage': {
      const crates = [[70, 70], [140, 70], [70, 140], [300, 300], [230, 340], [300, 200]];
      for (const [ox, oy] of crates) {
        const x = r.x1 + ox, y = r.y1 + oy;
        if (x > r.x2 - 30 || y > r.y2 - 30) continue;
        ctx.fillStyle = '#5a4a32';
        roundRect(ctx, x - 26, y - 26, 52, 52, 6); ctx.fill();
        ctx.strokeStyle = '#3a301f'; ctx.lineWidth = 3; ctx.stroke();
        ctx.strokeStyle = 'rgba(255,220,150,0.2)';
        ctx.beginPath(); ctx.moveTo(x - 26, y); ctx.lineTo(x + 26, y); ctx.stroke();
      }
      panel(ctx, 1370, 1120, 56, 46, '#2f3b52', '#4a6a30');
      break;
    }
    case 'admin': {
      ctx.fillStyle = '#3a4762';
      roundRect(ctx, ADMIN_TABLE.x - 96, ADMIN_TABLE.y - 56, 192, 112, 14); ctx.fill();
      ctx.fillStyle = 'rgba(90,200,255,0.16)';
      roundRect(ctx, ADMIN_TABLE.x - 82, ADMIN_TABLE.y - 44, 164, 88, 10); ctx.fill();
      ctx.strokeStyle = `rgba(120,220,255,${0.4 + 0.2 * Math.sin(t * 4)})`;
      ctx.lineWidth = 2;
      ctx.strokeRect(ADMIN_TABLE.x - 70, ADMIN_TABLE.y - 34, 140, 68);
      panel(ctx, 1830, 840, 50, 40, '#2c3a52', '#a8b6cc');
      break;
    }
    case 'weapons': {
      ctx.fillStyle = '#131c2e';
      ctx.beginPath(); ctx.ellipse(cx, r.y1 + 66, 116, 46, 0, 0, Math.PI * 2); ctx.fill();
      ctx.fillStyle = 'rgba(120,180,255,0.14)';
      ctx.beginPath(); ctx.ellipse(cx, r.y1 + 66, 110, 40, 0, 0, Math.PI * 2); ctx.fill();
      for (let i = 0; i < 4; i++) {
        const a = t * 0.7 + i * 1.7;
        ctx.fillStyle = 'rgba(220,240,255,0.7)';
        ctx.beginPath();
        ctx.arc(cx + Math.cos(a) * 80, r.y1 + 66 + Math.sin(a * 1.3) * 26, 3, 0, Math.PI * 2);
        ctx.fill();
      }
      panel(ctx, 2070, 210, 74, 50, '#2b3750', '#0f3050');
      break;
    }
    case 'o2': {
      for (let i = 0; i < 3; i++) {
        const x = r.x1 + 50 + i * 58;
        ctx.fillStyle = 'rgba(80,220,160,0.22)';
        roundRect(ctx, x - 20, r.y1 + 24, 40, 62, 18); ctx.fill();
        ctx.strokeStyle = 'rgba(140,255,200,0.5)'; ctx.lineWidth = 3; ctx.stroke();
      }
      for (const p of FIX_POINTS.o2) panel(ctx, p.x, p.y, 44, 34, '#2b3a4e', '#3f8f7a');
      break;
    }
    case 'nav': {
      ctx.fillStyle = '#101a2c';
      roundRect(ctx, r.x2 - 46, r.y1 + 34, 30, (r.y2 - r.y1) - 68, 12); ctx.fill();
      ctx.fillStyle = 'rgba(140,200,255,0.16)';
      roundRect(ctx, r.x2 - 42, r.y1 + 40, 22, (r.y2 - r.y1) - 80, 10); ctx.fill();
      panel(ctx, 2560, 390, 70, 46, '#2b3750', '#204a6e');
      panel(ctx, 2560, 570, 70, 46, '#2b3750', '#204a6e');
      break;
    }
    case 'shields': {
      for (let i = 0; i < 7; i++) {
        const a = (i / 7) * Math.PI * 2;
        const x = cx + Math.cos(a) * 66, y = cy - 20 + Math.sin(a) * 52;
        ctx.strokeStyle = `rgba(110,230,255,${0.3 + 0.25 * Math.sin(t * 3 + i)})`;
        ctx.lineWidth = 4;
        ctx.beginPath();
        for (let k = 0; k < 6; k++) {
          const ha = (k / 6) * Math.PI * 2;
          const hx = x + Math.cos(ha) * 17, hy = y + Math.sin(ha) * 17;
          k === 0 ? ctx.moveTo(hx, hy) : ctx.lineTo(hx, hy);
        }
        ctx.closePath(); ctx.stroke();
      }
      break;
    }
    case 'comms': {
      panel(ctx, 1830, 1030, 66, 48, '#2a3448', '#2d5f6b');
      ctx.strokeStyle = 'rgba(140,230,255,0.4)';
      ctx.lineWidth = 3;
      for (let i = 1; i <= 3; i++) {
        ctx.beginPath();
        ctx.arc(r.x2 - 60, r.y1 + 46, i * 18, -Math.PI * 0.75, -Math.PI * 0.25);
        ctx.stroke();
      }
      break;
    }
    default: break;
  }
}

function drawRoomLabels(ctx) {
  ctx.save();
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  for (const r of ROOMS) {
    const cx = (r.x1 + r.x2) / 2, cy = r.y1 + 26;
    ctx.font = '700 22px Trebuchet MS, sans-serif';
    ctx.fillStyle = 'rgba(190,215,255,0.30)';
    ctx.fillText(r.name.toUpperCase(), cx, cy);
  }
  ctx.restore();
}

function drawVents(ctx) {
  for (const v of VENTS) {
    ctx.save();
    ctx.translate(v.x, v.y);
    ctx.fillStyle = '#1b2436';
    roundRect(ctx, -22, -18, 44, 36, 6); ctx.fill();
    ctx.strokeStyle = '#6a7d9b'; ctx.lineWidth = 3; ctx.stroke();
    ctx.strokeStyle = 'rgba(200,220,255,0.35)';
    ctx.lineWidth = 3;
    for (let i = -1; i <= 1; i++) {
      ctx.beginPath(); ctx.moveTo(-14, i * 9); ctx.lineTo(14, i * 9); ctx.stroke();
    }
    ctx.restore();
  }
}

function drawConsoles(ctx) {
  const pending = new Set();
  for (const task of state.tasks || []) {
    const s = currentStep(task);
    if (s) pending.add(`${Math.round(s.x)},${Math.round(s.y)}`);
  }
  for (const c of CONSOLES) {
    const key = `${Math.round(c.x)},${Math.round(c.y)}`;
    const mine = pending.has(key);
    ctx.save();
    ctx.translate(c.x, c.y);
    if (mine) {
      const pulse = 0.35 + Math.sin(time * 4) * 0.2;
      ctx.fillStyle = `rgba(255,214,90,${pulse})`;
      ctx.beginPath(); ctx.arc(0, 0, 40, 0, Math.PI * 2); ctx.fill();
    }
    ctx.fillStyle = mine ? '#4a4022' : '#28324a';
    roundRect(ctx, -19, -15, 38, 30, 5); ctx.fill();
    ctx.strokeStyle = mine ? '#ffd65a' : 'rgba(150,180,220,0.5)';
    ctx.lineWidth = 3; ctx.stroke();
    ctx.fillStyle = mine ? '#ffe7a1' : 'rgba(150,190,240,0.45)';
    ctx.fillRect(-11, -8, 22, 16);
    ctx.restore();
  }
  // sabotage consoles glow while their system is down
  for (const f of FIX_LIST) {
    if (!state.sabotage || state.sabotage.kind !== f.kind) continue;
    const pulse = 0.4 + Math.sin(time * 7) * 0.3;
    ctx.fillStyle = `rgba(255,70,70,${pulse})`;
    ctx.beginPath(); ctx.arc(f.x, f.y, 46, 0, Math.PI * 2); ctx.fill();
  }
}

// ---------------------------------------------------------------------------
// Characters
// ---------------------------------------------------------------------------

function drawNameTag(ctx, p, screen) {
  ctx.save();
  ctx.font = '700 15px Trebuchet MS, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  const impostorMate = state.impostors.includes(p.id) && state.impostors.includes(state.you);
  const label = p.name + (p.bot ? ' •' : '');
  const w = ctx.measureText(label).width + 14;
  ctx.fillStyle = 'rgba(6,9,16,0.6)';
  roundRect(ctx, screen.x - w / 2, screen.y - 10, w, 20, 6);
  ctx.fill();
  ctx.fillStyle = impostorMate ? '#ff6b6b' : (p.ghost ? 'rgba(220,235,255,0.6)' : '#e8eef9');
  ctx.fillText(label, screen.x, screen.y);
  ctx.restore();
}

function drawEffects(ctx, dt) {
  for (let i = effects.length - 1; i >= 0; i--) {
    const e = effects[i];
    e.life += dt;
    const k = e.life / e.ttl;
    if (k >= 1) { effects.splice(i, 1); continue; }
    ctx.save();
    ctx.globalAlpha = 1 - k;
    if (e.type === 'kill') {
      ctx.strokeStyle = '#ff3b47';
      ctx.lineWidth = 6 * (1 - k);
      ctx.beginPath(); ctx.arc(e.x, e.y, 20 + k * 90, 0, Math.PI * 2); ctx.stroke();
      ctx.fillStyle = 'rgba(190,20,30,0.7)';
      for (let n = 0; n < 8; n++) {
        const a = (n / 8) * Math.PI * 2 + e.life;
        ctx.beginPath();
        ctx.arc(e.x + Math.cos(a) * k * 70, e.y + Math.sin(a) * k * 70, 6 * (1 - k), 0, Math.PI * 2);
        ctx.fill();
      }
    } else if (e.type === 'vent') {
      ctx.strokeStyle = 'rgba(200,220,255,0.7)';
      ctx.lineWidth = 4;
      ctx.beginPath(); ctx.arc(e.x, e.y, 10 + k * 46, 0, Math.PI * 2); ctx.stroke();
    } else if (e.type === 'scan') {
      ctx.strokeStyle = `rgba(120,255,180,${0.8 - k})`;
      ctx.lineWidth = 5;
      const yy = e.y - 50 + ((e.life * 120) % 100);
      ctx.beginPath(); ctx.moveTo(e.x - 40, yy); ctx.lineTo(e.x + 40, yy); ctx.stroke();
      ctx.beginPath(); ctx.arc(e.x, e.y, 46, 0, Math.PI * 2); ctx.stroke();
    } else if (e.type === 'shields') {
      ctx.strokeStyle = `rgba(110,230,255,${0.9 - k})`;
      ctx.lineWidth = 6;
      ctx.beginPath(); ctx.arc(e.x, e.y, 30 + k * 120, 0, Math.PI * 2); ctx.stroke();
    } else if (e.type === 'asteroids') {
      ctx.strokeStyle = `rgba(255,120,80,${0.9 - k})`;
      ctx.lineWidth = 4;
      for (let n = 0; n < 3; n++) {
        const a = e.life * 6 + n * 2;
        ctx.beginPath();
        ctx.moveTo(e.x, e.y);
        ctx.lineTo(e.x + Math.cos(a) * 120, e.y + Math.sin(a) * 90);
        ctx.stroke();
      }
    } else if (e.type === 'doors') {
      ctx.strokeStyle = `rgba(255,80,80,${0.8 - k})`;
      ctx.lineWidth = 5;
      ctx.strokeRect(e.x - 30, e.y - 30, 60, 60);
    }
    ctx.restore();
  }
}

// ---------------------------------------------------------------------------
// Frame
// ---------------------------------------------------------------------------

export function render(canvas, ctx, dt, local) {
  time += dt;
  const { w, h, dpr } = resize(canvas);
  const me = state.players.get(state.you);

  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = '#04060c';
  ctx.fillRect(0, 0, w, h);

  // Frame roughly one vision circle: zoom by the *larger* ratio so portrait
  // phones do not end up staring at a postage stamp.
  camera.scale = clamp(Math.max(w / 1300, h / 900), 0.5, 1.15);
  const cx = local ? local.x : camera.x;
  const cy = local ? local.y : camera.y;
  camera.x += (cx - camera.x) * Math.min(1, dt * 14);
  camera.y += (cy - camera.y) * Math.min(1, dt * 14);

  ctx.save();
  ctx.translate(w / 2, h / 2);
  ctx.scale(camera.scale, camera.scale);
  ctx.translate(-camera.x, -camera.y);

  drawFloors(ctx);
  for (const r of ROOMS) drawRoomDecor(ctx, r);
  drawRoomLabels(ctx);
  drawConsoles(ctx);
  drawVents(ctx);
  drawWalls(ctx);
  drawDoors(ctx);

  // bodies
  for (const b of state.bodies) drawCorpse(ctx, { x: b.x, y: b.y, r: 20, color: b.color });

  // players, painters' order by y
  const drawable = [];
  for (const p of state.players.values()) {
    if (p.id === state.you) continue;
    if (!p.visible || p.vent) continue;
    const smooth = Math.min(1, dt * 16);
    p.rx += (p.x - p.rx) * smooth;
    p.ry += (p.y - p.ry) * smooth;
    p.walk = (p.walk || 0) + (p.moving ? dt * 2.4 : 0);
    drawable.push(p);
  }
  if (me && local) {
    me.rx = local.x; me.ry = local.y;
    me.walk = (me.walk || 0) + (local.moving ? dt * 2.4 : 0);
    if (!state.inVent) drawable.push(me);
  }
  drawable.sort((a, b) => a.ry - b.ry);
  for (const p of drawable) {
    drawCrewmate(ctx, {
      x: p.rx, y: p.ry, r: PLAYER_RADIUS, color: p.color,
      dir: p.dir, walk: p.walk, ghost: p.ghost,
      alpha: p.ghost ? (p.id === state.you ? 0.7 : 0.45) : 1,
    });
  }

  drawEffects(ctx, dt);
  ctx.restore();

  // ---- darkness -----------------------------------------------------------
  const radius = visionRadius();
  if (local && radius < 1e8) {
    const segs = blockers(local.x, local.y, radius + 40);
    const poly = visibilityPolygon(local.x, local.y, radius, segs, 64);
    const toScreen = (p) => ({
      x: w / 2 + (p.x - camera.x) * camera.scale,
      y: h / 2 + (p.y - camera.y) * camera.scale,
    });
    ctx.save();
    ctx.beginPath();
    ctx.rect(0, 0, w, h);
    if (poly.length) {
      const p0 = toScreen(poly[0]);
      ctx.moveTo(p0.x, p0.y);
      for (let i = 1; i < poly.length; i++) {
        const q = toScreen(poly[i]);
        ctx.lineTo(q.x, q.y);
      }
      ctx.closePath();
    }
    ctx.fillStyle = 'rgba(3,5,10,0.92)';
    ctx.fill('evenodd');

    // soft falloff inside the lit polygon
    if (poly.length) {
      ctx.beginPath();
      const p0 = toScreen(poly[0]);
      ctx.moveTo(p0.x, p0.y);
      for (let i = 1; i < poly.length; i++) {
        const q = toScreen(poly[i]);
        ctx.lineTo(q.x, q.y);
      }
      ctx.closePath();
      ctx.clip();
      const cs = toScreen({ x: local.x, y: local.y });
      const R = radius * camera.scale;
      const g = ctx.createRadialGradient(cs.x, cs.y, R * 0.35, cs.x, cs.y, R);
      g.addColorStop(0, 'rgba(3,5,10,0)');
      g.addColorStop(0.75, 'rgba(3,5,10,0.55)');
      g.addColorStop(1, 'rgba(3,5,10,0.95)');
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, w, h);
    }
    ctx.restore();
  }

  // ---- name tags (drawn above the dark so they stay readable) -------------
  for (const p of drawable) {
    const sx = w / 2 + (p.rx - camera.x) * camera.scale;
    const sy = h / 2 + (p.ry - camera.y - PLAYER_RADIUS * 2.1) * camera.scale;
    if (sx < -100 || sx > w + 100 || sy < -60 || sy > h + 60) continue;
    drawNameTag(ctx, p, { x: sx, y: sy });
  }

  // ---- sabotage arrow -----------------------------------------------------
  if (local && state.sabotage && (state.sabotage.kind === 'reactor' || state.sabotage.kind === 'o2')) {
    const pts = FIX_LIST.filter((f) => f.kind === state.sabotage.kind);
    let best = null, bestD = Infinity;
    for (const p of pts) {
      const d = Math.hypot(p.x - local.x, p.y - local.y);
      if (d < bestD) { bestD = d; best = p; }
    }
    if (best && bestD > 160) {
      const a = Math.atan2(best.y - local.y, best.x - local.x);
      const r = Math.min(w, h) * 0.32;
      const ax = w / 2 + Math.cos(a) * r, ay = h / 2 + Math.sin(a) * r;
      ctx.save();
      ctx.translate(ax, ay);
      ctx.rotate(a);
      ctx.fillStyle = `rgba(255,70,70,${0.6 + 0.35 * Math.sin(time * 8)})`;
      ctx.beginPath();
      ctx.moveTo(26, 0); ctx.lineTo(-16, 16); ctx.lineTo(-16, -16);
      ctx.closePath(); ctx.fill();
      ctx.restore();
    }
  }
}

// ---------------------------------------------------------------------------
// Mini-map (also used as the sabotage board)
// ---------------------------------------------------------------------------

/** Tight bounding box of the ship, so the mini-map is not mostly empty space. */
const SHIP_BOUNDS = (() => {
  const b = { x1: Infinity, y1: Infinity, x2: -Infinity, y2: -Infinity };
  for (const r of RECTS) {
    b.x1 = Math.min(b.x1, r.x1); b.y1 = Math.min(b.y1, r.y1);
    b.x2 = Math.max(b.x2, r.x2); b.y2 = Math.max(b.y2, r.y2);
  }
  return { ...b, w: b.x2 - b.x1, h: b.y2 - b.y1 };
})();

export function drawMiniMap(canvas, opts = {}) {
  const ctx = canvas.getContext('2d');
  const w = canvas.width, h = canvas.height;
  const pad = 18;
  const scale = Math.min((w - pad * 2) / SHIP_BOUNDS.w, (h - pad * 2) / SHIP_BOUNDS.h);
  const ox = (w - SHIP_BOUNDS.w * scale) / 2 - SHIP_BOUNDS.x1 * scale;
  const oy = (h - SHIP_BOUNDS.h * scale) / 2 - SHIP_BOUNDS.y1 * scale;
  const T = (x, y) => ({ x: ox + x * scale, y: oy + y * scale });

  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = '#060911';
  ctx.fillRect(0, 0, w, h);

  ctx.save();
  for (const r of HALLS) {
    const a = T(r.x1, r.y1);
    ctx.fillStyle = 'rgba(80,120,190,0.22)';
    ctx.fillRect(a.x, a.y, (r.x2 - r.x1) * scale, (r.y2 - r.y1) * scale);
  }
  for (const r of ROOMS) {
    const a = T(r.x1, r.y1);
    const rw = (r.x2 - r.x1) * scale, rh = (r.y2 - r.y1) * scale;
    const hot = opts.sabotageRooms && opts.sabotageRooms.includes(r.id);
    ctx.fillStyle = hot ? 'rgba(255,90,70,0.28)' : 'rgba(90,140,220,0.3)';
    roundRect(ctx, a.x, a.y, rw, rh, 6);
    ctx.fill();
    ctx.strokeStyle = hot ? 'rgba(255,140,110,0.9)' : 'rgba(150,190,255,0.55)';
    ctx.lineWidth = 2;
    ctx.stroke();
    ctx.fillStyle = 'rgba(220,235,255,0.85)';
    ctx.font = '600 12px Trebuchet MS, sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText(r.name, a.x + rw / 2, a.y + rh / 2 - 2);
    if (opts.counts && opts.counts[r.id]) {
      ctx.fillStyle = '#ffd65a';
      ctx.font = '800 15px Trebuchet MS, sans-serif';
      ctx.fillText('● '.repeat(Math.min(6, opts.counts[r.id])).trim(), a.x + rw / 2, a.y + rh / 2 + 16);
    }
  }
  ctx.restore();

  // task markers
  if (opts.tasks) {
    for (const t of opts.tasks) {
      const p = T(t.x, t.y);
      ctx.fillStyle = '#ffd65a';
      ctx.beginPath(); ctx.arc(p.x, p.y, 5, 0, Math.PI * 2); ctx.fill();
      ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.lineWidth = 1.5; ctx.stroke();
    }
  }
  // you
  if (opts.me) {
    const p = T(opts.me.x, opts.me.y);
    ctx.fillStyle = '#fff';
    ctx.beginPath(); ctx.arc(p.x, p.y, 7, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = opts.meColor || '#4ea8ff';
    ctx.beginPath(); ctx.arc(p.x, p.y, 5, 0, Math.PI * 2); ctx.fill();
  }
  return { T, scale, ox, oy };
}

/** Hit-test a click on the mini-map back to a room id. */
export function miniMapRoomAt(canvas, clientX, clientY) {
  const rect = canvas.getBoundingClientRect();
  const x = (clientX - rect.left) * (canvas.width / rect.width);
  const y = (clientY - rect.top) * (canvas.height / rect.height);
  const pad = 18;
  const scale = Math.min((canvas.width - pad * 2) / SHIP_BOUNDS.w, (canvas.height - pad * 2) / SHIP_BOUNDS.h);
  const ox = (canvas.width - SHIP_BOUNDS.w * scale) / 2 - SHIP_BOUNDS.x1 * scale;
  const oy = (canvas.height - SHIP_BOUNDS.h * scale) / 2 - SHIP_BOUNDS.y1 * scale;
  const wx = (x - ox) / scale, wy = (y - oy) / scale;
  for (const r of ROOMS) if (wx >= r.x1 && wx <= r.x2 && wy >= r.y1 && wy <= r.y2) return r;
  return null;
}
