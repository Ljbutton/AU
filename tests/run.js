#!/usr/bin/env node
// tests/run.js - the project's test suite. Pure Node, no dependencies.

import assert from 'node:assert/strict';
import { simulate } from './sim.js';
import { RECTS, ROOMS, WALLS, VENTS, DOORS, FIX_POINTS, EMERGENCY_BUTTON, ADMIN_TABLE, findPath, roomAt } from '../shared/map.js';
import { pointInAnyRect, distToSegment, lineOfSight } from '../shared/geom.js';
import { stepMove, doorSegments } from '../shared/movement.js';
import { TASK_DEFS, WIRE_PANELS, DATA_SOURCES, POWER_NODES, assignTasks, taskProgress } from '../shared/tasks.js';
import { DEFAULT_SETTINGS, sanitizeSettings, COLORS, PHASE } from '../shared/constants.js';
import { GameRoom } from '../server/game.js';

let passed = 0, failed = 0;
const tests = [];
const test = (name, fn) => tests.push([name, fn]);

// ---------------------------------------------------------------------------
test('map: every walkable rect is reachable from spawn', () => {
  const STEP = 10;
  const key = (x, y) => x + ',' + y;
  const start = [Math.round(1580 / STEP) * STEP, Math.round(360 / STEP) * STEP];
  const seen = new Set([key(...start)]);
  const q = [start];
  while (q.length) {
    const [x, y] = q.pop();
    for (const [dx, dy] of [[STEP, 0], [-STEP, 0], [0, STEP], [0, -STEP]]) {
      const nx = x + dx, ny = y + dy;
      if (!pointInAnyRect(nx, ny, RECTS)) continue;
      const k = key(nx, ny);
      if (seen.has(k)) continue;
      seen.add(k);
      q.push([nx, ny]);
    }
  }
  for (const r of RECTS) {
    const cx = Math.round((r.x1 + r.x2) / 2 / STEP) * STEP;
    const cy = Math.round((r.y1 + r.y2) / 2 / STEP) * STEP;
    assert.ok(seen.has(key(cx, cy)), `${r.id} unreachable`);
  }
});

test('map: doors sit in doorways, not on top of existing walls', () => {
  for (const d of DOORS) {
    const mx = (d.x1 + d.x2) / 2, my = (d.y1 + d.y2) / 2;
    assert.ok(pointInAnyRect(mx, my, RECTS), `${d.id} not in walkable space`);
    const onWall = WALLS.some((w) => distToSegment(mx, my, w) < 2);
    assert.ok(!onWall, `${d.id} duplicates a wall`);
  }
});

test('map: vents, consoles and fix points are inside rooms with clearance', () => {
  const points = [
    ...VENTS.map((v) => ({ id: v.id, x: v.x, y: v.y })),
    ...Object.values(FIX_POINTS).flat(),
    { id: 'emergency', x: EMERGENCY_BUTTON.x, y: EMERGENCY_BUTTON.y },
    { id: 'adminTable', x: ADMIN_TABLE.x, y: ADMIN_TABLE.y },
    ...WIRE_PANELS.map((p, i) => ({ id: 'wire' + i, ...p })),
    ...DATA_SOURCES.map((p, i) => ({ id: 'data' + i, ...p })),
    ...POWER_NODES.map((p, i) => ({ id: 'power' + i, ...p })),
  ];
  for (const p of points) {
    assert.ok(pointInAnyRect(p.x, p.y, RECTS), `${p.id} outside the ship`);
  }
});

test('map: every task console is reachable and clear of walls', () => {
  for (const def of TASK_DEFS) {
    for (let i = 0; i < 25; i++) {
      const steps = typeof def.steps === 'function' ? def.steps(Math.random) : def.steps;
      for (const s of steps) {
        assert.ok(pointInAnyRect(s.x, s.y, RECTS), `${def.id}/${s.minigame} outside the ship`);
        const clearance = Math.min(...WALLS.map((w) => distToSegment(s.x, s.y, w)));
        assert.ok(clearance >= 18, `${def.id}/${s.minigame} is ${clearance.toFixed(1)}px from a wall`);
        const r = roomAt(s.x, s.y);
        assert.equal(r && r.id, s.room, `${def.id}/${s.minigame} is not in ${s.room}`);
      }
    }
  }
});

