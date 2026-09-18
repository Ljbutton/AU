// shared/map.js
// "The Hull" - an original Skeld-inspired ship map.
//
// The walkable area is a union of axis-aligned rectangles (rooms + corridors).
// Wall segments are *derived* from that union: for every rectangle edge we
// subtract the stretches that are interior to the union (i.e. covered by a
// neighbouring rectangle that straddles the edge). What survives is the outline
// of the ship, which gives us collision, line-of-sight and rendering all from
// one source of truth. Corridors therefore always overlap the rooms they join
// by >= 15px, and the size of that overlap is the width of the doorway.

import { rect, rectIntersection, rectCenter } from './geom.js';

export const WORLD = { w: 2800, h: 1300 };

const R = (x1, y1, x2, y2, extra) => rect(x1, y1, x2, y2, extra);

/** Named rooms. `label` is drawn on the floor; `kind` drives decoration. */
export const ROOMS = [
  { id: 'reactor',     name: 'Reactor',       kind: 'reactor',   ...R(120, 430, 420, 810) },
  { id: 'upperEngine', name: 'Upper Engine',  kind: 'engine',    ...R(520, 190, 820, 440) },
  { id: 'lowerEngine', name: 'Lower Engine',  kind: 'engine',    ...R(520, 820, 820, 1070) },
  { id: 'security',    name: 'Security',      kind: 'security',  ...R(570, 540, 780, 730) },
  { id: 'medbay',      name: 'MedBay',        kind: 'medbay',    ...R(900, 210, 1200, 450) },
  { id: 'electrical',  name: 'Electrical',    kind: 'electrical',...R(900, 800, 1230, 1080) },
  { id: 'cafeteria',   name: 'Cafeteria',     kind: 'cafeteria', ...R(1350, 150, 1810, 570) },
  { id: 'storage',     name: 'Storage',       kind: 'storage',   ...R(1330, 790, 1700, 1170) },
  { id: 'admin',       name: 'Admin',         kind: 'admin',     ...R(1800, 660, 2090, 870) },
  { id: 'weapons',     name: 'Weapons',       kind: 'weapons',   ...R(1930, 150, 2220, 390) },
  { id: 'o2',          name: 'O2',            kind: 'o2',        ...R(1900, 450, 2110, 610) },
  { id: 'navigation',  name: 'Navigation',    kind: 'nav',       ...R(2440, 350, 2680, 610) },
  { id: 'shields',     name: 'Shields',       kind: 'shields',   ...R(2170, 920, 2450, 1160) },
  { id: 'comms',       name: 'Communications', kind: 'comms',    ...R(1790, 990, 2050, 1180) },
];

/** Corridors. Each must overlap the rects it connects by >= 15px. */
export const HALLS = [
  { id: 'h_reactorTop',   ...R(408, 470, 528, 560) },   // reactor -> left hall
  { id: 'h_reactorBot',   ...R(408, 680, 528, 770) },
  { id: 'h_leftV',        ...R(430, 300, 515, 980) },   // long west corridor
  { id: 'h_upEngine',     ...R(500, 330, 620, 420) },   // left hall -> upper engine
  { id: 'h_lowEngine',    ...R(500, 850, 620, 940) },   // left hall -> lower engine
  { id: 'h_security',     ...R(495, 590, 590, 680) },   // left hall -> security
  { id: 'h_upEngineS',    ...R(690, 420, 800, 510) },   // upper engine -> spine
  { id: 'h_spine',        ...R(780, 470, 1380, 560) },  // central west-east spine
  { id: 'h_medbay',       ...R(1000, 430, 1090, 505) }, // spine -> medbay
  { id: 'h_lowEngineE',   ...R(805, 900, 915, 990) },   // lower engine -> electrical
  { id: 'h_elecStorage',  ...R(1215, 900, 1345, 990) }, // electrical -> storage
  { id: 'h_midV',         ...R(1600, 555, 1690, 880) }, // cafeteria -> storage
  { id: 'h_admin',        ...R(1675, 700, 1815, 790) }, // mid hall -> admin
  { id: 'h_weapons',      ...R(1795, 200, 1945, 290) }, // cafeteria -> weapons
  { id: 'h_o2',           ...R(1795, 470, 1915, 560) }, // cafeteria -> o2
  { id: 'h_weaponsE',     ...R(2205, 250, 2320, 340) }, // weapons -> east corridor
  { id: 'h_rightV',       ...R(2300, 250, 2390, 1040) },// long east corridor
  { id: 'h_o2E',          ...R(2095, 480, 2315, 570) }, // o2 -> east corridor
  { id: 'h_nav',          ...R(2375, 420, 2455, 510) }, // east corridor -> navigation
  { id: 'h_storComms',    ...R(1685, 1040, 1805, 1130) }, // storage -> comms
  { id: 'h_commsShields', ...R(2035, 1050, 2185, 1140) }, // comms -> shields
];

