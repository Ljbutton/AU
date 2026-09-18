// shared/movement.js
// The single movement implementation used by BOTH the authoritative server and
// the client's local prediction. Keeping one copy is what stops players from
// rubber-banding.

import { PLAYER_RADIUS, PLAYER_BASE_SPEED, GHOST_SPEED_MULT } from './constants.js';
import { RECTS, WALLS, WORLD, DOOR_BY_ID } from './map.js';
import { SegmentGrid, resolveCircle, pointInAnyRect, clamp } from './geom.js';

const GRID = new SegmentGrid(WALLS, 200);
const scratch = [];

/** Segment list for a set of currently closed door ids. */
export function doorSegments(closedDoorIds) {
  if (!closedDoorIds || closedDoorIds.length === 0) return [];
  const out = [];
  for (const id of closedDoorIds) {
    const d = DOOR_BY_ID.get(id);
    if (d) out.push({ x1: d.x1, y1: d.y1, x2: d.x2, y2: d.y2 });
  }
  return out;
}

/** Speed in px/s for a player given the lobby settings. */
export function speedFor(player, settings) {
  const mult = settings ? settings.playerSpeed : 1;
  return PLAYER_BASE_SPEED * mult * (player.alive ? 1 : GHOST_SPEED_MULT);
}

/**
 * Advance one entity by dt seconds.
 * `input` is a (not necessarily normalised) direction; length is clamped to 1.
 * Ghosts phase through walls; everyone is clamped to the world bounds.
 */
export function stepMove(ent, input, dt, opts = {}) {
  const speed = opts.speed ?? PLAYER_BASE_SPEED;
  let dx = input.dx || 0, dy = input.dy || 0;
  const len = Math.hypot(dx, dy);
  if (len > 1e-4) {
    if (len > 1) { dx /= len; dy /= len; }
  } else {
    return false;
  }

  const nx = ent.x + dx * speed * dt;
  const ny = ent.y + dy * speed * dt;

  if (opts.noclip) {
    ent.x = clamp(nx, 8, WORLD.w - 8);
    ent.y = clamp(ny, 8, WORLD.h - 8);
    return true;
  }

  const doors = opts.doorSegs || [];
  const tryMove = (tx, ty) => {
    GRID.query(tx, ty, PLAYER_RADIUS + 8, scratch);
    const segs = doors.length ? scratch.concat(doors) : scratch;
    const res = resolveCircle(tx, ty, PLAYER_RADIUS, segs);
    return pointInAnyRect(res.x, res.y, RECTS) ? res : null;
  };

  let res = tryMove(nx, ny);
  if (!res) res = tryMove(nx, ent.y);   // slide horizontally
  if (!res) res = tryMove(ent.x, ny);   // slide vertically
  if (!res) return false;

  ent.x = res.x;
  ent.y = res.y;
  return true;
}

/** Nudge an entity to the nearest legal spot (used after teleports/spawns). */
export function settle(ent) {
  if (pointInAnyRect(ent.x, ent.y, RECTS)) {
    GRID.query(ent.x, ent.y, PLAYER_RADIUS + 8, scratch);
    const res = resolveCircle(ent.x, ent.y, PLAYER_RADIUS, scratch);
    if (pointInAnyRect(res.x, res.y, RECTS)) { ent.x = res.x; ent.y = res.y; return true; }
    return true;
  }
  // Fall back to the centre of the closest rect.
  let best = null, bestD = Infinity;
  for (const r of RECTS) {
    const cx = clamp(ent.x, r.x1 + PLAYER_RADIUS, r.x2 - PLAYER_RADIUS);
    const cy = clamp(ent.y, r.y1 + PLAYER_RADIUS, r.y2 - PLAYER_RADIUS);
    const d = Math.hypot(cx - ent.x, cy - ent.y);
    if (d < bestD) { bestD = d; best = { x: cx, y: cy }; }
  }
  if (best) { ent.x = best.x; ent.y = best.y; }
  return false;
}

export { GRID as WALL_GRID };