test('pathing: every room pair is walkable end to end', () => {
  let ok = 0, total = 0;
  for (const a of ROOMS) for (const b of ROOMS) {
    if (a === b) continue;
    total++;
    const ent = { x: (a.x1 + a.x2) / 2, y: (a.y1 + a.y2) / 2 };
    const path = findPath(ent.x, ent.y, (b.x1 + b.x2) / 2, (b.y1 + b.y2) / 2);
    assert.ok(path, `no path ${a.id} -> ${b.id}`);
    let ticks = 0;
    while (path.length && ticks++ < 3000) {
      const wp = path[0];
      const dx = wp.x - ent.x, dy = wp.y - ent.y;
      const d = Math.hypot(dx, dy);
      if (d < 18) { path.shift(); continue; }
      stepMove(ent, { dx: dx / d, dy: dy / d }, 1 / 20, { speed: 230 });
    }
    assert.equal(path.length, 0, `walker stuck ${a.id} -> ${b.id}`);
    ok++;
  }
  assert.equal(ok, total);
});

test('movement: random walkers never escape the ship, even through closed doors', () => {
  const segs = doorSegments(DOORS.map((d) => d.id));
  const walkers = Array.from({ length: 40 }, () => ({ x: 1580, y: 360, dir: Math.random() * 7 }));
  for (let t = 0; t < 2000; t++) {
    for (const w of walkers) {
      if (Math.random() < 0.06) w.dir += (Math.random() - 0.5) * 3;
      stepMove(w, { dx: Math.cos(w.dir), dy: Math.sin(w.dir) }, 1 / 20,
        { speed: 260, doorSegs: t % 300 < 80 ? segs : [] });
      assert.ok(pointInAnyRect(w.x, w.y, RECTS), 'walker left the ship');
    }
  }
});

test('walls: a closed door blocks line of sight through its doorway', () => {
  const door = DOORS.find((d) => d.id === 'd_cafeW');
  const a = { x: door.x1 - 60, y: (door.y1 + door.y2) / 2 };
  const b = { x: door.x1 + 60, y: (door.y1 + door.y2) / 2 };
  assert.ok(lineOfSight(a.x, a.y, b.x, b.y, WALLS), 'doorway should be open');
  assert.ok(!lineOfSight(a.x, a.y, b.x, b.y, [...WALLS, door]), 'closed door should block');
});

test('settings: sanitiser clamps hostile input', () => {
  const s = sanitizeSettings({ impostors: 99, playerSpeed: 1e9, killCooldown: -5, taskBarUpdates: 'lol' }, 10);
  assert.ok(s.impostors <= 3 && s.impostors >= 1);
  assert.ok(s.playerSpeed <= 3);
  assert.ok(s.killCooldown >= 5);
  assert.equal(s.taskBarUpdates, 'always');
});

test('tasks: assignment honours the configured counts and is per-player', () => {
  const settings = { ...DEFAULT_SETTINGS, commonTasks: 1, longTasks: 2, shortTasks: 3 };
  const lists = assignTasks(['a', 'b', 'c'], settings);
  for (const id of ['a', 'b', 'c']) {
    const tasks = lists.get(id);
    assert.equal(tasks.filter((t) => t.category === 'common').length, 1);
    assert.equal(tasks.filter((t) => t.category === 'long').length, 2);
    assert.equal(tasks.filter((t) => t.category === 'short').length, 3);
    assert.equal(new Set(tasks.map((t) => t.id)).size, tasks.length, 'no duplicate tasks');
  }
  // The common task must be identical for everyone.
  const commonOf = (id) => lists.get(id).find((t) => t.category === 'common').id;
  assert.equal(commonOf('a'), commonOf('b'));
  const p = taskProgress([...lists.values()]);
  assert.equal(p.done, 0);
  assert.ok(p.total > 0);
});

test('lobby: colours stay unique and the host migrates on leave', () => {
  const room = new GameRoom('AAAAAA', null);
  const conn = { open: true, sendJSON() {} };
  const a = room.addPlayer({ name: 'A', color: 'red', conn }).player;
  const b = room.addPlayer({ name: 'B', color: 'red', conn }).player;
  assert.notEqual(a.color, b.color, 'duplicate colour handed out');
  assert.equal(room.hostId, a.id);
  room.setColor(b, a.color);
  assert.notEqual(a.color, b.color, 'colour steal allowed');
  room.removePlayer(a.id);
  assert.equal(room.hostId, b.id, 'host did not migrate');
});

