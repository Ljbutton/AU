// server/game.js
// The authoritative game room: lobby, round state machine, movement, tasks,
// kills, sabotage, meetings and win conditions.

import {
  TICK_RATE, PLAYER_RADIUS, INTERACT_RANGE, BODY_REPORT_RANGE, VENT_RANGE,
  COLORS, MAX_PLAYERS, MIN_PLAYERS, DEFAULT_SETTINGS, sanitizeSettings,
  KILL_DISTANCES, VISION, SABOTAGE, MEETING, ROLE, PHASE, MEETING_PHASE, WIN,
} from '../shared/constants.js';
import {
  RECTS, WALLS, ROOMS, VENTS, VENT_BY_ID, DOORS, DOOR_ROOMS, FIX_POINTS,
  EMERGENCY_BUTTON, ADMIN_TABLE, SPAWN, roomAt, WORLD,
} from '../shared/map.js';
import { assignTasks, taskProgress, currentStep } from '../shared/tasks.js';
import { stepMove, doorSegments, speedFor, settle } from '../shared/movement.js';
import { lineOfSight, dist, segmentsNear, clamp } from '../shared/geom.js';
import { BotBrain } from './bots.js';

const BOT_NAMES = [
  'Ash', 'Bolt', 'Cosmo', 'Dizzy', 'Echo', 'Flint', 'Gizmo', 'Hatch', 'Iris',
  'Jinx', 'Kilo', 'Lumen', 'Moxie', 'Nova', 'Orbit', 'Pixel', 'Quill', 'Rook',
  'Scout', 'Tango', 'Umbra', 'Vex', 'Wisp', 'Yara', 'Zephyr',
];

let nextPlayerId = 1;
const newId = () => 'p' + (nextPlayerId++).toString(36) + Math.random().toString(36).slice(2, 5);

export class Player {
  constructor({ id, name, color, conn = null, bot = false }) {
    this.id = id;
    this.name = name;
    this.color = color;
    this.conn = conn;
    this.bot = bot;
    this.x = SPAWN.x;
    this.y = SPAWN.y;
    this.dir = 1;                 // facing: 1 right, -1 left
    this.moving = false;
    this.input = { dx: 0, dy: 0 };
    this.seq = 0;
    this.role = ROLE.CREW;
    this.alive = true;
    this.inVent = null;
    this.killCooldown = 0;
    this.emergencyUses = 0;
    this.vote = undefined;        // undefined = not voted, null = skip, else id
    this.tasks = [];
    this.connected = true;
    this.brain = null;            // BotBrain for bots
    this.lastActivity = Date.now();
  }

  get ghost() { return !this.alive; }

  publicInfo() {
    return { id: this.id, name: this.name, color: this.color, bot: this.bot, connected: this.connected };
  }
}

export class GameRoom {
  constructor(code, manager) {
    this.code = code;
    this.manager = manager;
    this.players = new Map();
    this.hostId = null;
    this.settings = { ...DEFAULT_SETTINGS };
    this.phase = PHASE.LOBBY;
    this.time = 0;                         // seconds of simulated time
    this.tickCount = 0;
    this.bodies = [];
    this.sabotage = null;
    this.sabotageCooldown = 0;
    this.closedDoors = new Map();          // doorId -> unix ms when it reopens
    this.doorRoomCooldown = new Map();     // room -> unix ms when it can be closed again
    this.meeting = null;
    this.winner = null;
    this.endsAt = 0;
    this.taskBarShown = 0;
    this.createdAt = Date.now();
    this.chatLog = [];
    this.impostorIds = [];
    this.pendingEvents = [];               // one-off events flushed with the next snapshot
  }

  // -- membership ----------------------------------------------------------

  get playerList() { return [...this.players.values()]; }
  get humanCount() { return this.playerList.filter((p) => !p.bot).length; }
  get alivePlayers() { return this.playerList.filter((p) => p.alive); }

  freeColor(preferred) {
    const taken = new Set(this.playerList.map((p) => p.color));
    if (preferred && !taken.has(preferred)) return preferred;
    const free = COLORS.find((c) => !taken.has(c.id));
    return free ? free.id : COLORS[0].id;
  }

  addPlayer({ name, color, conn, bot = false }) {
    if (this.players.size >= MAX_PLAYERS) return { error: 'This lobby is full.' };
    if (this.phase !== PHASE.LOBBY) return { error: 'That game has already started.' };
    const player = new Player({
      id: newId(),
      name: sanitizeName(name) || (bot ? randomBotName(this) : 'Player'),
      color: this.freeColor(color),
      conn,
      bot,
    });
    if (bot) player.brain = new BotBrain(player, this);
    this.players.set(player.id, player);
    if (!this.hostId || !this.players.has(this.hostId)) this.hostId = player.id;
    this.broadcastLobby();
    return { player };
  }

