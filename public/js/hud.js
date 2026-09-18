// public/js/hud.js - heads-up display, contextual action buttons, map overlay.

import { state, actions } from './net.js';
import { currentStep, pendingConsoles } from '../../shared/tasks.js';
import { INTERACT_RANGE, BODY_REPORT_RANGE, VENT_RANGE, KILL_DISTANCES, PLAYER_RADIUS } from '../../shared/constants.js';
import { VENTS, VENT_BY_ID, EMERGENCY_BUTTON, FIX_POINTS, DOOR_ROOMS, ROOM_BY_ID, WALLS, DOOR_BY_ID } from '../../shared/map.js';
import { dist, lineOfSight, segmentsNear } from '../../shared/geom.js';
import { openMinigame, closeMinigame, minigameOpen } from './minigames/index.js';
import { drawMiniMap, miniMapRoomAt, addEffect } from './render.js';
import { sfx } from './sound.js';
import { input } from './input.js';

const $ = (id) => document.getElementById(id);

const ui = {};
let mapOpen = false;
let mapMode = 'map';        // map | sabotage
let context = { use: null, report: null, kill: null, vent: null };

export function initHud() {
  ui.taskList = $('task-list');
  ui.taskbarFill = $('taskbar-fill');
  ui.taskbarLabel = $('taskbar-label');
  ui.btnUse = $('btn-use');
  ui.btnKill = $('btn-kill');
  ui.btnReport = $('btn-report');
  ui.btnVent = $('btn-vent');
  ui.btnSabotage = $('btn-sabotage');
  ui.killCd = $('kill-cd');
  ui.notice = $('notice');
  ui.sabBanner = $('sab-banner');
  ui.mapOverlay = $('map-overlay');
  ui.mapCanvas = $('map-canvas');
  ui.mapTitle = $('map-title');
  ui.mapHint = $('map-hint');
  ui.ventUi = $('vent-ui');
  ui.ventLinks = $('vent-links');

  ui.btnUse.addEventListener('click', doUse);
  ui.btnKill.addEventListener('click', doKill);
  ui.btnReport.addEventListener('click', doReport);
  ui.btnVent.addEventListener('click', doVent);
  ui.btnSabotage.addEventListener('click', () => toggleMap('sabotage'));
  $('btn-map').addEventListener('click', () => toggleMap('map'));
  $('btn-vent-exit').addEventListener('click', () => actions.vent('exit'));
  for (const b of document.querySelectorAll('.close-map')) b.addEventListener('click', () => closeMap());
  ui.mapCanvas.addEventListener('click', onMapClick);
  $('minigame-close').addEventListener('click', () => closeMinigame());
}

// ---------------------------------------------------------------------------
// Contextual actions
// ---------------------------------------------------------------------------

function blockers(x, y, r) {
  const segs = segmentsNear(x, y, r, WALLS, []);
  for (const id of state.closedDoors) {
    const d = DOOR_BY_ID.get(id);
    if (d) segs.push(d);
  }
  return segs;
}

/** What the USE button would do right now. */
function findUseTarget(local) {
  const me = state.players.get(state.you);
  const alive = me ? me.alive : true;

  // 1. an active sabotage you are standing at
  if (state.sabotage && alive) {
    const pts = FIX_POINTS[state.sabotage.kind] || [];
    for (const p of pts) {
      if (dist(local.x, local.y, p.x, p.y) <= INTERACT_RANGE) {
        if (state.sabotage.kind === 'o2' && (state.sabotage.fixed || []).includes(p.id)) continue;
        return { kind: 'sabotage', system: state.sabotage.kind, pad: p.id, label: 'FIX' };
      }
    }
  }
  // 2. the emergency button
  if (alive && dist(local.x, local.y, EMERGENCY_BUTTON.x, EMERGENCY_BUTTON.y) <= EMERGENCY_BUTTON.r) {
    return { kind: 'emergency', label: 'MEET' };
  }
  // 3. one of your own task consoles
  for (const task of state.tasks || []) {
    const step = currentStep(task);
    if (!step) continue;
    if (dist(local.x, local.y, step.x, step.y) <= INTERACT_RANGE) {
      return { kind: 'task', task, step, label: 'USE' };
    }
  }
  return null;
}

