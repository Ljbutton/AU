// public/js/screens.js - menu, lobby, meeting and game-over screens.

import { state, actions, on } from './net.js';
import { COLORS, COLOR_BY_ID, HATS, MIN_PLAYERS, MAX_PLAYERS } from '../../shared/constants.js';
import { drawCrewmate, renderBeanTo } from './sprites.js';
import { sfx } from './sound.js';

const $ = (id) => document.getElementById(id);
const screens = ['menu', 'lobby', 'game', 'meeting', 'end'];

export function showScreen(name) {
  for (const s of screens) $(`screen-${s}`).classList.toggle('active', s === name);
}

// ---------------------------------------------------------------------------
// Menu
// ---------------------------------------------------------------------------

export const prefs = {
  name: localStorage.getItem('au.name') || '',
  color: localStorage.getItem('au.color') || COLORS[Math.floor(Math.random() * COLORS.length)].id,
  hat: localStorage.getItem('au.hat') || 'none',
};

export function initMenu({ onCreate, onJoin }) {
  const nameInput = $('input-name');
  const codeInput = $('input-code');
  const grid = $('color-grid');
  nameInput.value = prefs.name;

  const paintPreview = () => {
    const c = $('preview');
    const ctx = c.getContext('2d');
    ctx.clearRect(0, 0, c.width, c.height);
    drawCrewmate(ctx, {
      x: c.width / 2, y: c.height / 2 + 22, r: 40,
      color: COLOR_BY_ID.get(prefs.color), hat: prefs.hat,
      dir: 1, walk: performance.now() / 600,
    });
  };

  const paintSwatches = () => {
    grid.replaceChildren();
    for (const c of COLORS) {
      const b = document.createElement('div');
      b.className = 'swatch' + (c.id === prefs.color ? ' selected' : '');
      b.style.background = `linear-gradient(160deg, ${c.hex}, ${c.shadow})`;
      b.title = c.name;
      b.addEventListener('click', () => {
        prefs.color = c.id;
        localStorage.setItem('au.color', c.id);
        sfx.click();
        paintSwatches();
      });
      grid.append(b);
    }
  };

  // hat picker
  const hatRow = document.getElementById('hat-row');
  const hatLabel = document.getElementById('hat-name');
  const cycleHat = (dir) => {
    const i = HATS.findIndex((h) => h.id === prefs.hat);
    const next = HATS[(i + dir + HATS.length) % HATS.length];
    prefs.hat = next.id;
    localStorage.setItem('au.hat', next.id);
    hatLabel.textContent = next.name;
    sfx.click();
    actions.setHat(next.id);
  };
  hatLabel.textContent = (HATS.find((h) => h.id === prefs.hat) || HATS[0]).name;
  hatRow.querySelector('[data-dir="-1"]').addEventListener('click', () => cycleHat(-1));
  hatRow.querySelector('[data-dir="1"]').addEventListener('click', () => cycleHat(1));

  paintSwatches();
  const spin = () => { paintPreview(); requestAnimationFrame(spin); };
  requestAnimationFrame(spin);

  const readName = () => {
    const n = (nameInput.value || '').trim().slice(0, 12) || 'Crewmate';
    prefs.name = n;
    localStorage.setItem('au.name', n);
    return n;
  };

  $('btn-create').addEventListener('click', () => { sfx.confirm(); onCreate(readName(), prefs.color, prefs.hat); });
  $('btn-join').addEventListener('click', () => {
    const code = (codeInput.value || '').trim().toUpperCase();
    if (code.length < 4) { menuError('Enter the 6 letter lobby code.'); return; }
    sfx.confirm();
    onJoin(code, readName(), prefs.color, prefs.hat);
  });
  codeInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') $('btn-join').click(); });
  nameInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') $('btn-create').click(); });
}

export function menuError(msg) { $('menu-error').textContent = msg || ''; }

// ---------------------------------------------------------------------------
// Lobby
// ---------------------------------------------------------------------------