  removePlayer(id) {
    const player = this.players.get(id);
    if (!player) return;
    this.players.delete(id);
    // A body of a player who left is meaningless - drop it.
    this.bodies = this.bodies.filter((b) => b.playerId !== id);
    if (this.meeting) this.meeting.votes.delete(id);

    if (this.hostId === id) {
      const next = this.playerList.find((p) => !p.bot) || this.playerList[0];
      this.hostId = next ? next.id : null;
    }
    if (this.phase === PHASE.LOBBY) {
      this.broadcastLobby();
    } else {
      this.broadcast({ t: 'left', id, name: player.name });
      this.impostorIds = this.impostorIds.filter((i) => i !== id);
      if (!this.checkWin()) this.broadcastLobbyRoster();
    }
    if (this.humanCount === 0) this.manager?.destroyRoom(this.code);
  }

  setHostSetting(playerId, settings) {
    if (playerId !== this.hostId || this.phase !== PHASE.LOBBY) return;
    this.settings = sanitizeSettings(settings, this.players.size);
    this.broadcastLobby();
  }

  setColor(player, color) {
    if (this.phase !== PHASE.LOBBY) return;
    if (!COLORS.some((c) => c.id === color)) return;
    if (this.playerList.some((p) => p !== player && p.color === color)) return;
    player.color = color;
    this.broadcastLobby();
  }

  setName(player, name) {
    const clean = sanitizeName(name);
    if (!clean) return;
    player.name = clean;
    this.broadcastLobby();
  }

  addBot(playerId) {
    if (playerId !== this.hostId || this.phase !== PHASE.LOBBY) return;
    this.addPlayer({ name: null, color: null, conn: null, bot: true });
  }

  removeBot(playerId) {
    if (playerId !== this.hostId || this.phase !== PHASE.LOBBY) return;
    const bots = this.playerList.filter((p) => p.bot);
    if (bots.length) this.removePlayer(bots[bots.length - 1].id);
  }

  // -- round lifecycle -----------------------------------------------------

  startGame(playerId) {
    if (playerId !== this.hostId) return;
    if (this.phase !== PHASE.LOBBY) return;
    if (this.players.size < MIN_PLAYERS) {
      this.sendTo(this.players.get(playerId), { t: 'error', msg: `Need at least ${MIN_PLAYERS} players.` });
      return;
    }
    this.settings = sanitizeSettings(this.settings, this.players.size);

    const roster = this.playerList;
    shuffle(roster);
    const impostorCount = Math.min(this.settings.impostors, Math.floor((roster.length - 1) / 2) || 1);
    this.impostorIds = roster.slice(0, impostorCount).map((p) => p.id);

    const crewIds = [];
    for (const p of roster) {
      p.role = this.impostorIds.includes(p.id) ? ROLE.IMPOSTOR : ROLE.CREW;
      p.alive = true;
      p.inVent = null;
      p.vote = undefined;
      p.emergencyUses = 0;
      p.killCooldown = this.settings.killCooldown;
      p.input = { dx: 0, dy: 0 };
      crewIds.push(p.id);
      const angle = Math.random() * Math.PI * 2;
      const radius = SPAWN.r * (0.35 + Math.random() * 0.65);
      p.x = SPAWN.x + Math.cos(angle) * radius;
      p.y = SPAWN.y + Math.sin(angle) * radius;
      settle(p);
      if (p.brain) p.brain.reset();
    }

    // Everyone (impostors included, so they can fake) gets a task list.
    const lists = assignTasks(crewIds, this.settings);
    for (const p of roster) p.tasks = lists.get(p.id);

    this.phase = PHASE.PLAYING;
    this.bodies = [];
    this.sabotage = null;
    this.sabotageCooldown = 10;
    this.closedDoors.clear();
    this.doorRoomCooldown.clear();
    this.meeting = null;
    this.winner = null;
    this.chatLog = [];
    this.tickCount = 0;
    this.taskBarShown = 0;

    for (const p of roster) {
      this.sendTo(p, {
        t: 'start',
        you: p.id,
        role: p.role,
        impostors: p.role === ROLE.IMPOSTOR ? this.impostorIds : [],
        tasks: p.tasks,
        settings: this.settings,
        players: roster.map((q) => q.publicInfo()),
        spawn: { x: p.x, y: p.y },
      });
    }
  }

