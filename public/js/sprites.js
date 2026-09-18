// public/js/sprites.js
// Every character in the game is drawn procedurally - no image assets exist in
// this repo. One bean, a few poses.

/** Draw the classic bean body. Facing +x; callers flip with ctx.scale. */
function beanPath(ctx, w, h) {
  ctx.beginPath();
  ctx.moveTo(-w * 0.5, h * 0.5);
  ctx.bezierCurveTo(-w * 0.62, h * 0.02, -w * 0.5, -h * 0.5, -w * 0.05, -h * 0.5);
  ctx.bezierCurveTo(w * 0.42, -h * 0.5, w * 0.52, -h * 0.18, w * 0.5, h * 0.18);
  ctx.lineTo(w * 0.5, h * 0.5);
  ctx.closePath();
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

/**
 * @param {CanvasRenderingContext2D} ctx
 * @param {object} o  x, y, r (body half-width), color {hex, shadow}, dir,
 *                    walk (0..1 phase), ghost, dead, alpha, outline
 */
export function drawCrewmate(ctx, o) {
  const r = o.r ?? 22;
  const w = r * 2;
  const h = r * 2.45;
  const dir = o.dir ?? 1;
  const walk = o.walk ?? 0;
  const bob = o.ghost ? Math.sin(walk * Math.PI * 2) * r * 0.12 : 0;

  ctx.save();
  ctx.globalAlpha = o.alpha ?? (o.ghost ? 0.5 : 1);
  ctx.translate(o.x, o.y + bob);
  ctx.scale(dir >= 0 ? 1 : -1, 1);

  // ---- soft drop shadow on the floor
  if (!o.ghost) {
    ctx.save();
    ctx.globalAlpha *= 0.32;
    ctx.fillStyle = '#000';
    ctx.beginPath();
    ctx.ellipse(0, h * 0.54, w * 0.46, r * 0.26, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
  }

  // ---- legs (hidden for ghosts, which trail into a wisp instead)
  if (!o.ghost) {
    const swing = Math.sin(walk * Math.PI * 2) * r * 0.22;
    ctx.fillStyle = o.color.shadow;
    roundRect(ctx, -w * 0.34 + swing, h * 0.28, w * 0.3, h * 0.26, r * 0.22); ctx.fill();
    roundRect(ctx, w * 0.06 - swing, h * 0.28, w * 0.3, h * 0.26, r * 0.22); ctx.fill();
  }

  // ---- backpack
  ctx.fillStyle = o.color.shadow;
  roundRect(ctx, -w * 0.72, -h * 0.22, w * 0.26, h * 0.5, r * 0.3);
  ctx.fill();

  // ---- body
  if (o.ghost) {
    ctx.save();
    beanPath(ctx, w, h);
    ctx.clip();
    const g = ctx.createLinearGradient(0, -h * 0.5, 0, h * 0.5);
    g.addColorStop(0, o.color.hex);
    g.addColorStop(1, 'rgba(255,255,255,0.05)');
    ctx.fillStyle = g;
    ctx.fillRect(-w, -h, w * 2, h * 2);
    ctx.restore();
    // wispy tail
    ctx.fillStyle = o.color.hex;
    ctx.globalAlpha *= 0.55;
    ctx.beginPath();
    for (let i = 0; i <= 5; i++) {
      const t = i / 5;
      const x = -w * 0.5 + w * t;
      const y = h * 0.42 + Math.sin(walk * Math.PI * 2 + i) * r * 0.16;
      i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
    }
    ctx.lineTo(w * 0.5, h * 0.2);
    ctx.lineTo(-w * 0.5, h * 0.2);
    ctx.closePath();
    ctx.fill();
    ctx.globalAlpha = o.alpha ?? 0.5;
  } else {
    beanPath(ctx, w, h);
    const g = ctx.createLinearGradient(-w * 0.5, 0, w * 0.5, 0);
    g.addColorStop(0, o.color.shadow);
    g.addColorStop(0.35, o.color.hex);
    g.addColorStop(1, o.color.hex);
    ctx.fillStyle = g;
    ctx.fill();
    ctx.lineWidth = Math.max(2, r * 0.12);
    ctx.strokeStyle = o.outline || 'rgba(0,0,0,0.55)';
    ctx.stroke();
    // belly highlight
    ctx.save();
    beanPath(ctx, w, h);
    ctx.clip();
    ctx.globalAlpha *= 0.22;
    ctx.fillStyle = '#fff';
    ctx.beginPath();
    ctx.ellipse(-w * 0.05, -h * 0.05, w * 0.3, h * 0.34, -0.3, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
  }

  // ---- visor
  const vx = w * 0.12, vy = -h * 0.2, vrx = w * 0.34, vry = h * 0.16;
  ctx.beginPath();
  ctx.ellipse(vx, vy, vrx, vry, 0, 0, Math.PI * 2);
  const vg = ctx.createLinearGradient(vx - vrx, vy - vry, vx + vrx, vy + vry);
  vg.addColorStop(0, '#cdeefc');
  vg.addColorStop(0.5, '#8fc6e8');
  vg.addColorStop(1, '#5d92bb');
  ctx.fillStyle = vg;
  ctx.fill();
  ctx.lineWidth = Math.max(2, r * 0.1);
  ctx.strokeStyle = 'rgba(0,0,0,0.45)';
  ctx.stroke();
  ctx.save();
  ctx.globalAlpha *= 0.85;
  ctx.fillStyle = '#fff';
  ctx.beginPath();
  ctx.ellipse(vx - vrx * 0.35, vy - vry * 0.35, vrx * 0.26, vry * 0.3, -0.5, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();

  ctx.restore();
}

/** A body left behind: bean sliced in half, bone sticking out. */
export function drawCorpse(ctx, o) {
  const r = o.r ?? 20;
  const w = r * 2, h = r * 1.5;
  ctx.save();
  ctx.translate(o.x, o.y);
  ctx.globalAlpha = o.alpha ?? 1;

  // pooled shadow
  ctx.save();
  ctx.globalAlpha *= 0.4;
  ctx.fillStyle = '#000';
  ctx.beginPath();
  ctx.ellipse(0, h * 0.42, w * 0.62, r * 0.3, 0, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();

  // legs poking out
  ctx.fillStyle = o.color.shadow;
  roundRect(ctx, -w * 0.62, h * 0.06, w * 0.34, h * 0.3, r * 0.2); ctx.fill();
  roundRect(ctx, -w * 0.62, -h * 0.34, w * 0.34, h * 0.3, r * 0.2); ctx.fill();

  // squat body
  ctx.beginPath();
  ctx.moveTo(-w * 0.28, -h * 0.5);
  ctx.bezierCurveTo(w * 0.42, -h * 0.62, w * 0.6, h * 0.62, -w * 0.28, h * 0.5);
  ctx.closePath();
  const g = ctx.createLinearGradient(0, -h * 0.5, 0, h * 0.5);
  g.addColorStop(0, o.color.hex);
  g.addColorStop(1, o.color.shadow);
  ctx.fillStyle = g;
  ctx.fill();
  ctx.lineWidth = Math.max(2, r * 0.1);
  ctx.strokeStyle = 'rgba(0,0,0,0.5)';
  ctx.stroke();

  // bone
  ctx.fillStyle = '#f2f2ee';
  ctx.strokeStyle = 'rgba(0,0,0,0.4)';
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.roundRect?.(-w * 0.34, -r * 0.16, w * 0.34, r * 0.3, r * 0.15);
  if (!ctx.roundRect) roundRect(ctx, -w * 0.34, -r * 0.16, w * 0.34, r * 0.3, r * 0.15);
  ctx.fill(); ctx.stroke();
  ctx.beginPath();
  ctx.arc(-w * 0.34, -r * 0.18, r * 0.2, 0, Math.PI * 2);
  ctx.arc(-w * 0.34, r * 0.18, r * 0.2, 0, Math.PI * 2);
  ctx.fill(); ctx.stroke();

  ctx.restore();
}

/** Small bean icon used by the lobby / meeting lists. */
export function renderBeanTo(canvas, color, opts = {}) {
  const ctx = canvas.getContext('2d');
  const dpr = window.devicePixelRatio || 1;
  const w = canvas.width / dpr || canvas.width;
  const h = canvas.height / dpr || canvas.height;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  drawCrewmate(ctx, {
    x: w / 2, y: h / 2, r: Math.min(w, h) * 0.26,
    color, dir: 1, walk: 0, ghost: !!opts.ghost, alpha: opts.alpha,
  });
}

export { roundRect };
