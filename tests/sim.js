// tests/sim.js - headless game simulation. Runs full rounds with bots only and
// asserts each round reaches a legal end state without throwing.

import { GameRoom } from '../server/game.js';
import { BotBrain } from '../server/bots.js';
import { PHASE } from '../shared/constants.js';
import { RECTS } from '../shared/map.js';
import { pointInAnyRect } from '../shared/geom.js';

class FakeConn {
  constructor(onMsg) { this.open = true; this.onMsg = onMsg; }
  sendJSON(msg) { this.onMsg?.(msg); }
}

/**
 * Run one round to completion. Every player (including the "human", so the
 * round can finish unattended) is driven by a bot brain.
 */
export function simulate({ players = 8, impostors = 2, maxSeconds = 900, settings = {} } = {}) {
  const room = new GameRoom('TEST', null);
  const events = [];
  const snapshots = [];

  const conn = new FakeConn((m) => { (m.t === 's' ? snapshots : events).push(m); });
  const human = room.addPlayer({ name: 'Human', color: 'red', conn }).player;
  room.hostId = human.id;
  for (let i = 1; i < players; i++) room.addPlayer({ name: null, color: null, conn: null, bot: true });

  room.settings = {
    ...room.settings, impostors, discussionTime: 3, votingTime: 8,
    killCooldown: 12, emergencyCooldown: 5, ...settings,
  };
  room.startGame(room.hostId);
  human.brain = new BotBrain(human, room);   // autopilot the observer

  const dt = 1 / 20;
  let t = 0, escapes = 0, meetings = 0, kills = 0;
  const seenEvents = new Set();
  while (t < maxSeconds && room.phase !== PHASE.ENDED) {
    room.tick(dt);
    t += dt;
    for (const p of room.playerList) {
      if (p.alive && !p.inVent && !pointInAnyRect(p.x, p.y, RECTS)) escapes++;
    }
  }
  for (const e of events) {
    if (e.t === 'meeting') meetings++;
    seenEvents.add(e.t);
  }
  for (const s of snapshots) for (const ev of s.ev || []) if (ev.t === 'killed') kills++;

  return {
    room, events, snapshots, seconds: t, escapes, meetings, kills,
    end: events.find((e) => e.t === 'end') || null,
    eventTypes: [...seenEvents],
  };
}