  endGame(winner, reason) {
    this.phase = PHASE.ENDED;
    this.winner = winner;
    this.endsAt = this.time + 8;
    this.meeting = null;
    this.broadcast({
      t: 'end',
      winner,
      reason,
      impostors: this.impostorIds,
      players: this.playerList.map((p) => ({ ...p.publicInfo(), role: p.role, alive: p.alive })),
    });
  }

  returnToLobby() {
    this.phase = PHASE.LOBBY;
    this.winner = null;
    this.bodies = [];
    this.sabotage = null;
    this.meeting = null;
    this.closedDoors.clear();
    for (const p of this.playerList) {
      p.alive = true;
      p.role = ROLE.CREW;
      p.tasks = [];
      p.inVent = null;
      p.vote = undefined;
      p.x = SPAWN.x + (Math.random() - 0.5) * 120;
      p.y = SPAWN.y + (Math.random() - 0.5) * 120;
      settle(p);
    }
    this.broadcastLobby();
  }

  // -- incoming messages ---------------------------------------------------

  handleMessage(player, msg) {
    if (!msg || typeof msg.t !== 'string') return;
    player.lastActivity = Date.now();
    switch (msg.t) {
      case 'input': {
        if (this.phase !== PHASE.PLAYING) return;
        const dx = Number(msg.dx) || 0, dy = Number(msg.dy) || 0;
        const len = Math.hypot(dx, dy) || 1;
        player.input = len > 1 ? { dx: dx / len, dy: dy / len } : { dx, dy };
        player.seq = Number(msg.seq) || player.seq;
        break;
      }
      case 'settings': this.setHostSetting(player.id, msg.settings); break;
      case 'color': this.setColor(player, msg.color); break;
      case 'name': this.setName(player, msg.name); break;
      case 'start': this.startGame(player.id); break;
      case 'addBot': this.addBot(player.id); break;
      case 'removeBot': this.removeBot(player.id); break;
      case 'taskStep': this.completeTaskStep(player, msg.uid); break;
      case 'kill': this.tryKill(player, msg.targetId); break;
      case 'report': this.tryReport(player, msg.bodyId); break;
      case 'emergency': this.tryEmergency(player); break;
      case 'vent': this.tryVent(player, msg.action, msg.ventId); break;
      case 'sabotage': this.trySabotage(player, msg.kind, msg.room); break;
      case 'fix': this.tryFix(player, msg.kind, msg.data); break;
      case 'vote': this.castVote(player, msg.targetId); break;
      case 'chat': this.handleChat(player, msg.text); break;
      case 'ping': this.sendTo(player, { t: 'pong', ts: msg.ts }); break;
      case 'lobby': if (this.phase === PHASE.ENDED && player.id === this.hostId) this.returnToLobby(); break;
      default: break;
    }
  }

  // -- tasks ---------------------------------------------------------------

  completeTaskStep(player, uid) {
    if (this.phase !== PHASE.PLAYING) return;
    const task = player.tasks.find((t) => t.uid === uid);
    if (!task || task.complete) return;
    const step = currentStep(task);
    if (!step) return;
    // Proximity check (a small slack covers client-side prediction drift).
    if (dist(player.x, player.y, step.x, step.y) > INTERACT_RANGE + 60) return;

    step.done = true;
    task.step = task.steps.filter((s) => s.done).length;
    task.complete = task.steps.every((s) => s.done);

    if (step.visual && this.settings.visualTasks && player.role === ROLE.CREW && player.alive) {
      this.pushEvent({ t: 'visual', kind: step.minigame, id: player.id, x: step.x, y: step.y });
    }
    this.sendTo(player, { t: 'tasks', tasks: player.tasks });
    this.checkWin();
  }

  /** Crew-only progress (impostor "tasks" are fake and never count). */
  taskBar() {
    const lists = this.playerList
      .filter((p) => p.role === ROLE.CREW)
      .map((p) => p.tasks || []);
    return taskProgress(lists);
  }

  // -- killing, bodies, vents ---------------------------------------------

  killRange() { return KILL_DISTANCES[this.settings.killDistance] ?? KILL_DISTANCES.normal; }

  /** Best kill target for an impostor, or null. */
  killTarget(player) {
    if (player.role !== ROLE.IMPOSTOR || !player.alive || player.inVent) return null;
    if (player.killCooldown > 0) return null;
    if (this.phase !== PHASE.PLAYING) return null;
    const range = this.killRange();
    let best = null, bestD = Infinity;
    for (const other of this.players.values()) {
      if (other === player || !other.alive || other.inVent) continue;
      const d = dist(player.x, player.y, other.x, other.y);
      if (d > range || d >= bestD) continue;
      if (!this.hasLineOfSight(player, other)) continue;
      best = other; bestD = d;
    }
    return best;
  }

