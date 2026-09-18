// public/js/main.js - bootstrap, the client game loop and event wiring.

import { state, on, connect, actions } from './net.js';
import { initInput, input, readMovement, onAction } from './input.js';
import { render, addEffect, resize } from './render.js';
import {
  initHud, updateHud, renderTaskList, showNotice, toggleMap, closeMap, isMapOpen,
  closeCameras, isCamsOpen, doUse, doKill, doReport, doVent,
} from './hud.js';
import {
  showScreen, initMenu, initLobby, initMeeting, renderLobby, renderMeeting,
  tickMeeting, renderChat, menuError, playEjection, renderEnd, resetMeetingSelection,
} from './screens.js';
import { minigameOpen, closeMinigame } from './minigames/index.js';
import { stepMove, doorSegments, speedFor } from '../../shared/movement.js';
import { PLAYER_RADIUS } from '../../shared/constants.js';
import { sfx, unlockAudio } from './sound.js';

const canvas = document.getElementById('game');
const ctx = canvas.getContext('2d');

/** Locally predicted position of the player we control. */
const local = { x: 1580, y: 360, moving: false };
let inputSeq = 0;
let lastInputSent = 0;
let lastFrame = performance.now();

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------

initInput(canvas, document.getElementById('joystick'), document.getElementById('joystick-knob'));
initHud();
initLobby();
initMeeting();
initMenu({
  onCreate: async (name, color, hat) => {
    unlockAudio();
    await ensureConnection();
    actions.create(name, color, hat);
  },
  onJoin: async (code, name, color, hat) => {
    unlockAudio();
    await ensureConnection();
    actions.join(code, name, color, hat);
  },
});

let connecting = null;
async function ensureConnection() {
  if (state.connected) return;
  if (!connecting) connecting = connect().catch((e) => { menuError('Could not reach the server.'); throw e; });
  await connecting;
}

document.getElementById('btn-quit').addEventListener('click', () => location.reload());
document.getElementById('btn-reload').addEventListener('click', () => location.reload());
document.getElementById('btn-back-lobby').addEventListener('click', () => actions.backToLobby());

// ---------------------------------------------------------------------------
// Keyboard actions
// ---------------------------------------------------------------------------

onAction('use', () => { if (!blocked()) doUse(); });
onAction('kill', () => { if (!blocked()) doKill(); });
onAction('report', () => { if (!blocked()) doReport(); });
onAction('vent', () => { if (!blocked()) doVent(); });
onAction('map', () => { if (state.phase === 'playing' && !minigameOpen()) toggleMap('map'); });
onAction('chat', () => { if (state.phase === 'meeting') document.getElementById('chat-input').focus(); });
onAction('escape', () => {
  if (minigameOpen()) closeMinigame();
  else if (isCamsOpen()) closeCameras();
  else if (isMapOpen()) closeMap();
});

function blocked() {
  return state.phase !== 'playing' || minigameOpen() || isMapOpen() || isCamsOpen();
}

// ---------------------------------------------------------------------------
// Server events
// ---------------------------------------------------------------------------

on('joined', () => { showScreen('lobby'); renderLobby(); });
on('lobby', () => {
  if (state.phase === 'lobby') { showScreen('lobby'); renderLobby(); }
});
on('roster', () => { if (state.phase === 'meeting') renderMeeting(); });
on('serverError', (msg) => { menuError(msg.msg); showNotice(msg.msg); });
on('notice', (msg) => showNotice(msg.msg));

on('start', (msg) => {
  showScreen('game');
  resetMeetingSelection();
  local.x = msg.spawn.x;
  local.y = msg.spawn.y;
  renderTaskList();
  showRoleBanner(msg.role, msg.impostors);
  input.enabled = true;
  sfx.meeting();
});

on('tasks', () => renderTaskList());

on('died', () => {
  sfx.death();
  document.getElementById('ghost-banner').classList.remove('hidden');
  setTimeout(() => document.getElementById('ghost-banner').classList.add('hidden'), 5200);
  closeMinigame();
});

on('worldEvent', (ev) => {
  if (ev.t === 'killed') {
    addEffect('kill', ev.x, ev.y, { ttl: 0.9 });
    if (ev.victim !== state.you) sfx.kill();
  } else if (ev.t === 'ventfx') {
    addEffect('vent', ev.x, ev.y, { ttl: 0.6 });
    sfx.vent();
  } else if (ev.t === 'visual') {
    addEffect(ev.kind === 'scan' ? 'scan' : ev.kind, ev.x, ev.y, { ttl: 2.4 });
  } else if (ev.t === 'doors') {
    sfx.doors();
  }
});

