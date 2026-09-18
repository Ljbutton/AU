// server/bots.js
// Lightweight AI crewmates/impostors so a lobby can be filled out and tested
// solo. Bots use the same movement code and the same public room actions a
// human client would send - they have no special powers.

import { findPath, VENTS, VENT_BY_ID, ROOMS, DOOR_ROOMS, EMERGENCY_BUTTON } from '../shared/map.js';
import { currentStep } from '../shared/tasks.js';
import { dist } from '../shared/geom.js';
import { ROLE, BODY_REPORT_RANGE, INTERACT_RANGE } from '../shared/constants.js';

/** Roughly how long each console takes a bot, in seconds. */
const WORK_TIME = {
  wires: 4, swipe: 3, calibrate: 4, chart: 3, leaves: 5, asteroids: 6, shields: 4,
  steering: 3, manifolds: 5, align: 3, divert: 3, accept: 2, download: 8, upload: 8,
  garbage: 4, fuelpick: 3, fuel: 5, sample: 12, simon: 7, scan: 10,
};

const CHATTER_CREW = [
  'where?', 'i was in electrical', 'i saw nothing', 'skip?', 'sus', 'who reported',
  'i was doing wires', 'green was following me', 'idk', 'not me', 'that is weird',
  'i can vouch for blue', 'medbay scan clear', 'nobody was in nav',
];
const CHATTER_IMP = [
  'i was in admin', 'skip', 'why would i', 'i was with red the whole time',
  'wasnt me', 'seems sus', 'i just got there', 'lets skip this round',
];

export class BotBrain {
  constructor(player, room) {
    this.p = player;
    this.room = room;
    this.reset();
  }

  reset() {
    this.path = null;
    this.goal = null;
    this.goalKind = 'idle';
    this.workTimer = 0;
    this.repathTimer = 0;
    this.stuckTimer = 0;
    this.lastPos = { x: this.p.x, y: this.p.y };
    this.decisionTimer = 0;
    this.ventTimer = 8 + Math.random() * 20;
    this.sabotageTimer = 20 + Math.random() * 30;
    this.chatTimer = 2 + Math.random() * 8;
    this.voteTimer = 4 + Math.random() * 10;
    this.chattedThisMeeting = 0;
  }

  onMeeting() {
    this.path = null;
    this.goal = null;
    this.workTimer = 0;
    this.voteTimer = 5 + Math.random() * 12;
    this.chatTimer = 1 + Math.random() * 6;
    this.chattedThisMeeting = 0;
    this.p.input = { dx: 0, dy: 0 };
  }

  // -- meeting behaviour ---------------------------------------------------

  thinkMeeting(dt) {
    const room = this.room, p = this.p;
    if (!room.meeting) return;

    this.chatTimer -= dt;
    if (this.chatTimer <= 0 && this.chattedThisMeeting < 2) {
      this.chatTimer = 6 + Math.random() * 14;
      this.chattedThisMeeting++;
      const pool = p.role === ROLE.IMPOSTOR ? CHATTER_IMP : CHATTER_CREW;
      room.handleChat(p, pool[Math.floor(Math.random() * pool.length)]);
    }

    if (room.meeting.phase !== 'vote' || !p.alive || p.vote !== undefined) return;
    this.voteTimer -= dt;
    if (this.voteTimer > 0) return;

    const candidates = room.alivePlayers.filter((q) => q !== p);
    let choice = 'skip';
    if (candidates.length) {
      if (p.role === ROLE.IMPOSTOR) {
        // Impostors never vote a fellow impostor, and skip fairly often.
        const crew = candidates.filter((q) => q.role !== ROLE.IMPOSTOR);
        choice = Math.random() < 0.45 || !crew.length
          ? 'skip'
          : crew[Math.floor(Math.random() * crew.length)].id;
      } else {
        choice = Math.random() < 0.55
          ? 'skip'
          : candidates[Math.floor(Math.random() * candidates.length)].id;
      }
    }
    room.castVote(p, choice);
  }

  // -- round behaviour -----------------------------------------------------