  tryKill(player, targetId) {
    const target = this.players.get(targetId);
    const best = this.killTarget(player);
    if (!target || !best) return;
    if (dist(player.x, player.y, target.x, target.y) > this.killRange() + 10) return;
    if (!target.alive || target.inVent) return;

    target.alive = false;
    target.inVent = null;
    target.input = { dx: 0, dy: 0 };
    player.killCooldown = this.settings.killCooldown;
    // The killer snaps onto the victim, as in the original.
    player.x = target.x;
    player.y = target.y;
    settle(player);

    const body = {
      id: 'b' + Math.random().toString(36).slice(2, 8),
      playerId: target.id,
      color: target.color,
      name: target.name,
      x: target.x,
      y: target.y,
    };
    this.bodies.push(body);
    this.pushEvent({ t: 'killed', killer: player.id, victim: target.id, x: target.x, y: target.y });
    this.sendTo(target, { t: 'died', by: player.id });
    this.checkWin();
  }

  tryReport(player, bodyId) {
    if (this.phase !== PHASE.PLAYING || !player.alive) return;
    if (this.criticalSabotageActive()) return;
    const body = this.bodies.find((b) => b.id === bodyId) || this.nearestBody(player);
    if (!body) return;
    if (dist(player.x, player.y, body.x, body.y) > BODY_REPORT_RANGE + 40) return;
    this.startMeeting('body', player, body);
  }

  nearestBody(player) {
    let best = null, bestD = BODY_REPORT_RANGE;
    for (const b of this.bodies) {
      const d = dist(player.x, player.y, b.x, b.y);
      if (d < bestD) { best = b; bestD = d; }
    }
    return best;
  }

  tryEmergency(player) {
    if (this.phase !== PHASE.PLAYING || !player.alive) return;
    if (this.criticalSabotageActive()) return;
    if (dist(player.x, player.y, EMERGENCY_BUTTON.x, EMERGENCY_BUTTON.y) > EMERGENCY_BUTTON.r + 30) return;
    if (player.emergencyUses >= this.settings.emergencyMeetings) {
      this.sendTo(player, { t: 'notice', msg: 'No emergency meetings left.' });
      return;
    }
    const since = this.time - (this.lastMeetingEnd ?? -Infinity);
    if (this.lastMeetingEnd !== undefined && since < this.settings.emergencyCooldown) {
      this.sendTo(player, { t: 'notice', msg: `Emergency cooldown: ${Math.ceil(this.settings.emergencyCooldown - since)}s` });
      return;
    }
    player.emergencyUses++;
    this.startMeeting('emergency', player, null);
  }

  tryVent(player, action, ventId) {
    if (this.phase !== PHASE.PLAYING || player.role !== ROLE.IMPOSTOR || !player.alive) return;
    if (action === 'enter') {
      const vent = VENTS.find((v) => dist(player.x, player.y, v.x, v.y) <= VENT_RANGE + 20);
      if (!vent || player.inVent) return;
      player.inVent = vent.id;
      player.x = vent.x; player.y = vent.y;
      player.input = { dx: 0, dy: 0 };
      this.sendTo(player, { t: 'vent', state: 'in', ventId: vent.id, links: VENT_BY_ID.get(vent.id).links });
      this.pushEvent({ t: 'ventfx', id: player.id, x: vent.x, y: vent.y, enter: true });
    } else if (action === 'move') {
      if (!player.inVent) return;
      const from = VENT_BY_ID.get(player.inVent);
      if (!from || !from.links.includes(ventId)) return;
      const to = VENT_BY_ID.get(ventId);
      player.inVent = to.id;
      player.x = to.x; player.y = to.y;
      this.sendTo(player, { t: 'vent', state: 'in', ventId: to.id, links: to.links });
    } else if (action === 'exit') {
      if (!player.inVent) return;
      const vent = VENT_BY_ID.get(player.inVent);
      player.inVent = null;
      player.x = vent.x; player.y = vent.y + 6;
      settle(player);
      this.sendTo(player, { t: 'vent', state: 'out' });
      this.pushEvent({ t: 'ventfx', id: player.id, x: vent.x, y: vent.y, enter: false });
    }
  }

  // -- sabotage ------------------------------------------------------------

  criticalSabotageActive() {
    return !!this.sabotage && (this.sabotage.kind === 'reactor' || this.sabotage.kind === 'o2');
  }