function findKillTarget(local) {
  if (state.role !== 'impostor') return null;
  const me = state.players.get(state.you);
  if (!me || !me.alive || state.inVent) return null;
  const range = KILL_DISTANCES[state.settings?.killDistance || 'normal'];
  let best = null, bestD = Infinity;
  for (const p of state.players.values()) {
    if (p.id === state.you || !p.visible || p.ghost || p.vent) continue;
    const d = dist(local.x, local.y, p.x, p.y);
    if (d > range || d >= bestD) continue;
    if (!lineOfSight(local.x, local.y, p.x, p.y, blockers(local.x, local.y, d + 40))) continue;
    best = p; bestD = d;
  }
  return best;
}

function findBody(local) {
  const me = state.players.get(state.you);
  if (!me || !me.alive) return null;
  let best = null, bestD = BODY_REPORT_RANGE;
  for (const b of state.bodies) {
    const d = dist(local.x, local.y, b.x, b.y);
    if (d < bestD) { best = b; bestD = d; }
  }
  return best;
}

function findVent(local) {
  if (state.role !== 'impostor') return null;
  const me = state.players.get(state.you);
  if (!me || !me.alive) return null;
  if (state.inVent) return VENT_BY_ID.get(state.inVent);
  for (const v of VENTS) if (dist(local.x, local.y, v.x, v.y) <= VENT_RANGE) return v;
  return null;
}

// ---------------------------------------------------------------------------
// Actions
// ---------------------------------------------------------------------------

export function doUse() {
  const target = context.use;
  if (!target) return;
  if (target.kind === 'emergency') { actions.emergency(); sfx.report(); return; }

  if (target.kind === 'sabotage') {
    const system = target.system;
    if (system === 'lights') {
      openMinigame('lightsfix', { title: 'Restore Lights', onComplete: () => actions.fix('lights', {}) });
    } else if (system === 'comms') {
      openMinigame('commsfix', { title: 'Restore Comms', onComplete: () => actions.fix('comms', {}) });
    } else if (system === 'o2') {
      openMinigame('o2fix', { title: 'Restore Oxygen', onComplete: () => actions.fix('o2', { pad: target.pad }) });
    } else if (system === 'reactor') {
      openMinigame('reactorfix', {
        title: 'Reactor Meltdown',
        data: { beat: () => actions.fix('reactor', { pad: target.pad }) },
      });
    }
    return;
  }

  if (target.kind === 'task') {
    const { task, step } = target;
    openMinigame(step.minigame, {
      title: step.label || task.name,
      onComplete: () => {
        step.done = true;                      // optimistic; the server confirms
        actions.completeTask(task.uid);
        if (step.visual) addEffect(step.minigame === 'scan' ? 'scan' : step.minigame, step.x, step.y, { ttl: 2 });
        renderTaskList();
      },
    });
  }
}

export function doKill() {
  if (!context.kill) return;
  actions.kill(context.kill.id);
}

export function doReport() {
  if (!context.report) return;
  actions.report(context.report.id);
  sfx.report();
}

export function doVent() {
  const v = context.vent;
  if (!v) return;
  if (state.inVent) actions.vent('exit');
  else actions.vent('enter');
  sfx.vent();
}

// ---------------------------------------------------------------------------
// Per-frame update
// ---------------------------------------------------------------------------

