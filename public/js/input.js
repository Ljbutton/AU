// public/js/input.js - keyboard, mouse-drag and touch joystick.

export const input = {
  dx: 0, dy: 0,
  enabled: true,
  pointerActive: false,
  keys: new Set(),
};

const actionHandlers = new Map();
export function onAction(name, fn) {
  if (!actionHandlers.has(name)) actionHandlers.set(name, new Set());
  actionHandlers.get(name).add(fn);
}
function fire(name) {
  const set = actionHandlers.get(name);
  if (set) for (const fn of set) fn();
}

const KEY_ACTIONS = {
  KeyE: 'use', Space: 'use',
  KeyQ: 'kill',
  KeyR: 'report',
  KeyF: 'vent',
  Tab: 'map', KeyM: 'map',
  Escape: 'escape',
  KeyC: 'chat',
};

const MOVE_KEYS = {
  KeyW: [0, -1], ArrowUp: [0, -1],
  KeyS: [0, 1], ArrowDown: [0, 1],
  KeyA: [-1, 0], ArrowLeft: [-1, 0],
  KeyD: [1, 0], ArrowRight: [1, 0],
};

function typingInInput() {
  const el = document.activeElement;
  return el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA');
}

export function initInput(canvas, joystickEl, knobEl) {
  window.addEventListener('keydown', (e) => {
    if (e.code === 'Tab') e.preventDefault();
    if (typingInInput()) {
      if (e.code === 'Escape') document.activeElement.blur();
      return;
    }
    if (MOVE_KEYS[e.code]) { input.keys.add(e.code); e.preventDefault(); }
    const action = KEY_ACTIONS[e.code];
    if (action && !e.repeat) { fire(action); e.preventDefault(); }
  });
  window.addEventListener('keyup', (e) => input.keys.delete(e.code));
  window.addEventListener('blur', () => input.keys.clear());

  // ---- pointer / touch joystick -------------------------------------------
  let originX = 0, originY = 0, pointerId = null;
  const RADIUS = 66;

  const place = (x, y) => {
    joystickEl.style.left = `${x - RADIUS}px`;
    joystickEl.style.top = `${y - RADIUS}px`;
    joystickEl.style.position = 'absolute';
    joystickEl.style.bottom = 'auto';
  };

  const start = (e) => {
    if (!input.enabled) return;
    if (e.target.closest('button, .overlay, #hud .action-buttons, .vent-ui')) return;
    pointerId = e.pointerId;
    originX = e.clientX; originY = e.clientY;
    input.pointerActive = true;
    place(originX, originY);
    joystickEl.classList.add('visible');
    knobEl.style.transform = 'translate(0px, 0px)';
    canvas.setPointerCapture?.(e.pointerId);
  };
  const move = (e) => {
    if (pointerId !== e.pointerId) return;
    let dx = e.clientX - originX, dy = e.clientY - originY;
    const d = Math.hypot(dx, dy);
    const max = RADIUS;
    if (d > max) { dx = (dx / d) * max; dy = (dy / d) * max; }
    knobEl.style.transform = `translate(${dx}px, ${dy}px)`;
    const mag = Math.min(1, d / max);
    if (d > 6) {
      const len = Math.hypot(dx, dy) || 1;
      input.dx = (dx / len) * mag;
      input.dy = (dy / len) * mag;
    } else {
      input.dx = 0; input.dy = 0;
    }
  };
  const end = (e) => {
    if (pointerId !== e.pointerId) return;
    pointerId = null;
    input.pointerActive = false;
    input.dx = 0; input.dy = 0;
    joystickEl.classList.remove('visible');
  };

  canvas.addEventListener('pointerdown', start);
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', end);
  window.addEventListener('pointercancel', end);
}

/** Current movement vector, combining keyboard and joystick. */
export function readMovement() {
  if (!input.enabled) return { dx: 0, dy: 0 };
  if (input.pointerActive) return { dx: input.dx, dy: input.dy };
  let dx = 0, dy = 0;
  for (const code of input.keys) {
    const v = MOVE_KEYS[code];
    if (v) { dx += v[0]; dy += v[1]; }
  }
  const len = Math.hypot(dx, dy);
  if (len > 1) { dx /= len; dy /= len; }
  return { dx, dy };
}