  trySabotage(player, kind, room) {
    if (this.phase !== PHASE.PLAYING) return;
    if (player.role !== ROLE.IMPOSTOR || !player.alive) return;
    if (this.sabotageCooldown > 0) return;

    if (kind === 'doors') {
      if (!DOOR_ROOMS.includes(room)) return;
      if ((this.doorRoomCooldown.get(room) || 0) > this.time) return;
      const doors = DOORS.filter((d) => d.room === room);
      for (const d of doors) this.closedDoors.set(d.id, this.time + SABOTAGE.doorDuration);
      this.doorRoomCooldown.set(room, this.time + SABOTAGE.doorDuration + SABOTAGE.doorCooldown);
      this.sabotageCooldown = SABOTAGE.doorCooldown;
      this.pushEvent({ t: 'doors', room, closed: doors.map((d) => d.id) });
      return;
    }

    if (this.sabotage) return;
    if (!['lights', 'comms', 'reactor', 'o2'].includes(kind)) return;

    const critical = kind === 'reactor' || kind === 'o2';
    this.sabotage = {
      kind,
      startedAt: this.time,
      timeLeft: critical ? (kind === 'reactor' ? SABOTAGE.reactorTime : SABOTAGE.o2Time) : 0,
      critical,
      pads: {},          // reactor: padId -> timestamp last held
      fixed: {},         // o2: padId -> true
      by: player.id,
    };
    this.sabotageCooldown = SABOTAGE.cooldown;
    this.broadcast({ t: 'sabotage', kind, critical, time: this.sabotage.timeLeft });
  }

  tryFix(player, kind, data) {
    if (this.phase !== PHASE.PLAYING || !this.sabotage || this.sabotage.kind !== kind) return;
    if (!player.alive) return;
    const points = FIX_POINTS[kind] || [];

    if (kind === 'lights' || kind === 'comms') {
      const p = points[0];
      if (dist(player.x, player.y, p.x, p.y) > INTERACT_RANGE + 60) return;
      this.clearSabotage();
      return;
    }

    if (kind === 'o2') {
      const pad = points.find((p) => p.id === data?.pad);
      if (!pad) return;
      if (dist(player.x, player.y, pad.x, pad.y) > INTERACT_RANGE + 60) return;
      this.sabotage.fixed[pad.id] = true;
      this.broadcast({ t: 'sabfix', kind, pad: pad.id, fixed: Object.keys(this.sabotage.fixed) });
      if (points.every((p) => this.sabotage.fixed[p.id])) this.clearSabotage();
      return;
    }

    if (kind === 'reactor') {
      const pad = points.find((p) => p.id === data?.pad);
      if (!pad) return;
      if (dist(player.x, player.y, pad.x, pad.y) > INTERACT_RANGE + 60) return;
      // "Holding" is a heartbeat: the client pings while a hand is down.
      this.sabotage.pads[pad.id] = { at: this.time, by: player.id };
      const held = points.filter((p) => {
        const h = this.sabotage.pads[p.id];
        return h && this.time - h.at < 0.7;
      });
      this.broadcast({ t: 'sabfix', kind, pads: held.map((p) => p.id) });
      if (held.length === points.length) this.clearSabotage();
    }
  }

  clearSabotage() {
    if (!this.sabotage) return;
    const kind = this.sabotage.kind;
    this.sabotage = null;
    this.broadcast({ t: 'sabotageFixed', kind });
  }

  // -- meetings ------------------------------------------------------------

  startMeeting(reason, caller, body) {
    this.phase = PHASE.MEETING;
    this.bodies = [];
    this.closedDoors.clear();
    if (this.sabotage && this.sabotage.critical) this.clearSabotage();

    for (const p of this.playerList) {
      p.vote = undefined;
      p.input = { dx: 0, dy: 0 };
      p.inVent = null;
      if (p.alive) {
        const i = this.alivePlayers.indexOf(p);
        const angle = (i / Math.max(1, this.alivePlayers.length)) * Math.PI * 2;
        p.x = SPAWN.x + Math.cos(angle) * 110;
        p.y = SPAWN.y + Math.sin(angle) * 110;
        settle(p);
      }
      if (p.brain) p.brain.onMeeting();
    }

    this.taskBarShown = this.taskBar().ratio;
    this.meeting = {
      reason,
      by: caller ? caller.id : null,
      bodyOf: body ? body.playerId : null,
      phase: this.settings.discussionTime > 0 ? MEETING_PHASE.DISCUSS : MEETING_PHASE.VOTE,
      ends: this.time + (this.settings.discussionTime > 0 ? this.settings.discussionTime : this.settings.votingTime),
      votes: new Map(),
      result: null,
      chat: [],
    };
    this.broadcast({
      t: 'meeting',
      reason,
      by: caller ? caller.id : null,
      bodyOf: body ? body.playerId : null,
      bodyName: body ? body.name : null,
      phase: this.meeting.phase,
      secs: Math.max(0, Math.round(this.meeting.ends - this.time)),
      players: this.playerList.map((p) => ({ ...p.publicInfo(), alive: p.alive })),
      taskbar: this.settings.taskBarUpdates === 'never' ? null : this.taskBarShown,
    });
  }

