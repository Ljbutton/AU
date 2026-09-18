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
 * Cosmetic hats, drawn sitting on the dome of the bean (origin is the body
 * centre, facing +x, so the crown of the head is at roughly (-0.02w, -0.5h)).
 */
function drawHat(ctx, hat, w, h) {
  if (!hat || hat === 'none') return;
  const top = -h * 0.5;
  ctx.save();
  ctx.lineWidth = Math.max(2, w * 0.05);
  ctx.strokeStyle = 'rgba(0,0,0,0.5)';
  switch (hat) {
    case 'band':
      ctx.fillStyle = '#e8484f';
      roundRect(ctx, -w * 0.46, top - w * 0.02, w * 0.84, w * 0.16, w * 0.07);
      ctx.fill(); ctx.stroke();
      ctx.fillStyle = '#ffd65a';
      ctx.beginPath(); ctx.arc(w * 0.28, top + w * 0.06, w * 0.09, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
      break;
    case 'cap':
      ctx.fillStyle = '#2f7de0';
      ctx.beginPath();
      ctx.ellipse(-w * 0.02, top + w * 0.02, w * 0.44, w * 0.3, 0, Math.PI, Math.PI * 2);
      ctx.fill(); ctx.stroke();
      ctx.fillStyle = '#255fa8';
      roundRect(ctx, w * 0.32, top - w * 0.02, w * 0.38, w * 0.12, w * 0.05);
      ctx.fill(); ctx.stroke();
      break;
    case 'tophat':
      ctx.fillStyle = '#1b2029';
      roundRect(ctx, -w * 0.44, top - w * 0.06, w * 0.86, w * 0.12, w * 0.05);
      ctx.fill(); ctx.stroke();
      roundRect(ctx, -w * 0.28, top - w * 0.62, w * 0.56, w * 0.58, w * 0.06);
      ctx.fill(); ctx.stroke();
      ctx.fillStyle = '#c0392b';
      ctx.fillRect(-w * 0.28, top - w * 0.2, w * 0.56, w * 0.12);
      break;
    case 'crown': {
      ctx.fillStyle = '#ffd65a';
      ctx.beginPath();
      const bx = -w * 0.36, by = top + w * 0.04, cw = w * 0.72, ch = w * 0.34;
      ctx.moveTo(bx, by);
      ctx.lineTo(bx, by - ch * 0.5);
      for (let i = 0; i < 3; i++) {
        ctx.lineTo(bx + cw * (i + 0.5) / 3, by - ch);
        ctx.lineTo(bx + cw * (i + 1) / 3, by - ch * 0.5);
      }
      ctx.lineTo(bx + cw, by);
      ctx.closePath();
      ctx.fill(); ctx.stroke();
      break;
    }
    case 'horns':
      ctx.fillStyle = '#b8352f';
      for (const sx of [-1, 1]) {
        ctx.beginPath();
        ctx.moveTo(sx * w * 0.16, top + w * 0.06);
        ctx.quadraticCurveTo(sx * w * 0.42, top - w * 0.1, sx * w * 0.3, top - w * 0.4);
        ctx.quadraticCurveTo(sx * w * 0.2, top - w * 0.12, sx * w * 0.06, top + w * 0.06);
        ctx.closePath();
        ctx.fill(); ctx.stroke();
      }
      break;
    case 'antenna':
      ctx.strokeStyle = '#9fb3cc';
      ctx.lineWidth = Math.max(2, w * 0.06);
      ctx.beginPath();
      ctx.moveTo(0, top + w * 0.04);
      ctx.quadraticCurveTo(w * 0.1, top - w * 0.36, w * 0.26, top - w * 0.46);
      ctx.stroke();
      ctx.fillStyle = '#ff5a5a';
      ctx.beginPath(); ctx.arc(w * 0.28, top - w * 0.48, w * 0.1, 0, Math.PI * 2); ctx.fill();
      break;
    case 'flower':
      ctx.strokeStyle = '#2f7a35';
      ctx.lineWidth = Math.max(2, w * 0.05);
      ctx.beginPath();
      ctx.moveTo(-w * 0.04, top + w * 0.08);
      ctx.quadraticCurveTo(-w * 0.16, top - w * 0.18, -w * 0.1, top - w * 0.34);
      ctx.stroke();
      ctx.fillStyle = '#ff77c8';
      for (let i = 0; i < 5; i++) {
        const a = (i / 5) * Math.PI * 2;
        ctx.beginPath();
        ctx.ellipse(-w * 0.1 + Math.cos(a) * w * 0.12, top - w * 0.34 + Math.sin(a) * w * 0.12, w * 0.09, w * 0.09, 0, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.fillStyle = '#ffd65a';
      ctx.beginPath(); ctx.arc(-w * 0.1, top - w * 0.34, w * 0.07, 0, Math.PI * 2); ctx.fill();
      break;
    case 'cone':
      ctx.fillStyle = '#ef7d0d';
      ctx.beginPath();
      ctx.moveTo(-w * 0.3, top + w * 0.06);
      ctx.lineTo(0, top - w * 0.54);
      ctx.lineTo(w * 0.3, top + w * 0.06);
      ctx.closePath();
      ctx.fill(); ctx.stroke();
      ctx.fillStyle = '#fff';
      ctx.fillRect(-w * 0.19, top - w * 0.24, w * 0.38, w * 0.1);
      break;
    case 'egg':
      ctx.fillStyle = '#f3f0e6';
      ctx.beginPath();
      ctx.ellipse(0, top - w * 0.12, w * 0.24, w * 0.3, 0, 0, Math.PI * 2);
      ctx.fill(); ctx.stroke();
      break;
    default: break;
  }
  ctx.restore();
}

/**
 * @param {CanvasRenderingContext2D} ctx
 * @param {object} o  x, y, r (body half-width), color {hex, shadow}, dir,
 *                    walk (0..1 phase), ghost, dead, alpha, outline, hat
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

  drawHat(ctx, o.hat, w, h);

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
    x: w / 2, y: h * (opts.hat && opts.hat !== 'none' ? 0.58 : 0.5),
    r: Math.min(w, h) * (opts.hat && opts.hat !== 'none' ? 0.23 : 0.26),
    color, dir: 1, walk: 0, ghost: !!opts.ghost, alpha: opts.alpha, hat: opts.hat,
  });
}

export { roundRect, drawHat };
