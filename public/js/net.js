// public/js/net.js - websocket transport plus the client-side mirror of the
// game state. UI modules subscribe with on(...) and never touch the socket.

import { COLOR_BY_ID } from '../../shared/constants.js';

export const state = {
  connected: false,
  you: null,
  code: null,
  hostId: null,
  settings: {},
  phase: 'menu',              // menu | lobby | playing | meeting | ended
  players: new Map(),
  role: null,
  impostors: [],
  tasks: [],
  bodies: [],
  closedDoors: new Set(),
  sabotage: null,
  sabotageCooldown: 0,
  taskbar: null,
  killCooldown: 0,
  adminCounts: null,
  inVent: null,
  ventLinks: [],
  meeting: null,
  chat: [],
  cameraFeed: [],
  camerasWatched: false,
  ping: 0,
  serverMe: null,
  lastSnapshot: 0,
};

const handlers = new Map();
export function on(type, fn) {
  if (!handlers.has(type)) handlers.set(type, new Set());
  handlers.get(type).add(fn);
  return () => handlers.get(type).delete(fn);
}
function emit(type, payload) {
  const set = handlers.get(type);
  if (set) for (const fn of set) { try { fn(payload); } catch (e) { console.error(e); } }
}

let socket = null;
let sendQueue = [];
let pingTimer = null;

export function connect() {
  return new Promise((resolve, reject) => {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    socket = new WebSocket(`${proto}://${location.host}/ws`);
    socket.onopen = () => {
      state.connected = true;
      for (const m of sendQueue) socket.send(JSON.stringify(m));
      sendQueue = [];
      pingTimer = setInterval(() => send({ t: 'ping', ts: Date.now() }), 4000);
      emit('open');
      resolve();
    };
    socket.onmessage = (ev) => {
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      handle(msg);
    };
    socket.onclose = () => {
      state.connected = false;
      clearInterval(pingTimer);
      emit('close');
    };
    socket.onerror = (e) => { reject(e); emit('error', e); };
  });
}

export function send(msg) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(msg));
  else sendQueue.push(msg);
}

function colorOf(id) { return COLOR_BY_ID.get(id) || COLOR_BY_ID.get('red'); }

function upsertPlayers(list) {
  const seen = new Set();
  for (const info of list) {
    seen.add(info.id);
    let p = state.players.get(info.id);
    if (!p) {
      p = {
        id: info.id, x: 1580, y: 360, rx: 1580, ry: 360,
        dir: 1, moving: false, ghost: false, vent: false, walk: 0, visible: false, lastSeen: 0,
      };
      state.players.set(info.id, p);
    }
    p.name = info.name ?? p.name;
    p.hat = info.hat ?? p.hat ?? 'none';
    p.bot = info.bot ?? p.bot;
    p.colorId = info.color ?? p.colorId;
    p.color = colorOf(p.colorId);
    if (info.alive !== undefined) p.alive = info.alive;
    if (info.connected !== undefined) p.connected = info.connected;
  }
  for (const id of [...state.players.keys()]) if (!seen.has(id)) state.players.delete(id);
}