  castVote(player, targetId) {
    if (this.phase !== PHASE.MEETING || !this.meeting) return;
    if (this.meeting.phase !== MEETING_PHASE.VOTE) return;
    if (!player.alive || player.vote !== undefined) return;
    if (targetId !== null && targetId !== 'skip') {
      const target = this.players.get(targetId);
      if (!target || !target.alive) return;
    }
    player.vote = targetId === 'skip' ? null : targetId;
    this.meeting.votes.set(player.id, player.vote);
    this.broadcast({ t: 'voted', id: player.id, count: this.meeting.votes.size, of: this.alivePlayers.length });
    if (this.alivePlayers.every((p) => p.vote !== undefined)) this.finishVoting();
  }

  finishVoting() {
    if (!this.meeting || this.meeting.phase === MEETING_PHASE.RESULTS) return;
    const tally = new Map();
    for (const p of this.alivePlayers) {
      const v = p.vote === undefined ? 'novote' : (p.vote === null ? 'skip' : p.vote);
      if (v === 'novote') continue;
      tally.set(v, (tally.get(v) || 0) + 1);
    }
    let top = null, topCount = 0, tie = false;
    for (const [k, c] of tally) {
      if (c > topCount) { top = k; topCount = c; tie = false; }
      else if (c === topCount) tie = true;
    }
    const ejectedId = (!top || top === 'skip' || tie) ? null : top;
    const ejected = ejectedId ? this.players.get(ejectedId) : null;
    if (ejected) {
      ejected.alive = false;
      ejected.inVent = null;
    }
    this.meeting.phase = MEETING_PHASE.RESULTS;
    this.meeting.ends = this.time + MEETING.ejectAnimation;
    this.meeting.result = {
      ejected: ejectedId,
      tie,
      skipped: !ejectedId,
      wasImpostor: ejected ? ejected.role === ROLE.IMPOSTOR : false,
      remainingImpostors: this.alivePlayers.filter((p) => p.role === ROLE.IMPOSTOR).length,
    };
    this.broadcast({
      t: 'voteResult',
      votes: [...this.meeting.votes.entries()].map(([voter, target]) => ({
        voter: this.settings.anonymousVotes ? null : voter,
        target: target === undefined ? undefined : target,
      })),
      ...this.meeting.result,
      confirmEjects: this.settings.confirmEjects,
      ejectedName: ejected ? ejected.name : null,
      ejectedColor: ejected ? ejected.color : null,
    });
  }

  endMeeting() {
    this.lastMeetingEnd = this.time;
    this.meeting = null;
    if (this.checkWin()) return;
    this.phase = PHASE.PLAYING;
    for (const p of this.playerList) {
      p.vote = undefined;
      if (p.role === ROLE.IMPOSTOR) {
        p.killCooldown = Math.max(this.settings.killCooldown * 0.4, MEETING.postMeetingKillCooldown);
      }
    }
    this.sabotageCooldown = Math.max(this.sabotageCooldown, 5);
    this.broadcast({ t: 'resume', players: this.playerList.map((p) => ({ id: p.id, alive: p.alive })) });
  }

  handleChat(player, text) {
    const clean = String(text || '').slice(0, 200).replace(/[\u0000-\u001f]/g, '').trim();
    if (!clean) return;
    // Alive players may only talk during a meeting; ghosts get their own channel.
    const dead = !player.alive;
    if (!dead && this.phase !== PHASE.MEETING) return;
    const entry = { from: player.id, name: player.name, color: player.color, text: clean, dead, at: Date.now() };
    if (this.meeting) this.meeting.chat.push(entry);
    for (const p of this.playerList) {
      if (dead && p.alive) continue;   // ghost chat is invisible to the living
      this.sendTo(p, { t: 'chat', ...entry });
    }
  }

  // -- win conditions ------------------------------------------------------

  checkWin() {
    if (this.phase === PHASE.LOBBY || this.phase === PHASE.ENDED) return false;
    const alive = this.alivePlayers;
    const imps = alive.filter((p) => p.role === ROLE.IMPOSTOR);
    const crew = alive.filter((p) => p.role === ROLE.CREW);

    if (imps.length === 0) {
      this.endGame('crew', WIN.CREW_VOTE);
      return true;
    }
    if (imps.length >= crew.length) {
      this.endGame('impostor', this.phase === PHASE.MEETING ? WIN.IMPOSTOR_VOTE : WIN.IMPOSTOR_KILL);
      return true;
    }
    const bar = this.taskBar();
    if (bar.total > 0 && bar.done >= bar.total) {
      this.endGame('crew', WIN.CREW_TASKS);
      return true;
    }
    return false;
  }