/** Every walkable rectangle. */
export const RECTS = [...ROOMS, ...HALLS];

/**
 * Doors that the Impostor can slam shut. Each segment lies exactly across a
 * doorway created by a hall/room overlap.
 */
export const DOORS = [
  { id: 'd_upEngineW',  room: 'upperEngine', ...R(520, 330, 520, 420) },
  { id: 'd_upEngineS',  room: 'upperEngine', ...R(690, 440, 800, 440) },
  { id: 'd_lowEngineW', room: 'lowerEngine', ...R(520, 850, 520, 940) },
  { id: 'd_lowEngineE', room: 'lowerEngine', ...R(820, 900, 820, 990) },
  { id: 'd_security',   room: 'security',    ...R(570, 590, 570, 680) },
  { id: 'd_medbay',     room: 'medbay',      ...R(1000, 450, 1090, 450) },
  { id: 'd_elecW',      room: 'electrical',  ...R(900, 900, 900, 990) },
  { id: 'd_elecE',      room: 'electrical',  ...R(1230, 900, 1230, 990) },
  { id: 'd_cafeW',      room: 'cafeteria',   ...R(1350, 470, 1350, 560) },
  { id: 'd_cafeNE',     room: 'cafeteria',   ...R(1810, 200, 1810, 290) },
  { id: 'd_cafeSE',     room: 'cafeteria',   ...R(1810, 470, 1810, 560) },
  { id: 'd_cafeS',      room: 'cafeteria',   ...R(1600, 570, 1690, 570) },
  { id: 'd_storN',      room: 'storage',     ...R(1600, 790, 1690, 790) },
  { id: 'd_storW',      room: 'storage',     ...R(1330, 900, 1330, 990) },
  { id: 'd_storE',      room: 'storage',     ...R(1700, 1040, 1700, 1130) },
  { id: 'd_admin',      room: 'admin',       ...R(1800, 700, 1800, 790) },
  { id: 'd_weaponsW',   room: 'weapons',     ...R(1930, 200, 1930, 290) },
  { id: 'd_weaponsE',   room: 'weapons',     ...R(2220, 250, 2220, 340) },
  { id: 'd_o2W',        room: 'o2',          ...R(1900, 470, 1900, 560) },
  { id: 'd_o2E',        room: 'o2',          ...R(2110, 480, 2110, 570) },
  { id: 'd_nav',        room: 'navigation',  ...R(2440, 420, 2440, 510) },
  { id: 'd_shieldsN',   room: 'shields',     ...R(2300, 920, 2390, 920) },
  { id: 'd_shieldsW',   room: 'shields',     ...R(2170, 1050, 2170, 1140) },
  { id: 'd_commsW',     room: 'comms',       ...R(1790, 1040, 1790, 1130) },
  { id: 'd_commsE',     room: 'comms',       ...R(2050, 1050, 2050, 1140) },
];

/** Rooms whose doors the Impostor may close (each entry is a sabotage target). */
export const DOOR_ROOMS = [...new Set(DOORS.map((d) => d.room))];

/** Vent network. `links` are bidirectional hops. */
export const VENTS = [
  { id: 'v_upperEngine', room: 'upperEngine', x: 560, y: 240, links: ['v_reactor'] },
  { id: 'v_reactor',     room: 'reactor',     x: 165, y: 470, links: ['v_upperEngine', 'v_lowerEngine'] },
  { id: 'v_lowerEngine', room: 'lowerEngine', x: 560, y: 1020, links: ['v_reactor'] },
  { id: 'v_medbay',      room: 'medbay',      x: 940, y: 410, links: ['v_electrical'] },
  { id: 'v_electrical',  room: 'electrical',  x: 950, y: 1035, links: ['v_medbay', 'v_security'] },
  { id: 'v_security',    room: 'security',    x: 740, y: 580, links: ['v_electrical'] },
  { id: 'v_weapons',     room: 'weapons',     x: 2175, y: 195, links: ['v_navigation'] },
  { id: 'v_navigation',  room: 'navigation',  x: 2635, y: 395, links: ['v_weapons', 'v_shields'] },
  { id: 'v_shields',     room: 'shields',     x: 2215, y: 1115, links: ['v_navigation'] },
  { id: 'v_cafeteria',   room: 'cafeteria',   x: 1395, y: 200, links: ['v_admin'] },
  { id: 'v_admin',       room: 'admin',       x: 2045, y: 825, links: ['v_cafeteria'] },
];