export function updateHud(local, dt) {
  const me = state.players.get(state.you);
  const impostor = state.role === 'impostor';
  const alive = me ? me.alive : true;

  context.use = local ? findUseTarget(local) : null;
  context.kill = local ? findKillTarget(local) : null;
  context.report = local ? findBody(local) : null;
  context.vent = local ? findVent(local) : null;

  ui.btnUse.disabled = !context.use;
  ui.btnUse.textContent = context.use?.label || 'USE';
  ui.btnUse.classList.toggle('ready', !!context.use);

  ui.btnReport.disabled = !context.report;
  ui.btnReport.classList.toggle('ready', !!context.report);

  ui.btnKill.style.display = impostor ? '' : 'none';
  ui.btnVent.style.display = impostor ? '' : 'none';
  ui.btnSabotage.style.display = impostor && alive ? '' : 'none';
  if (impostor) {
    const cd = state.killCooldown;
    ui.killCd.textContent = cd > 0 ? String(Math.ceil(cd)) : '';
    ui.btnKill.disabled = !context.kill || cd > 0 || !alive;
    ui.btnKill.classList.toggle('ready', !!context.kill && cd <= 0);
    ui.btnVent.disabled = !context.vent || !alive;
    ui.btnSabotage.disabled = state.sabotageCooldown > 0;
  }

  // vent navigation panel
  if (state.inVent) {
    ui.ventUi.classList.remove('hidden');
    const links = state.ventLinks || [];
    if (ui.ventLinks.dataset.for !== state.inVent) {
      ui.ventLinks.dataset.for = state.inVent;
      ui.ventLinks.replaceChildren();
      for (const id of links) {
        const v = VENT_BY_ID.get(id);
        const btn = document.createElement('button');
        btn.className = 'btn btn-small';
        btn.textContent = ROOM_BY_ID.get(v.room)?.name || v.room;
        btn.addEventListener('click', () => { actions.vent('move', id); sfx.vent(); });
        ui.ventLinks.append(btn);
      }
    }
  } else {
    ui.ventUi.classList.add('hidden');
    ui.ventLinks.dataset.for = '';
  }

  // task bar
  if (state.taskbar === null || state.taskbar === undefined) {
    ui.taskbarFill.style.width = '0%';
    ui.taskbarLabel.textContent = state.sabotage?.kind === 'comms' ? 'Comms Disabled' : 'Total Tasks';
  } else {
    ui.taskbarFill.style.width = `${Math.round(state.taskbar * 100)}%`;
    ui.taskbarLabel.textContent = 'Total Tasks';
  }

  // sabotage banner
  const sab = state.sabotage;
  if (sab) {
    const names = { reactor: 'REACTOR MELTDOWN', o2: 'OXYGEN DEPLETED', lights: 'LIGHTS SABOTAGED', comms: 'COMMUNICATIONS DISABLED' };
    const secs = sab.time > 0 ? ` — ${sab.time.toFixed(1)}s` : '';
    ui.sabBanner.textContent = (names[sab.kind] || 'SABOTAGE') + secs;
    ui.sabBanner.classList.add('show');
  } else {
    ui.sabBanner.classList.remove('show');
  }

  if (mapOpen) drawMap(local);
}

let lastTaskSignature = '';
export function renderTaskList() {
  const impostor = state.role === 'impostor';
  const lines = [];
  for (const task of state.tasks || []) {
    const step = currentStep(task);
    const done = !step;
    const label = step ? (step.label || task.name) : task.name;
    lines.push({ label, done, total: task.steps.length, at: task.steps.filter((s) => s.done).length });
  }
  const signature = JSON.stringify(lines) + impostor;
  if (signature === lastTaskSignature) return;
  lastTaskSignature = signature;

  ui.taskList.replaceChildren();
  const head = document.createElement('div');
  head.className = 't-head';
  head.textContent = impostor ? 'Fake Tasks' : 'Tasks';
  ui.taskList.append(head);
  for (const l of lines) {
    const d = document.createElement('div');
    d.className = 't' + (l.done ? ' done' : '') + (impostor ? ' fake' : '');
    // Multi-step labels already read like "Fuel Engines (Upper)", so only add a
    // counter when the label does not carry one.
    const hasCounter = /\(\d+\/\d+\)/.test(l.label);
    d.textContent = !l.done && l.total > 1 && !hasCounter ? `${l.label}  (${l.at}/${l.total})` : l.label;
    ui.taskList.append(d);
  }
}