const SETTING_DEFS = [
  { key: 'impostors', label: 'Impostors', type: 'int', min: 1, max: 3, step: 1 },
  { key: 'playerSpeed', label: 'Player Speed', type: 'float', min: 0.5, max: 3, step: 0.25, suffix: 'x' },
  { key: 'crewVision', label: 'Crewmate Vision', type: 'float', min: 0.25, max: 5, step: 0.25, suffix: 'x' },
  { key: 'impostorVision', label: 'Impostor Vision', type: 'float', min: 0.25, max: 5, step: 0.25, suffix: 'x' },
  { key: 'killCooldown', label: 'Kill Cooldown', type: 'int', min: 5, max: 60, step: 5, suffix: 's' },
  { key: 'killDistance', label: 'Kill Distance', type: 'enum', values: ['short', 'normal', 'long'] },
  { key: 'emergencyMeetings', label: 'Emergency Meetings', type: 'int', min: 0, max: 9, step: 1 },
  { key: 'emergencyCooldown', label: 'Emergency Cooldown', type: 'int', min: 0, max: 60, step: 5, suffix: 's' },
  { key: 'discussionTime', label: 'Discussion Time', type: 'int', min: 0, max: 120, step: 5, suffix: 's' },
  { key: 'votingTime', label: 'Voting Time', type: 'int', min: 15, max: 300, step: 15, suffix: 's' },
  { key: 'confirmEjects', label: 'Confirm Ejects', type: 'bool' },
  { key: 'visualTasks', label: 'Visual Tasks', type: 'bool' },
  { key: 'anonymousVotes', label: 'Anonymous Votes', type: 'bool' },
  { key: 'taskBarUpdates', label: 'Task Bar Updates', type: 'enum', values: ['always', 'meetings', 'never'] },
  { key: 'commonTasks', label: 'Common Tasks', type: 'int', min: 0, max: 2, step: 1 },
  { key: 'longTasks', label: 'Long Tasks', type: 'int', min: 0, max: 3, step: 1 },
  { key: 'shortTasks', label: 'Short Tasks', type: 'int', min: 0, max: 5, step: 1 },
];

export function initLobby() {
  $('btn-start').addEventListener('click', () => { sfx.confirm(); actions.startGame(); });
  $('btn-add-bot').addEventListener('click', () => { sfx.click(); actions.addBot(); });
  $('btn-remove-bot').addEventListener('click', () => { sfx.click(); actions.removeBot(); });
  $('btn-leave').addEventListener('click', () => location.reload());
  $('btn-copy').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(state.code);
      $('lobby-hint').textContent = 'Code copied.';
    } catch {
      $('lobby-hint').textContent = `Lobby code: ${state.code}`;
    }
  });
}

export function renderLobby() {
  const isHost = state.you === state.hostId;
  $('lobby-code').textContent = state.code || '------';

  const wrap = $('lobby-players');
  wrap.replaceChildren();
  for (const p of state.players.values()) {
    const card = document.createElement('div');
    card.className = 'lobby-player' + (p.id === state.hostId ? ' host' : '');
    const canvas = document.createElement('canvas');
    canvas.width = 90; canvas.height = 96;
    renderBeanTo(canvas, p.color, { hat: p.hat });
    const name = document.createElement('div');
    name.className = 'pname';
    name.textContent = p.name + (p.id === state.you ? ' (you)' : '');
    const tag = document.createElement('div');
    tag.className = 'ptag';
    tag.textContent = p.id === state.hostId ? 'host' : (p.bot ? 'bot' : '');
    card.append(canvas, name, tag);
    wrap.append(card);
  }

  const count = state.players.size;
  $('btn-start').disabled = !isHost || count < MIN_PLAYERS;
  $('btn-add-bot').disabled = !isHost || count >= MAX_PLAYERS;
  $('btn-remove-bot').disabled = !isHost || ![...state.players.values()].some((p) => p.bot);
  $('lobby-hint').textContent = isHost
    ? (count < MIN_PLAYERS ? `Need ${MIN_PLAYERS - count} more player(s) — add bots to fill out the crew.` : 'Ready when you are.')
    : 'Waiting for the host to start…';

  renderSettings(isHost);
}