/** Where the emergency button lives (middle of the cafeteria table). */
export const EMERGENCY_BUTTON = { x: 1580, y: 360, r: 90 };

/** Admin map table + security cameras + spawn ring. */
export const ADMIN_TABLE = { x: 1945, y: 765, r: 80 };
export const SPAWN = { x: 1580, y: 360, r: 150 };

/** Console positions used by sabotage fixes. */
export const FIX_POINTS = {
  lights:      [{ id: 'lights', room: 'electrical', x: 1180, y: 840 }],
  comms:       [{ id: 'comms', room: 'comms', x: 1830, y: 1030 }],
  reactor:     [
    { id: 'reactorL', room: 'reactor', x: 170, y: 620 },
    { id: 'reactorR', room: 'reactor', x: 370, y: 620 },
  ],
  o2:          [
    { id: 'o2A', room: 'o2', x: 2070, y: 590 },
    { id: 'o2B', room: 'admin', x: 1840, y: 700 },
  ],
};

// ---------------------------------------------------------------------------
// Wall extraction
// ---------------------------------------------------------------------------

/**
 * Subtract `cuts` (a list of [from,to] intervals) from [start,end] and return
 * the surviving pieces.
 */
function subtractIntervals(start, end, cuts, minLen = 1) {
  if (!cuts.length) return [[start, end]];
  cuts.sort((a, b) => a[0] - b[0]);
  const out = [];
  let cursor = start;
  for (const [a, b] of cuts) {
    if (b <= cursor) continue;
    if (a > cursor) out.push([cursor, Math.min(a, end)]);
    cursor = Math.max(cursor, b);
    if (cursor >= end) break;
  }
  if (cursor < end) out.push([cursor, end]);
  return out.filter(([a, b]) => b - a > minLen);
}

/**
 * Derive the outline of the union of rects.
 * `eps` is how far a neighbour must straddle an edge before that stretch counts
 * as interior (guards against rects that merely touch).
 */
export function buildWalls(rects, eps = 1) {
  const segs = [];
  for (const a of rects) {
    // Horizontal edges: top (outside is above), bottom (outside is below).
    for (const [y, outsideUp] of [[a.y1, true], [a.y2, false]]) {
      const cuts = [];
      for (const b of rects) {
        if (b === a) continue;
        const straddles = outsideUp
          ? b.y1 <= y - eps && b.y2 >= y + eps
          : b.y1 <= y - eps && b.y2 >= y + eps;
        if (!straddles) continue;
        const x1 = Math.max(a.x1, b.x1), x2 = Math.min(a.x2, b.x2);
        if (x2 > x1) cuts.push([x1, x2]);
      }
      for (const [x1, x2] of subtractIntervals(a.x1, a.x2, cuts)) {
        segs.push({ x1, y1: y, x2, y2: y, horizontal: true });
      }
    }
    // Vertical edges.
    for (const x of [a.x1, a.x2]) {
      const cuts = [];
      for (const b of rects) {
        if (b === a) continue;
        if (!(b.x1 <= x - eps && b.x2 >= x + eps)) continue;
        const y1 = Math.max(a.y1, b.y1), y2 = Math.min(a.y2, b.y2);
        if (y2 > y1) cuts.push([y1, y2]);
      }
      for (const [y1, y2] of subtractIntervals(a.y1, a.y2, cuts)) {
        segs.push({ x1: x, y1, x2: x, y2, horizontal: false });
      }
    }
  }
  return dedupeSegments(segs);
}