  // -- simulation ----------------------------------------------------------

  tick(dt) {
    this.time += dt;

    if (this.phase === PHASE.PLAYING) {
      // Doors reopen on their own.
      for (const [id, until] of this.closedDoors) if (until <= this.time) this.closedDoors.delete(id);
      const doorSegs = doorSegments([...this.closedDoors.keys()]);

      for (const p of this.players.values()) {
        if (p.brain) p.brain.think(dt);
        if (p.killCooldown > 0) p.killCooldown = Math.max(0, p.killCooldown - dt);
        if (p.inVent) continue;
        const moved = stepMove(p, p.input, dt, {
          speed: speedFor(p, this.settings),
          doorSegs: p.alive ? doorSegs : [],
          noclip: !p.alive,
        });
        p.moving = moved && (Math.abs(p.input.dx) + Math.abs(p.input.dy)) > 0.01;
        if (Math.abs(p.input.dx) > 0.05) p.dir = p.input.dx > 0 ? 1 : -1;
      }

      if (this.sabotageCooldown > 0) this.sabotageCooldown = Math.max(0, this.sabotageCooldown - dt);

      if (this.sabotage && this.sabotage.critical) {
        this.sabotage.timeLeft -= dt;
        if (this.sabotage.timeLeft <= 0) {
          const kind = this.sabotage.kind;
          this.sabotage = null;
          this.endGame('impostor', kind === 'reactor' ? WIN.IMPOSTOR_SABOTAGE : WIN.IMPOSTOR_SABOTAGE);
          return;
        }
      }
    } else if (this.phase === PHASE.MEETING && this.meeting) {
      for (const p of this.players.values()) if (p.brain) p.brain.thinkMeeting(dt);
      if (this.time >= this.meeting.ends) {
        if (this.meeting.phase === MEETING_PHASE.DISCUSS) {
          this.meeting.phase = MEETING_PHASE.VOTE;
          this.meeting.ends = this.time + this.settings.votingTime;
          this.broadcast({ t: 'meetingPhase', phase: MEETING_PHASE.VOTE, secs: this.settings.votingTime });
        } else if (this.meeting.phase === MEETING_PHASE.VOTE) {
          this.finishVoting();
        } else {
          this.endMeeting();
        }
      }
    } else if (this.phase === PHASE.ENDED) {
      if (this.time >= this.endsAt) this.returnToLobby();
    }

    this.tickCount++;
    this.sendSnapshots();
  }

  // -- visibility & snapshots ---------------------------------------------

  visionRadius(player) {
    if (!player.alive) return Infinity;
    if (player.role === ROLE.IMPOSTOR) return VISION.impostor * this.settings.impostorVision;
    const lightsOut = this.sabotage && this.sabotage.kind === 'lights';
    return VISION.crew * this.settings.crewVision * (lightsOut ? VISION.lightsOut : 1);
  }

  blockingSegments(x, y, r) {
    const near = segmentsNear(x, y, r, WALLS, []);
    for (const id of this.closedDoors.keys()) {
      const d = DOORS.find((dd) => dd.id === id);
      if (d) near.push(d);
    }
    return near;
  }

  hasLineOfSight(a, b) {
    const segs = this.blockingSegments(a.x, a.y, dist(a.x, a.y, b.x, b.y) + 40);
    return lineOfSight(a.x, a.y, b.x, b.y, segs);
  }

  canSee(viewer, target) {
    if (viewer === target) return true;
    if (!viewer.alive) return true;              // ghosts see everything
    if (!target.alive) return false;             // the living never see ghosts
    if (target.inVent) return false;
    const r = this.visionRadius(viewer) * 1.2 + PLAYER_RADIUS * 2;
    if (dist(viewer.x, viewer.y, target.x, target.y) > r) return false;
    return this.hasLineOfSight(viewer, target);
  }

  pushEvent(ev) { this.pendingEvents.push(ev); }

