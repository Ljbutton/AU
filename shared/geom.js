// shared/geom.js
// Small 2D geometry toolkit shared by the authoritative server and the browser
// client. Everything here is pure ESM with no platform dependencies so the same
// collision / line-of-sight maths runs on both sides (client prediction must
// agree with the server or players rubber-band).

export const EPS = 1e-9;

export function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
export function lerp(a, b, t) { return a + (b - a) * t; }

export function dist(ax, ay, bx, by) { return Math.hypot(ax - bx, ay - by); }
export function dist2(ax, ay, bx, by) { const dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }

/** Shortest angular difference, result in (-PI, PI]. */
export function angleDelta(a, b) {
  let d = (b - a) % (Math.PI * 2);
  if (d > Math.PI) d -= Math.PI * 2;
  if (d <= -Math.PI) d += Math.PI * 2;
  return d;
}

/** Rectangle helper. Rects are stored as {x1,y1,x2,y2} with x1<x2, y1<y2. */
export function rect(x1, y1, x2, y2, extra = {}) {
  return { x1: Math.min(x1, x2), y1: Math.min(y1, y2), x2: Math.max(x1, x2), y2: Math.max(y1, y2), ...extra };
}
export function rectCenter(r) { return { x: (r.x1 + r.x2) / 2, y: (r.y1 + r.y2) / 2 }; }
export function pointInRect(x, y, r, pad = 0) {
  return x >= r.x1 - pad && x <= r.x2 + pad && y >= r.y1 - pad && y <= r.y2 + pad;
}
export function rectsOverlap(a, b) {
  return a.x1 < b.x2 && b.x1 < a.x2 && a.y1 < b.y2 && b.y1 < a.y2;
}
/** Overlap rectangle of two rects, or null. */
export function rectIntersection(a, b) {
  const x1 = Math.max(a.x1, b.x1), y1 = Math.max(a.y1, b.y1);
  const x2 = Math.min(a.x2, b.x2), y2 = Math.min(a.y2, b.y2);
  if (x2 <= x1 || y2 <= y1) return null;
  return { x1, y1, x2, y2 };
}

/** True when the point sits inside any rect of the walkable union. */
export function pointInAnyRect(x, y, rects, pad = 0) {
  for (let i = 0; i < rects.length; i++) if (pointInRect(x, y, rects[i], pad)) return true;
  return false;
}
export function findRectAt(x, y, rects, pad = 0) {
  for (let i = 0; i < rects.length; i++) if (pointInRect(x, y, rects[i], pad)) return rects[i];
  return null;
}

// ---------------------------------------------------------------------------
// Segments
// ---------------------------------------------------------------------------

export function segment(x1, y1, x2, y2, extra = {}) { return { x1, y1, x2, y2, ...extra }; }

export function closestPointOnSegment(px, py, s) {
  const dx = s.x2 - s.x1, dy = s.y2 - s.y1;
  const len2 = dx * dx + dy * dy;
  if (len2 < EPS) return { x: s.x1, y: s.y1, t: 0 };
  let t = ((px - s.x1) * dx + (py - s.y1) * dy) / len2;
  t = clamp(t, 0, 1);
  return { x: s.x1 + dx * t, y: s.y1 + dy * t, t };
}

export function distToSegment(px, py, s) {
  const p = closestPointOnSegment(px, py, s);
  return Math.hypot(px - p.x, py - p.y);
}

/** Do two segments properly intersect? */
export function segmentsIntersect(a, b) {
  const d1x = a.x2 - a.x1, d1y = a.y2 - a.y1;
  const d2x = b.x2 - b.x1, d2y = b.y2 - b.y1;
  const denom = d1x * d2y - d1y * d2x;
  if (Math.abs(denom) < EPS) return false;
  const t = ((b.x1 - a.x1) * d2y - (b.y1 - a.y1) * d2x) / denom;
  const u = ((b.x1 - a.x1) * d1y - (b.y1 - a.y1) * d1x) / denom;
  return t >= 0 && t <= 1 && u >= 0 && u <= 1;
}

/**
 * Cast a ray from (ox,oy) along the unit-ish direction (dx,dy).
 * Returns the smallest positive hit distance, or maxT when nothing is hit.
 */
export function raycast(ox, oy, dx, dy, segs, maxT = Infinity) {
  let best = maxT;
  for (let i = 0; i < segs.length; i++) {
    const s = segs[i];
    const rx = s.x2 - s.x1, ry = s.y2 - s.y1;
    const denom = dx * ry - dy * rx;
    if (Math.abs(denom) < EPS) continue;
    const ox1 = s.x1 - ox, oy1 = s.y1 - oy;
    const t = (ox1 * ry - oy1 * rx) / denom;
    if (t < 0 || t >= best) continue;
    const u = (ox1 * dy - oy1 * dx) / denom;
    if (u < 0 || u > 1) continue;
    best = t;
  }
  return best;
}