function renderSettings(isHost) {
  const list = $('settings-list');
  list.replaceChildren();
  for (const def of SETTING_DEFS) {
    const row = document.createElement('div');
    row.className = 'setting';
    const label = document.createElement('label');
    label.textContent = def.label;
    const ctl = document.createElement('div');
    ctl.className = 'ctl';
    const value = state.settings[def.key];

    const apply = (v) => {
      const next = { ...state.settings, [def.key]: v };
      state.settings = next;
      actions.setSettings(next);
      sfx.click();
      renderSettings(isHost);
    };

    if (def.type === 'bool') {
      const btn = document.createElement('button');
      btn.textContent = value ? '✓' : '✕';
      btn.style.width = '52px';
      btn.disabled = !isHost;
      btn.addEventListener('click', () => apply(!value));
      ctl.append(btn);
    } else if (def.type === 'enum') {
      const btn = document.createElement('button');
      btn.textContent = '›';
      btn.disabled = !isHost;
      const span = document.createElement('span');
      span.className = 'val';
      span.textContent = String(value);
      btn.addEventListener('click', () => {
        const i = def.values.indexOf(value);
        apply(def.values[(i + 1) % def.values.length]);
      });
      ctl.append(span, btn);
    } else {
      const dec = document.createElement('button');
      dec.textContent = '−';
      const inc = document.createElement('button');
      inc.textContent = '+';
      const span = document.createElement('span');
      span.className = 'val';
      span.textContent = def.type === 'float' ? `${Number(value).toFixed(2)}${def.suffix || ''}` : `${value}${def.suffix || ''}`;
      dec.disabled = !isHost || value <= def.min;
      inc.disabled = !isHost || value >= def.max;
      dec.addEventListener('click', () => apply(Math.max(def.min, +(value - def.step).toFixed(2))));
      inc.addEventListener('click', () => apply(Math.min(def.max, +(value + def.step).toFixed(2))));
      ctl.append(dec, span, inc);
    }
    row.append(label, ctl);
    list.append(row);
  }
}

// ---------------------------------------------------------------------------
// Meeting
// ---------------------------------------------------------------------------

let selectedVote = null;
let meetingClock = 0;

export function initMeeting() {
  $('btn-skip').addEventListener('click', () => castVote('skip'));
  $('chat-form').addEventListener('submit', (e) => {
    e.preventDefault();
    const input = $('chat-input');
    const text = input.value.trim();
    if (!text) return;
    actions.chat(text);
    input.value = '';
  });
}

function castVote(target) {
  const meeting = state.meeting;
  if (!meeting || meeting.phase !== 'vote') return;
  const me = state.players.get(state.you);
  if (!me || !me.alive || meeting.voted.has(state.you)) return;
  selectedVote = target;
  actions.vote(target);
  sfx.vote();
  renderMeeting();
}