  sendSnapshots() {
    const now = Date.now();
    const closed = [...this.closedDoors.keys()];
    const bar = this.taskBar();
    const barValue =
      this.settings.taskBarUpdates === 'never' ? null
        : this.settings.taskBarUpdates === 'meetings' ? this.taskBarShown
          : bar.ratio;
    const commsDown = !!this.sabotage && this.sabotage.kind === 'comms';

    const sab = this.sabotage ? {
      k: this.sabotage.kind,
      t: this.sabotage.critical ? Math.max(0, this.sabotage.timeLeft) : 0,
      f: this.sabotage.kind === 'o2' ? Object.keys(this.sabotage.fixed) : undefined,
    } : null;

    for (const viewer of this.players.values()) {
      if (viewer.bot || !viewer.conn) continue;
      const players = [];
      for (const p of this.players.values()) {
        if (!this.canSee(viewer, p)) continue;
        players.push({
          i: p.id,
          x: Math.round(p.x * 10) / 10,
          y: Math.round(p.y * 10) / 10,
          d: p.dir,
          m: p.moving ? 1 : 0,
          g: p.alive ? 0 : 1,
          v: p.inVent ? 1 : 0,
        });
      }
      const bodies = this.bodies
        .filter((b) => !viewer.alive || this.visibleBody(viewer, b))
        .map((b) => ({ i: b.id, c: b.color, x: b.x, y: b.y, n: b.name }));

      const payload = {
        t: 's',
        k: this.tickCount,
        n: now,
        seq: viewer.seq,
        me: { x: Math.round(viewer.x * 10) / 10, y: Math.round(viewer.y * 10) / 10, cd: Math.round(viewer.killCooldown * 10) / 10 },
        p: players,
        b: bodies,
        dr: closed,
        sab,
        bar: commsDown ? null : barValue,
        sc: this.sabotageCooldown > 0 ? Math.ceil(this.sabotageCooldown) : 0,
      };
      if (this.meeting) payload.mt = Math.max(0, this.meeting.ends - this.time);
      if (!commsDown && dist(viewer.x, viewer.y, ADMIN_TABLE.x, ADMIN_TABLE.y) < ADMIN_TABLE.r + 40) {
        payload.adm = this.adminCounts();
      }
      if (this.pendingEvents.length) {
        const evs = this.pendingEvents.filter((ev) => this.eventVisibleTo(viewer, ev));
        if (evs.length) payload.ev = evs;
      }
      this.sendTo(viewer, payload);
    }
    this.pendingEvents.length = 0;
  }

  eventVisibleTo(viewer, ev) {
    if (!viewer.alive) return true;
    if (ev.x === undefined) return true;
    const r = this.visionRadius(viewer) * 1.2;
    if (dist(viewer.x, viewer.y, ev.x, ev.y) > r) return false;
    return lineOfSight(viewer.x, viewer.y, ev.x, ev.y, this.blockingSegments(viewer.x, viewer.y, r));
  }

  visibleBody(viewer, body) {
    const r = this.visionRadius(viewer) * 1.2;
    if (dist(viewer.x, viewer.y, body.x, body.y) > r) return false;
    return lineOfSight(viewer.x, viewer.y, body.x, body.y, this.blockingSegments(viewer.x, viewer.y, r));
  }

  adminCounts() {
    const counts = {};
    for (const p of this.players.values()) {
      if (!p.alive || p.inVent) continue;
      const room = roomAt(p.x, p.y);
      if (!room) continue;
      counts[room.id] = (counts[room.id] || 0) + 1;
    }
    return counts;
  }

  // -- transport -----------------------------------------------------------

  sendTo(player, msg) {
    if (!player || !player.conn || !player.conn.open) return;
    player.conn.sendJSON(msg);
  }

  broadcast(msg, filter = null) {
    for (const p of this.players.values()) {
      if (filter && !filter(p)) continue;
      this.sendTo(p, msg);
    }
  }

  lobbyState() {
    return {
      t: 'lobby',
      code: this.code,
      hostId: this.hostId,
      settings: this.settings,
      phase: this.phase,
      players: this.playerList.map((p) => p.publicInfo()),
    };
  }

  broadcastLobby() { this.broadcast(this.lobbyState()); }
  broadcastLobbyRoster() {
    this.broadcast({ t: 'roster', players: this.playerList.map((p) => ({ ...p.publicInfo(), alive: p.alive })) });
  }
}

// ---------------------------------------------------------------------------

function shuffle(arr) {
  for (let i = arr.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j], arr[i]];
  }
  return arr;
}

export function sanitizeName(name) {
  return String(name || '')
    .replace(/[\u0000-\u001f<>]/g, '')
    .trim()
    .slice(0, 12);
}

function randomBotName(room) {
  const taken = new Set([...room.players.values()].map((p) => p.name));
  const free = BOT_NAMES.filter((n) => !taken.has(n));
  const pool = free.length ? free : BOT_NAMES;
  return pool[Math.floor(Math.random() * pool.length)];
}