on('sabotageStart', (msg) => {
  const kind = msg.kind || msg.k;
  renderTaskList();
  sfx.sabotage();
  const names = { reactor: 'Reactor meltdown!', o2: 'Oxygen depleting!', lights: 'Lights are out!', comms: 'Comms disabled!' };
  showNotice(names[kind] || 'Sabotage!', 3);
});
on('sabotageFixed', () => { renderTaskList(); sfx.confirm(); showNotice('Systems restored.', 2); });

on('meeting', () => {
  closeMinigame();
  closeMap();
  closeCameras();
  resetMeetingSelection();
  showScreen('meeting');
  renderMeeting();
  sfx.meeting();
});
on('meetingPhase', () => renderMeeting());
on('voted', () => renderMeeting());
on('chat', () => { if (state.phase === 'meeting') renderChat(); else showNotice('👻 ghost chat'); });

on('voteResult', async (msg) => {
  renderMeeting();
  await new Promise((r) => setTimeout(r, 1400));
  await playEjection(msg);
});

on('resume', () => {
  showScreen('game');
  resetMeetingSelection();
  const me = state.players.get(state.you);
  if (state.serverMe) { local.x = state.serverMe.x; local.y = state.serverMe.y; }
  input.enabled = true;
  renderTaskList();
});

on('end', (msg) => {
  closeMinigame();
  closeMap();
  showScreen('end');
  renderEnd(msg);
  const iWon = (msg.winner === 'impostor') === (state.role === 'impostor');
  iWon ? sfx.win() : sfx.lose();
});

on('close', () => {
  document.getElementById('connection-lost').classList.remove('hidden');
});

// ---------------------------------------------------------------------------
// Role reveal
// ---------------------------------------------------------------------------

function showRoleBanner(role, impostors) {
  const banner = document.getElementById('role-banner');
  const impostor = role === 'impostor';
  const mates = (impostors || []).filter((id) => id !== state.you)
    .map((id) => state.players.get(id)?.name)
    .filter(Boolean);
  banner.replaceChildren();
  const title = document.createElement('div');
  title.textContent = impostor ? 'IMPOSTOR' : 'CREWMATE';
  title.style.color = impostor ? '#ff2f3c' : '#7fe7ff';
  const sub = document.createElement('small');
  sub.textContent = impostor
    ? (mates.length ? `Your partner${mates.length > 1 ? 's' : ''}: ${mates.join(', ')} — sabotage and kill.` : 'There is 1 Impostor among us — sabotage and kill.')
    : 'Finish your tasks. Stay alive.';
  banner.append(title, sub);
  banner.classList.remove('show');
  void banner.offsetWidth;          // restart the animation
  banner.classList.add('show');
}

// ---------------------------------------------------------------------------
// Game loop
// ---------------------------------------------------------------------------

function frame(now) {
  const dt = Math.min(0.05, (now - lastFrame) / 1000);
  lastFrame = now;

  if (state.phase === 'playing') {
    stepLocal(dt);
    render(canvas, ctx, dt, local);
    updateHud(local, dt);
  } else if (state.phase === 'meeting') {
    tickMeeting(dt);
  }
  requestAnimationFrame(frame);
}

function stepLocal(dt) {
  const me = state.players.get(state.you);
  if (!me) return;

  // In a vent (or with an overlay open) the player is parked.
  const frozen = state.inVent || minigameOpen() || isMapOpen() || isCamsOpen();
  const move = frozen ? { dx: 0, dy: 0 } : readMovement();

  if (!frozen) {
    const moved = stepMove(local, move, dt, {
      speed: speedFor({ alive: me.alive }, state.settings),
      doorSegs: me.alive ? doorSegments([...state.closedDoors]) : [],
      noclip: !me.alive,
    });
    local.moving = moved && (Math.abs(move.dx) + Math.abs(move.dy)) > 0.01;
    if (Math.abs(move.dx) > 0.05) me.dir = move.dx > 0 ? 1 : -1;
  } else {
    local.moving = false;
  }

  // Reconcile with the server: snap on a big divergence, otherwise ease across.
  if (state.serverMe) {
    const dx = state.serverMe.x - local.x, dy = state.serverMe.y - local.y;
    const err = Math.hypot(dx, dy);
    if (err > 120 || state.inVent) { local.x = state.serverMe.x; local.y = state.serverMe.y; }
    else if (err > 2) { local.x += dx * Math.min(1, dt * 6); local.y += dy * Math.min(1, dt * 6); }
  }

  // Input goes up at a fixed rate, not per frame.
  const nowMs = performance.now();
  if (nowMs - lastInputSent > 50) {
    lastInputSent = nowMs;
    actions.input(move.dx, move.dy, ++inputSeq);
  }
}

// Exposed for the browser test-suite (and handy in the console).
window.__game = { state, local, actions, input };

window.addEventListener('resize', () => resize(canvas));
window.addEventListener('pointerdown', unlockAudio, { once: true });
window.addEventListener('keydown', unlockAudio, { once: true });

showScreen('menu');
requestAnimationFrame(frame);