export function renderMeeting() {
  const meeting = state.meeting;
  if (!meeting) return;
  const me = state.players.get(state.you);
  const canVote = me && me.alive && meeting.phase === 'vote' && !meeting.voted.has(state.you);

  $('meeting-title').textContent = meeting.reason === 'body'
    ? `Dead Body Reported${meeting.bodyName ? `: ${meeting.bodyName}` : ''}`
    : 'Emergency Meeting';
  const caller = meeting.by ? state.players.get(meeting.by) : null;
  $('meeting-sub').textContent = caller
    ? `${meeting.reason === 'body' ? 'Reported' : 'Called'} by ${caller.name}${caller.id === state.you ? ' (you)' : ''}`
    : '';
  $('meeting-phase').textContent = meeting.phase === 'discuss' ? 'Discussion'
    : meeting.phase === 'vote' ? 'Voting' : 'Results';

  const wrap = $('meeting-players');
  wrap.replaceChildren();
  for (const p of state.players.values()) {
    const row = document.createElement('div');
    row.className = 'mp' + (p.alive === false ? ' dead' : '') + (selectedVote === p.id ? ' selected' : '');
    const canvas = document.createElement('canvas');
    canvas.width = 54; canvas.height = 56;
    renderBeanTo(canvas, p.color, { ghost: p.alive === false, hat: p.hat });
    const name = document.createElement('div');
    name.className = 'mp-name';
    name.textContent = p.name + (p.id === state.you ? ' (you)' : '');
    const votes = document.createElement('div');
    votes.className = 'mp-votes';
    if (meeting.result) {
      for (const [voter, target] of meeting.votes) {
        if (target !== p.id) continue;
        const dot = document.createElement('span');
        dot.className = 'vdot';
        dot.style.background = state.players.get(voter)?.color?.hex || '#888';
        votes.append(dot);
      }
    } else if (meeting.voted.has(p.id)) {
      const tick = document.createElement('span');
      tick.className = 'mp-tick';
      tick.textContent = 'voted';
      votes.append(tick);
    }
    row.append(canvas, name, votes);
    if (canVote && p.alive !== false) {
      row.classList.add('votable');
      row.title = `Vote for ${p.name}`;
      row.addEventListener('click', () => castVote(p.id));
    }
    wrap.append(row);
  }

  $('btn-skip').disabled = !canVote;
  const voted = meeting.voted.size;
  const alive = [...state.players.values()].filter((p) => p.alive !== false).length;
  const spectating = me && me.alive === false;
  $('vote-status').textContent = meeting.phase === 'discuss'
    ? 'Discussion — voting opens shortly.'
    : meeting.phase === 'results' ? 'Votes are in.'
      : spectating ? `Ghosts cannot vote — ${voted} / ${alive} votes cast`
        : `${voted} / ${alive} votes cast`;

  renderChat();
}

export function tickMeeting(dt) {
  if (!state.meeting) return;
  meetingClock += dt;
  if (meetingClock > 0.25) {
    meetingClock = 0;
    state.meeting.secs = Math.max(0, (state.meeting.secs || 0) - 0.25);
  }
  $('meeting-timer').textContent = state.meeting.phase === 'results'
    ? '—'
    : `${Math.ceil(state.meeting.secs || 0)}s`;
  $('meeting-phase').textContent = state.meeting.phase === 'discuss' ? 'Discussion'
    : state.meeting.phase === 'vote' ? 'Voting' : 'Results';
}

export function renderChat() {
  const log = $('chat-log');
  log.replaceChildren();
  if (!state.chat.length) {
    const hint = document.createElement('div');
    hint.className = 'chat-empty';
    const me = state.players.get(state.you);
    hint.textContent = me && me.alive === false
      ? 'Ghost chat — only other ghosts can read this.'
      : 'Nobody has said anything yet. Where was everyone?';
    log.append(hint);
  }
  for (const m of state.chat) {
    const line = document.createElement('div');
    line.className = 'cl' + (m.dead ? ' dead' : '');
    const who = document.createElement('span');
    who.className = 'cl-name';
    who.style.color = COLOR_BY_ID.get(m.color)?.hex || '#fff';
    who.textContent = `${m.name}${m.dead ? ' (ghost)' : ''}: `;
    line.append(who, document.createTextNode(m.text));
    log.append(line);
  }
  log.scrollTop = log.scrollHeight;
}

export function resetMeetingSelection() { selectedVote = null; }

// ---------------------------------------------------------------------------
// Ejection cutscene
// ---------------------------------------------------------------------------