/** Unobstructed sight line between two points? */
export function lineOfSight(ax, ay, bx, by, segs) {
  const probe = { x1: ax, y1: ay, x2: bx, y2: by };
  for (let i = 0; i < segs.length; i++) if (segmentsIntersect(probe, segs[i])) return false;
  return true;
}

/**
 * Visibility polygon around an origin, clipped to `radius`.
 * Rays are shot at every segment endpoint (plus a small angular nudge either
 * side so we wrap around corners) and at a regular angular cadence so the
 * clipped circle stays smooth.
 */
export function visibilityPolygon(ox, oy, radius, segs, angularSteps = 72) {
  const angles = [];
  const nudge = 0.0006;
  for (let i = 0; i < segs.length; i++) {
    const s = segs[i];
    for (const [px, py] of [[s.x1, s.y1], [s.x2, s.y2]]) {
      const a = Math.atan2(py - oy, px - ox);
      angles.push(a - nudge, a, a + nudge);
    }
  }
  for (let i = 0; i < angularSteps; i++) angles.push((i / angularSteps) * Math.PI * 2 - Math.PI);
  angles.sort((a, b) => a - b);

  const poly = [];
  let last = -Infinity;
  for (let i = 0; i < angles.length; i++) {
    const a = angles[i];
    if (a - last < 1e-7) continue;
    last = a;
    const dx = Math.cos(a), dy = Math.sin(a);
    const t = raycast(ox, oy, dx, dy, segs, radius);
    poly.push({ x: ox + dx * t, y: oy + dy * t });
  }
  return poly;
}

// ---------------------------------------------------------------------------
// Circle vs. wall resolution
// ---------------------------------------------------------------------------

/**
 * Push a circle out of every wall segment it overlaps. Walls are the boundary
 * of the walkable union, so "push away from the segment" always means "push
 * back into the walkable area" for a circle that started inside.
 */
export function resolveCircle(x, y, r, segs, iterations = 4) {
  for (let it = 0; it < iterations; it++) {
    let moved = false;
    for (let i = 0; i < segs.length; i++) {
      const s = segs[i];
      const p = closestPointOnSegment(x, y, s);
      let dx = x - p.x, dy = y - p.y;
      let d = Math.hypot(dx, dy);
      if (d >= r) continue;
      if (d < 1e-6) {
        // Degenerate: sitting exactly on the wall. Push along the normal.
        dx = -(s.y2 - s.y1); dy = s.x2 - s.x1;
        d = Math.hypot(dx, dy) || 1;
      }
      const push = (r - d) / d;
      x += dx * push;
      y += dy * push;
      moved = true;
    }
    if (!moved) break;
  }
  return { x, y };
}

// ---------------------------------------------------------------------------
// Spatial hash for wall segments (keeps per-tick collision cheap)
// ---------------------------------------------------------------------------

export class SegmentGrid {
  constructor(segs, cell = 200) {
    this.cell = cell;
    this.map = new Map();
    this.segs = segs;
    for (const s of segs) this.insert(s);
  }
  key(cx, cy) { return cx + ',' + cy; }
  insert(s) {
    const c = this.cell;
    const cx1 = Math.floor(Math.min(s.x1, s.x2) / c), cx2 = Math.floor(Math.max(s.x1, s.x2) / c);
    const cy1 = Math.floor(Math.min(s.y1, s.y2) / c), cy2 = Math.floor(Math.max(s.y1, s.y2) / c);
    for (let cx = cx1; cx <= cx2; cx++) {
      for (let cy = cy1; cy <= cy2; cy++) {
        const k = this.key(cx, cy);
        let list = this.map.get(k);
        if (!list) this.map.set(k, (list = []));
        list.push(s);
      }
    }
  }
  /** All segments whose cells intersect the box around (x,y) of half-size r. */
  query(x, y, r, out = []) {
    out.length = 0;
    const c = this.cell;
    const cx1 = Math.floor((x - r) / c), cx2 = Math.floor((x + r) / c);
    const cy1 = Math.floor((y - r) / c), cy2 = Math.floor((y + r) / c);
    const seen = new Set();
    for (let cx = cx1; cx <= cx2; cx++) {
      for (let cy = cy1; cy <= cy2; cy++) {
        const list = this.map.get(this.key(cx, cy));
        if (!list) continue;
        for (const s of list) {
          if (seen.has(s)) continue;
          seen.add(s);
          out.push(s);
        }
      }
    }
    return out;
  }
}

/** Segments whose bounding box is within `r` of a point (no grid needed). */
export function segmentsNear(x, y, r, segs, out = []) {
  out.length = 0;
  for (let i = 0; i < segs.length; i++) {
    const s = segs[i];
    const minX = Math.min(s.x1, s.x2) - r, maxX = Math.max(s.x1, s.x2) + r;
    const minY = Math.min(s.y1, s.y2) - r, maxY = Math.max(s.y1, s.y2) + r;
    if (x < minX || x > maxX || y < minY || y > maxY) continue;
    out.push(s);
  }
  return out;
}