function dedupeSegments(segs) {
  const seen = new Set();
  const out = [];
  for (const s of segs) {
    const key = `${Math.round(s.x1)},${Math.round(s.y1)},${Math.round(s.x2)},${Math.round(s.y2)}`;
    const rev = `${Math.round(s.x2)},${Math.round(s.y2)},${Math.round(s.x1)},${Math.round(s.y1)}`;
    if (seen.has(key) || seen.has(rev)) continue;
    seen.add(key);
    out.push(s);
  }
  return out;
}

export const WALLS = buildWalls(RECTS);

// ---------------------------------------------------------------------------
// Navigation graph (used by bots and by the "distance between rooms" helpers)
// ---------------------------------------------------------------------------

/**
 * Nodes are walkable rects; two rects are neighbours when they overlap, and the
 * transit point is the centre of the overlap. Good enough for bot pathing and
 * dirt cheap to build.
 */
export function buildNavGraph(rects) {
  const nodes = rects.map((r, i) => ({ i, id: r.id, rect: r, center: rectCenter(r), edges: [] }));
  for (let i = 0; i < rects.length; i++) {
    for (let j = i + 1; j < rects.length; j++) {
      const inter = rectIntersection(rects[i], rects[j]);
      if (!inter) continue;
      const gate = rectCenter(inter);
      nodes[i].edges.push({ to: j, gate });
      nodes[j].edges.push({ to: i, gate });
    }
  }
  return nodes;
}

export const NAV = buildNavGraph(RECTS);
const NAV_BY_ID = new Map(NAV.map((n) => [n.id, n]));

export function navNodeAt(x, y) {
  // Prefer rooms over halls when a point is inside both.
  let hall = null;
  for (const n of NAV) {
    const r = n.rect;
    if (x >= r.x1 && x <= r.x2 && y >= r.y1 && y <= r.y2) {
      if (ROOM_IDS.has(n.id)) return n;
      if (!hall) hall = n;
    }
  }
  return hall;
}

export const ROOM_IDS = new Set(ROOMS.map((r) => r.id));
export const ROOM_BY_ID = new Map(ROOMS.map((r) => [r.id, r]));
export const VENT_BY_ID = new Map(VENTS.map((v) => [v.id, v]));
export const DOOR_BY_ID = new Map(DOORS.map((d) => [d.id, d]));

/**
 * Breadth-first waypoint path between two world points.
 *
 * Waypoints alternate gate -> rect centre -> gate -> ... -> target. Because
 * every rect is convex and each consecutive pair of waypoints lies inside the
 * same rect, a walker that steers straight at the next waypoint can never cut
 * a corner into a wall.
 */
export function findPath(sx, sy, tx, ty) {
  const start = navNodeAt(sx, sy);
  const goal = navNodeAt(tx, ty);
  if (!start || !goal) return null;
  if (start === goal) return [{ x: tx, y: ty }];

  const prev = new Map();
  const queue = [start.i];
  const seen = new Set([start.i]);
  let found = false;
  while (queue.length) {
    const cur = queue.shift();
    if (cur === goal.i) { found = true; break; }
    for (const e of NAV[cur].edges) {
      if (seen.has(e.to)) continue;
      seen.add(e.to);
      prev.set(e.to, { from: cur, gate: e.gate });
      queue.push(e.to);
    }
  }
  if (!found && !seen.has(goal.i)) return null;

  // Walk the chain back to the start, then emit gate/centre pairs forward.
  const chain = [];
  let cur = goal.i;
  while (cur !== start.i) {
    const stepInfo = prev.get(cur);
    if (!stepInfo) return null;
    chain.unshift({ node: cur, gate: stepInfo.gate });
    cur = stepInfo.from;
  }
  const waypoints = [];
  for (let i = 0; i < chain.length; i++) {
    waypoints.push({ x: chain[i].gate.x, y: chain[i].gate.y });
    if (i < chain.length - 1) {
      const c = NAV[chain[i].node].center;
      waypoints.push({ x: c.x, y: c.y });
    }
  }
  waypoints.push({ x: tx, y: ty });
  return waypoints;
}

/** Which named room contains a point (or null when in a corridor). */
export function roomAt(x, y) {
  for (const r of ROOMS) if (x >= r.x1 && x <= r.x2 && y >= r.y1 && y <= r.y2) return r;
  return null;
}

export function roomName(x, y) {
  const r = roomAt(x, y);
  return r ? r.name : 'Hallway';
}

export { NAV_BY_ID };