export function playEjection(result) {
  return new Promise((resolve) => {
    const scene = $('eject-scene');
    const canvas = $('eject-canvas');
    const ctx = canvas.getContext('2d');
    const text = $('eject-text');
    scene.classList.remove('hidden');

    const ejected = result.ejected ? state.players.get(result.ejected) : null;
    const color = ejected ? ejected.color : COLOR_BY_ID.get('grey') || COLORS[0];
    const name = result.ejectedName || (ejected ? ejected.name : null);

    if (!name) {
      text.textContent = result.tie ? 'No one was ejected. (Tie)' : 'No one was ejected. (Skipped)';
    } else if (result.confirmEjects) {
      text.textContent = `${name} was ${result.wasImpostor ? '' : 'not '}The Impostor.`;
    } else {
      text.textContent = `${name} was ejected.`;
    }

    const started = performance.now();
    const DURATION = 5200;
    const stars = Array.from({ length: 90 }, () => ({
      x: Math.random() * canvas.width, y: Math.random() * canvas.height, r: Math.random() * 1.8 + 0.4,
    }));

    const frame = (now) => {
      const k = Math.min(1, (now - started) / DURATION);
      ctx.fillStyle = '#02030a';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      for (const s of stars) {
        ctx.fillStyle = 'rgba(255,255,255,0.75)';
        ctx.beginPath(); ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2); ctx.fill();
        s.x -= 0.4 + s.r * 0.5;
        if (s.x < 0) { s.x = canvas.width; s.y = Math.random() * canvas.height; }
      }
      // planet
      ctx.fillStyle = '#1d2a4a';
      ctx.beginPath(); ctx.arc(canvas.width * 0.14, canvas.height * 1.1, 200, 0, Math.PI * 2); ctx.fill();

      if (name) {
        const x = canvas.width * (0.18 + k * 0.72);
        const y = canvas.height * (0.55 - Math.sin(k * Math.PI) * 0.12);
        ctx.save();
        ctx.translate(x, y);
        ctx.rotate(k * Math.PI * 3);
        drawCrewmate(ctx, { x: 0, y: 0, r: 34 - k * 16, color, hat: ejected?.hat, dir: 1, walk: 0 });
        ctx.restore();
      }
      if (k < 1) requestAnimationFrame(frame);
      else {
        setTimeout(() => {
          scene.classList.add('hidden');
          resolve();
        }, 500);
      }
    };
    requestAnimationFrame(frame);
    sfx.eject();
  });
}

// ---------------------------------------------------------------------------
// Game over
// ---------------------------------------------------------------------------

const WIN_REASONS = {
  crew_tasks: 'The crew completed every task.',
  crew_vote: 'The crew ejected the last Impostor.',
  impostor_kill: 'The Impostors outnumber the crew.',
  impostor_vote: 'The crew ejected one of their own.',
  impostor_sabotage: 'A critical sabotage went unfixed.',
};

export function renderEnd(msg) {
  const crewWin = msg.winner === 'crew';
  $('end-title').textContent = crewWin ? 'Crewmates Win' : 'Impostors Win';
  $('end-title').style.color = crewWin ? '#6ee7ff' : '#ff4d5a';
  $('end-reason').textContent = WIN_REASONS[msg.reason] || '';
  const wrap = $('end-players');
  wrap.replaceChildren();
  for (const p of msg.players) {
    const card = document.createElement('div');
    card.className = 'end-player' + (p.role === 'impostor' ? ' impostor' : '');
    const canvas = document.createElement('canvas');
    canvas.width = 76; canvas.height = 80;
    renderBeanTo(canvas, COLOR_BY_ID.get(p.color), { ghost: !p.alive, hat: p.hat });
    const name = document.createElement('div');
    name.className = 'ep-name';
    name.textContent = p.name;
    const role = document.createElement('div');
    role.textContent = p.role === 'impostor' ? 'Impostor' : 'Crewmate';
    role.style.color = p.role === 'impostor' ? '#ff6b6b' : '#8b97ad';
    card.append(canvas, name, role);
    wrap.append(card);
  }
  $('btn-back-lobby').style.display = state.you === state.hostId ? '' : 'none';
}