test('rules: crew cannot kill, impostors cannot kill through walls', () => {
  const room = new GameRoom('BBBBBB', null);
  const conn = { open: true, sendJSON() {} };
  const ids = ['a', 'b', 'c', 'd'].map((n) => room.addPlayer({ name: n, color: null, conn }).player);
  room.settings.impostors = 1;
  room.startGame(room.hostId);
  const imp = room.playerList.find((p) => p.role === 'impostor');
  const crew = room.playerList.find((p) => p.role === 'crew');

  // Crewmate tries to kill: refused.
  crew.killCooldown = 0;
  imp.x = crew.x + 10; imp.y = crew.y;
  room.tryKill(crew, imp.id);
  assert.ok(imp.alive, 'crewmate managed a kill');

  // Impostor across the map: out of range.
  imp.killCooldown = 0;
  imp.x = 270; imp.y = 620;      // reactor
  crew.x = 2560; crew.y = 470;   // navigation
  room.tryKill(imp, crew.id);
  assert.ok(crew.alive, 'kill landed across the ship');

  // Point blank: allowed, and leaves a body.
  imp.x = crew.x + 20; imp.y = crew.y;
  room.tryKill(imp, crew.id);
  assert.ok(!crew.alive, 'point blank kill failed');
  assert.equal(room.bodies.length, 1);
  assert.ok(imp.killCooldown > 0, 'kill cooldown not applied');
});

test('rules: task steps only count near the console and only for crew', () => {
  const room = new GameRoom('CCCCCC', null);
  const conn = { open: true, sendJSON() {} };
  for (const n of ['a', 'b', 'c', 'd', 'e']) room.addPlayer({ name: n, color: null, conn });
  room.startGame(room.hostId);
  const crew = room.playerList.find((p) => p.role === 'crew');
  const task = crew.tasks[0];
  const step = task.steps[0];

  crew.x = 100; crew.y = 100;   // nowhere near
  room.completeTaskStep(crew, task.uid);
  assert.ok(!task.steps[0].done, 'remote task completion allowed');

  crew.x = step.x; crew.y = step.y;
  room.completeTaskStep(crew, task.uid);
  assert.ok(task.steps[0].done, 'nearby task completion refused');

  const imp = room.playerList.find((p) => p.role === 'impostor');
  const before = room.taskBar().done;
  const impTask = imp.tasks[0];
  imp.x = impTask.steps[0].x; imp.y = impTask.steps[0].y;
  room.completeTaskStep(imp, impTask.uid);
  assert.equal(room.taskBar().done, before, 'impostor task counted towards the bar');
});

test('rules: voting ejects the majority target and ties skip', () => {
  const mk = () => {
    const room = new GameRoom('DDDDDD', null);
    const conn = { open: true, sendJSON() {} };
    for (const n of ['a', 'b', 'c', 'd', 'e']) room.addPlayer({ name: n, color: null, conn });
    for (const p of room.playerList) p.brain = null;
    room.settings.impostors = 1;
    room.startGame(room.hostId);
    return room;
  };
  let room = mk();
  let [p1, p2, p3, p4, p5] = room.playerList;
  room.startMeeting('emergency', p1, null);
  room.meeting.phase = 'vote';
  for (const p of [p1, p2, p3]) room.castVote(p, p5.id);
  room.castVote(p4, 'skip');
  room.castVote(p5, 'skip');
  assert.ok(!p5.alive, 'majority target survived');

  room = mk();
  [p1, p2, p3, p4, p5] = room.playerList;
  room.startMeeting('emergency', p1, null);
  room.meeting.phase = 'vote';
  room.castVote(p1, p4.id);
  room.castVote(p2, p4.id);
  room.castVote(p3, p5.id);
  room.castVote(p4, p5.id);
  room.castVote(p5, 'skip');
  assert.ok(p4.alive && p5.alive, 'tie should eject nobody');
});

test('rules: ghosts cannot vote or report, and the living cannot see them', () => {
  const room = new GameRoom('EEEEEE', null);
  const conn = { open: true, sendJSON() {} };
  for (const n of ['a', 'b', 'c', 'd', 'e']) room.addPlayer({ name: n, color: null, conn });
  for (const p of room.playerList) p.brain = null;
  room.startGame(room.hostId);
  const [a, b] = room.playerList;
  a.alive = false;
  b.x = a.x; b.y = a.y;
  assert.equal(room.canSee(b, a), false, 'living player can see a ghost');
  assert.equal(room.canSee(a, b), true, 'ghost cannot see the living');
  room.startMeeting('emergency', b, null);
  room.meeting.phase = 'vote';
  room.castVote(a, b.id);
  assert.equal(a.vote, undefined, 'ghost vote accepted');
});