  think(dt) {
    const p = this.p, room = this.room;
    if (room.phase !== 'playing') { p.input = { dx: 0, dy: 0 }; return; }

    if (p.alive && p.role === ROLE.IMPOSTOR) this.impostorUrges(dt);
    if (p.alive) this.maybeReport();

    if (this.workTimer > 0) {
      this.workTimer -= dt;
      p.input = { dx: 0, dy: 0 };
      if (this.workTimer <= 0) this.finishWork();
      return;
    }

    if (p.inVent) { p.input = { dx: 0, dy: 0 }; return; }

    this.decisionTimer -= dt;
    if (!this.goal || this.decisionTimer <= 0) this.chooseGoal();
    this.follow(dt);
  }

  chooseGoal() {
    const p = this.p, room = this.room;
    this.decisionTimer = 12 + Math.random() * 12;

    // A critical sabotage pulls crew (and impostors keeping up appearances).
    if (room.sabotage && room.sabotage.critical && (p.role === ROLE.CREW || Math.random() < 0.5)) {
      const points = room.sabotage.kind === 'reactor'
        ? [{ x: 170, y: 620 }, { x: 370, y: 620 }]
        : [{ x: 2070, y: 590 }, { x: 1840, y: 700 }];
      const pt = points[Math.floor(Math.random() * points.length)];
      this.setGoal(pt.x, pt.y, 'sabfix');
      return;
    }
    if (room.sabotage && room.sabotage.kind === 'lights' && p.role === ROLE.CREW && Math.random() < 0.6) {
      this.setGoal(1180, 840, 'sabfix');
      return;
    }

    // Otherwise head for the next task console (impostors "fake" the same walk).
    const pending = (p.tasks || []).map((t) => ({ t, s: currentStep(t) })).filter((x) => x.s);
    if (pending.length) {
      const target = pending[Math.floor(Math.random() * pending.length)];
      this.taskRef = target.t;
      this.setGoal(target.s.x, target.s.y, 'task');
      return;
    }

    const room2 = ROOMS[Math.floor(Math.random() * ROOMS.length)];
    this.setGoal(
      room2.x1 + 60 + Math.random() * Math.max(20, room2.x2 - room2.x1 - 120),
      room2.y1 + 60 + Math.random() * Math.max(20, room2.y2 - room2.y1 - 120),
      'wander'
    );
  }

  setGoal(x, y, kind) {
    this.goal = { x, y };
    this.goalKind = kind;
    this.path = findPath(this.p.x, this.p.y, x, y) || null;
    this.repathTimer = 3;
  }

  follow(dt) {
    const p = this.p;
    if (!this.path || !this.path.length) {
      if (this.goal) this.arrive();
      else p.input = { dx: 0, dy: 0 };
      return;
    }
    const wp = this.path[0];
    const dx = wp.x - p.x, dy = wp.y - p.y;
    const d = Math.hypot(dx, dy);
    if (d < 18) {
      this.path.shift();
      if (!this.path.length) { this.arrive(); return; }
      p.input = { dx: 0, dy: 0 };
      return;
    }
    p.input = { dx: dx / d, dy: dy / d };

    // Stuck detection: a door slammed in our face, or a corner we cannot round.
    const moved = Math.hypot(p.x - this.lastPos.x, p.y - this.lastPos.y);
    this.lastPos = { x: p.x, y: p.y };
    this.stuckTimer = moved < 0.6 ? this.stuckTimer + dt : 0;
    this.repathTimer -= dt;
    if (this.stuckTimer > 1.2) {
      this.stuckTimer = 0;
      if (this.goal) this.setGoal(this.goal.x, this.goal.y, this.goalKind);
      else this.chooseGoal();
      // Shove sideways for a moment so we unhook from the corner.
      p.input = { dx: -p.input.dy, dy: p.input.dx };
    }
  }