let noticeTimer = 0;
export function showNotice(text, seconds = 2.5) {
  ui.notice.textContent = text;
  ui.notice.classList.add('show');
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => ui.notice.classList.remove('show'), seconds * 1000);
}

// ---------------------------------------------------------------------------
// Map / sabotage board
// ---------------------------------------------------------------------------

export function toggleMap(mode = 'map') {
  if (mapOpen && mapMode === mode) { closeMap(); return; }
  if (mode === 'sabotage' && state.role !== 'impostor') return;
  mapOpen = true;
  mapMode = mode;
  ui.mapOverlay.classList.remove('hidden');
  ui.mapTitle.textContent = mode === 'sabotage' ? 'Sabotage' : 'Map';
  ui.mapHint.textContent = mode === 'sabotage'
    ? 'Pick a system below, or tap a room to slam its doors.'
    : 'Yellow dots are your remaining tasks.';
  updateSabotageButtons();
  input.enabled = false;
  sfx.click();
}

export function closeMap() {
  mapOpen = false;
  ui.mapOverlay.classList.add('hidden');
  input.enabled = true;
}

export function isMapOpen() { return mapOpen; }

const SABOTAGE_SYSTEMS = [
  { kind: 'reactor', label: 'Reactor Meltdown' },
  { kind: 'o2', label: 'Oxygen Depletion' },
  { kind: 'lights', label: 'Lights' },
  { kind: 'comms', label: 'Comms' },
];
let sabButtons = null;

/**
 * Build the sabotage board once. Rebuilding it every frame would detach the
 * buttons mid-click, so state changes are applied in place instead.
 */
function buildSabotageButtons() {
  let holder = document.getElementById('sab-buttons');
  if (!holder) {
    holder = document.createElement('div');
    holder.id = 'sab-buttons';
    holder.style.cssText = 'display:flex;gap:10px;flex-wrap:wrap;justify-content:center;align-items:center;margin-top:12px';
    ui.mapCanvas.parentElement.insertBefore(holder, ui.mapHint);
  }
  if (sabButtons) return holder;
  sabButtons = { holder, buttons: [], cooldown: null };
  for (const s of SABOTAGE_SYSTEMS) {
    const b = document.createElement('button');
    b.className = 'btn btn-small';
    b.textContent = s.label;
    b.addEventListener('click', () => {
      if (b.disabled) return;
      actions.sabotage(s.kind);
      sfx.sabotage();
      closeMap();
    });
    holder.append(b);
    sabButtons.buttons.push({ def: s, el: b });
  }
  const cd = document.createElement('span');
  cd.className = 'hint';
  holder.append(cd);
  sabButtons.cooldown = cd;
  return holder;
}

function updateSabotageButtons() {
  const holder = buildSabotageButtons();
  if (mapMode !== 'sabotage') { holder.style.display = 'none'; return; }
  holder.style.display = 'flex';
  const blocked = !!state.sabotage || state.sabotageCooldown > 0;
  for (const { el: b } of sabButtons.buttons) b.disabled = blocked;
  sabButtons.cooldown.textContent = state.sabotageCooldown > 0 ? `cooldown ${state.sabotageCooldown}s` : '';
}

function drawMap(local) {
  const me = state.players.get(state.you);
  drawMiniMap(ui.mapCanvas, {
    me: local,
    meColor: me?.color?.hex,
    tasks: state.sabotage?.kind === 'comms' ? [] : pendingConsoles(state.tasks || []),
    counts: state.adminCounts,
    sabotageRooms: mapMode === 'sabotage' ? DOOR_ROOMS : null,
  });
  if (mapMode === 'sabotage') updateSabotageButtons();
}

function onMapClick(ev) {
  if (mapMode !== 'sabotage') return;
  const room = miniMapRoomAt(ui.mapCanvas, ev.clientX, ev.clientY);
  if (!room || !DOOR_ROOMS.includes(room.id)) return;
  actions.sabotage('doors', room.id);
  sfx.doors();
  closeMap();
}

export function hudContext() { return context; }