test('sabotage: critical sabotage runs down and needs both pads', () => {
  const room = new GameRoom('FFFFFF', null);
  const conn = { open: true, sendJSON() {} };
  for (const n of ['a', 'b', 'c', 'd', 'e']) room.addPlayer({ name: n, color: null, conn });
  for (const p of room.playerList) p.brain = null;
  room.settings.impostors = 1;
  room.startGame(room.hostId);
  const imp = room.playerList.find((p) => p.role === 'impostor');
  const crew = room.playerList.filter((p) => p.role === 'crew');
  room.sabotageCooldown = 0;
  room.trySabotage(imp, 'reactor');
  assert.ok(room.sabotage && room.sabotage.critical);

  const pads = FIX_POINTS.reactor;
  crew[0].x = pads[0].x; crew[0].y = pads[0].y;
  room.tryFix(crew[0], 'reactor', { pad: pads[0].id });
  assert.ok(room.sabotage, 'one pad should not be enough');
  crew[1].x = pads[1].x; crew[1].y = pads[1].y;
  room.tryFix(crew[1], 'reactor', { pad: pads[1].id });
  assert.equal(room.sabotage, null, 'both pads should fix the reactor');
});

test('sabotage: an unfixed reactor ends the game for the crew', () => {
  const room = new GameRoom('GGGGGG', null);
  const conn = { open: true, sendJSON() {} };
  for (const n of ['a', 'b', 'c', 'd', 'e']) room.addPlayer({ name: n, color: null, conn });
  for (const p of room.playerList) p.brain = null;
  room.settings.impostors = 1;
  room.startGame(room.hostId);
  const imp = room.playerList.find((p) => p.role === 'impostor');
  room.sabotageCooldown = 0;
  room.trySabotage(imp, 'reactor');
  for (let i = 0; i < 20 * 60 && room.phase !== PHASE.ENDED; i++) room.tick(1 / 20);
  assert.equal(room.phase, PHASE.ENDED);
  assert.equal(room.winner, 'impostor');
});

test('sabotage: only impostors may sabotage or vent', () => {
  const room = new GameRoom('HHHHHH', null);
  const conn = { open: true, sendJSON() {} };
  for (const n of ['a', 'b', 'c', 'd', 'e']) room.addPlayer({ name: n, color: null, conn });
  for (const p of room.playerList) p.brain = null;
  room.startGame(room.hostId);
  const crew = room.playerList.find((p) => p.role === 'crew');
  room.sabotageCooldown = 0;
  room.trySabotage(crew, 'lights');
  assert.equal(room.sabotage, null, 'crewmate sabotaged');
  const vent = VENTS[0];
  crew.x = vent.x; crew.y = vent.y;
  room.tryVent(crew, 'enter');
  assert.equal(crew.inVent, null, 'crewmate vented');
});

test('simulation: a full bot round finishes with a winner', () => {
  const r = simulate({ players: 8, impostors: 2, maxSeconds: 900 });
  assert.equal(r.escapes, 0, 'a player escaped the ship during the round');
  assert.ok(r.end, `round did not finish in ${r.seconds.toFixed(0)}s`);
  assert.ok(['crew', 'impostor'].includes(r.end.winner));
  assert.ok(r.snapshots.length > 100, 'no snapshots were produced');
});

test('simulation: ten rounds all terminate legally', () => {
  const winners = { crew: 0, impostor: 0 };
  for (let i = 0; i < 10; i++) {
    const r = simulate({ players: 5 + (i % 6), impostors: 1 + (i % 2), maxSeconds: 900 });
    assert.ok(r.end, `round ${i} never ended`);
    assert.equal(r.escapes, 0, `round ${i}: player left the ship`);
    winners[r.end.winner]++;
  }
  assert.ok(winners.crew + winners.impostor === 10);
  console.log(`      (crew ${winners.crew} / impostor ${winners.impostor})`);
});

// ---------------------------------------------------------------------------
console.log('');
for (const [name, fn] of tests) {
  try {
    fn();
    console.log(`  \x1b[32mPASS\x1b[0m ${name}`);
    passed++;
  } catch (err) {
    console.log(`  \x1b[31mFAIL\x1b[0m ${name}`);
    console.log(`       ${err.message.split('\n')[0]}`);
    if (process.env.VERBOSE) console.log(err.stack);
    failed++;
  }
}
console.log(`\n  ${passed} passed, ${failed} failed\n`);
process.exit(failed ? 1 : 0);