  arrive() {
    const p = this.p, room = this.room;
    this.path = null;
    p.input = { dx: 0, dy: 0 };
    if (this.goalKind === 'task' && this.taskRef) {
      const step = currentStep(this.taskRef);
      if (step && dist(p.x, p.y, step.x, step.y) <= INTERACT_RANGE) {
        this.workTimer = (WORK_TIME[step.minigame] || 4) * (0.7 + Math.random() * 0.6);
        return;
      }
    }
    if (this.goalKind === 'sabfix') {
      this.workTimer = 1.5;
      return;
    }
    this.goal = null;
    this.decisionTimer = 0;
  }

  finishWork() {
    const p = this.p, room = this.room;
    if (this.goalKind === 'task' && this.taskRef) {
      // Impostors only mime the animation; their steps never count.
      if (p.role === ROLE.CREW) room.completeTaskStep(p, this.taskRef.uid);
      else {
        const s = currentStep(this.taskRef);
        if (s) s.done = true;
      }
    } else if (this.goalKind === 'sabfix' && room.sabotage) {
      const kind = room.sabotage.kind;
      if (kind === 'lights' || kind === 'comms') room.tryFix(p, kind, {});
      else {
        const pads = kind === 'reactor'
          ? [{ id: 'reactorL', x: 170, y: 620 }, { id: 'reactorR', x: 370, y: 620 }]
          : [{ id: 'o2A', x: 2070, y: 590 }, { id: 'o2B', x: 1840, y: 700 }];
        const pad = pads.find((q) => dist(p.x, p.y, q.x, q.y) < INTERACT_RANGE);
        if (pad) room.tryFix(p, kind, { pad: pad.id });
      }
    }
    this.goal = null;
    this.decisionTimer = 0;
  }

  maybeReport() {
    const p = this.p, room = this.room;
    if (room.criticalSabotageActive()) return;
    for (const b of room.bodies) {
      if (dist(p.x, p.y, b.x, b.y) > BODY_REPORT_RANGE) continue;
      if (p.role === ROLE.IMPOSTOR && Math.random() < 0.7) continue;  // impostors usually walk on by
      if (!room.hasLineOfSight(p, b)) continue;
      room.tryReport(p, b.id);
      return;
    }
  }

  impostorUrges(dt) {
    const p = this.p, room = this.room;

    const target = room.killTarget(p);
    if (target) {
      // Prefer kills with few witnesses nearby.
      const witnesses = room.alivePlayers.filter(
        (q) => q !== p && q !== target && dist(q.x, q.y, p.x, p.y) < 420 && room.hasLineOfSight(q, p)
      ).length;
      if (witnesses === 0 || Math.random() < 0.05) {
        room.tryKill(p, target.id);
        this.goal = null;
        this.decisionTimer = 0;
        return;
      }
    }

    this.sabotageTimer -= dt;
    if (this.sabotageTimer <= 0 && room.sabotageCooldown <= 0 && !room.sabotage) {
      this.sabotageTimer = 30 + Math.random() * 40;
      const roll = Math.random();
      if (roll < 0.35) room.trySabotage(p, 'lights');
      else if (roll < 0.5) room.trySabotage(p, 'comms');
      else if (roll < 0.7) room.trySabotage(p, 'doors', DOOR_ROOMS[Math.floor(Math.random() * DOOR_ROOMS.length)]);
      else if (roll < 0.85) room.trySabotage(p, 'reactor');
      else room.trySabotage(p, 'o2');
    }

    this.ventTimer -= dt;
    if (this.ventTimer <= 0) {
      this.ventTimer = 20 + Math.random() * 30;
      if (p.inVent) {
        const links = VENT_BY_ID.get(p.inVent)?.links || [];
        if (links.length && Math.random() < 0.6) room.tryVent(p, 'move', links[Math.floor(Math.random() * links.length)]);
        else room.tryVent(p, 'exit');
      } else {
        const near = VENTS.find((v) => dist(p.x, p.y, v.x, v.y) < 60);
        const seen = room.alivePlayers.some((q) => q !== p && dist(q.x, q.y, p.x, p.y) < 400 && room.hasLineOfSight(q, p));
        if (near && !seen) {
          room.tryVent(p, 'enter');
          this.ventTimer = 3 + Math.random() * 5;
        }
      }
    }
  }
}