function handle(msg) {
  switch (msg.t) {
    case 'joined':
      state.you = msg.you;
      state.code = msg.code;
      state.hostId = msg.hostId;
      state.settings = msg.settings;
      state.phase = 'lobby';
      upsertPlayers(msg.players);
      emit('joined', msg);
      break;

    case 'lobby':
      state.hostId = msg.hostId;
      state.settings = msg.settings;
      state.code = msg.code ?? state.code;
      if (msg.phase === 'lobby' && state.phase !== 'lobby') state.phase = 'lobby';
      upsertPlayers(msg.players);
      emit('lobby', msg);
      break;

    case 'roster':
      upsertPlayers(msg.players);
      emit('roster', msg);
      break;

    case 'start': {
      state.phase = 'playing';
      state.role = msg.role;
      state.impostors = msg.impostors || [];
      state.tasks = msg.tasks || [];
      state.settings = msg.settings;
      state.bodies = [];
      state.sabotage = null;
      state.meeting = null;
      state.chat = [];
      state.inVent = null;
      upsertPlayers(msg.players);
      for (const p of state.players.values()) { p.alive = true; p.ghost = false; }
      const me = state.players.get(state.you);
      if (me && msg.spawn) { me.x = me.rx = msg.spawn.x; me.y = me.ry = msg.spawn.y; }
      state.serverMe = msg.spawn ? { ...msg.spawn } : null;
      emit('start', msg);
      break;
    }

    case 's': {           // world snapshot
      state.lastSnapshot = performance.now();
      state.serverMe = msg.me;
      state.killCooldown = msg.me.cd ?? 0;
      state.sabotageCooldown = msg.sc ?? 0;
      state.taskbar = msg.bar;
      state.adminCounts = msg.adm || null;
      state.camerasWatched = !!msg.camOn;
      state.cameraFeed = (msg.cam || []).map((p) => ({ ...p, color: colorOf(p.c) }));
      state.closedDoors = new Set(msg.dr || []);
      state.bodies = (msg.b || []).map((b) => ({ id: b.i, color: colorOf(b.c), name: b.n, x: b.x, y: b.y }));
      if (msg.sab) {
        if (!state.sabotage || state.sabotage.kind !== msg.sab.k) emit('sabotageStart', msg.sab);
        state.sabotage = { kind: msg.sab.k, time: msg.sab.t, fixed: msg.sab.f || [] };
      } else if (state.sabotage) {
        state.sabotage = null;
      }
      if (msg.mt !== undefined && state.meeting) state.meeting.secs = msg.mt;

      const seen = new Set();
      for (const p of msg.p || []) {
        seen.add(p.i);
        let pl = state.players.get(p.i);
        if (!pl) continue;
        if (!pl.visible) { pl.rx = p.x; pl.ry = p.y; }      // pop in without sliding
        pl.x = p.x; pl.y = p.y; pl.dir = p.d; pl.moving = !!p.m;
        pl.ghost = !!p.g; pl.alive = !p.g; pl.vent = !!p.v;
        pl.visible = true;
        pl.lastSeen = state.lastSnapshot;
      }
      for (const pl of state.players.values()) if (!seen.has(pl.id)) pl.visible = false;

      for (const ev of msg.ev || []) emit('worldEvent', ev);
      emit('snapshot', msg);
      break;
    }

    case 'tasks':
      state.tasks = msg.tasks;
      emit('tasks', msg);
      break;

    case 'died': {
      const me = state.players.get(state.you);
      if (me) { me.alive = false; me.ghost = true; }
      emit('died', msg);
      break;
    }

    case 'vent':
      state.inVent = msg.state === 'in' ? msg.ventId : null;
      state.ventLinks = msg.links || [];
      emit('vent', msg);
      break;

    case 'sabotage':
      state.sabotage = { kind: msg.kind, time: msg.time, fixed: [] };
      emit('sabotageStart', msg);
      break;

    case 'sabotageFixed':
      state.sabotage = null;
      emit('sabotageFixed', msg);
      break;

    case 'sabfix':
      if (state.sabotage) state.sabotage.fixed = msg.fixed || msg.pads || [];
      emit('sabfix', msg);
      break;

    case 'meeting':
      state.phase = 'meeting';
      // The server yanks everyone out of vents for a meeting; mirror that here
      // or the vent panel would linger and freeze movement afterwards.
      state.inVent = null;
      state.ventLinks = [];
      upsertPlayers(msg.players);
      state.meeting = {
        reason: msg.reason, by: msg.by, bodyOf: msg.bodyOf, bodyName: msg.bodyName,
        phase: msg.phase, secs: msg.secs, votes: new Map(), voted: new Set(), result: null,
      };
      state.chat = [];
      if (msg.taskbar !== null && msg.taskbar !== undefined) state.taskbar = msg.taskbar;
      emit('meeting', msg);
      break;

    case 'meetingPhase':
      if (state.meeting) { state.meeting.phase = msg.phase; state.meeting.secs = msg.secs; }
      emit('meetingPhase', msg);
      break;

    case 'voted':
      if (state.meeting) state.meeting.voted.add(msg.id);
      emit('voted', msg);
      break;

    case 'voteResult':
      if (state.meeting) {
        state.meeting.phase = 'results';
        state.meeting.result = msg;
        for (const v of msg.votes || []) if (v.voter) state.meeting.votes.set(v.voter, v.target);
      }
      emit('voteResult', msg);
      break;

    case 'resume':
      state.phase = 'playing';
      state.meeting = null;
      state.inVent = null;
      state.ventLinks = [];
      for (const info of msg.players || []) {
        const p = state.players.get(info.id);
        if (p) { p.alive = info.alive; p.ghost = !info.alive; }
      }
      emit('resume', msg);
      break;

    case 'chat':
      state.chat.push(msg);
      if (state.chat.length > 120) state.chat.shift();
      emit('chat', msg);
      break;

    case 'end':
      state.phase = 'ended';
      emit('end', msg);
      break;

    case 'left':
      emit('left', msg);
      break;

    case 'doors':
      emit('doors', msg);
      break;

    case 'notice':
      emit('notice', msg);
      break;

    case 'error':
      emit('serverError', msg);
      break;

    case 'pong':
      state.ping = Date.now() - msg.ts;
      break;

    default:
      break;
  }
}

export const actions = {
  create: (name, color, hat) => send({ t: 'create', name, color, hat }),
  join: (code, name, color, hat) => send({ t: 'join', code, name, color, hat }),
  setColor: (color) => send({ t: 'color', color }),
  setHat: (hat) => send({ t: 'hat', hat }),
  setSettings: (settings) => send({ t: 'settings', settings }),
  startGame: () => send({ t: 'start' }),
  addBot: () => send({ t: 'addBot' }),
  removeBot: () => send({ t: 'removeBot' }),
  input: (dx, dy, seq) => send({ t: 'input', dx, dy, seq }),
  completeTask: (uid) => send({ t: 'taskStep', uid }),
  kill: (targetId) => send({ t: 'kill', targetId }),
  report: (bodyId) => send({ t: 'report', bodyId }),
  emergency: () => send({ t: 'emergency' }),
  vent: (action, ventId) => send({ t: 'vent', action, ventId }),
  sabotage: (kind, room) => send({ t: 'sabotage', kind, room }),
  fix: (kind, data) => send({ t: 'fix', kind, data }),
  cameras: (on) => send({ t: 'cams', on }),
  vote: (targetId) => send({ t: 'vote', targetId }),
  chat: (text) => send({ t: 'chat', text }),
  backToLobby: () => send({ t: 'lobby' }),
};
