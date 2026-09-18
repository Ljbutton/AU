// public/js/minigames/index.js
// Minigame host + registry. Each game is a factory that returns an element and
// calls ctx.done() when the player finishes it.

import { sfx } from '../sound.js';
import * as basic from './basic.js';
import * as wires from './wires.js';
import * as dexterity from './dexterity.js';
import * as memory from './memory.js';

export const GAMES = {
  ...basic.games,
  ...wires.games,
  ...dexterity.games,
  ...memory.games,
};

let active = null;

export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') node.className = v;
    else if (k === 'style') Object.assign(node.style, v);
    else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2).toLowerCase(), v);
    else if (v !== undefined && v !== null) node.setAttribute(k, v);
  }
  for (const c of children.flat()) {
    if (c === null || c === undefined) continue;
    node.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return node;
}

export function canvasEl(w, h) {
  const c = el('canvas', { width: w, height: h });
  c.style.maxWidth = '100%';
  c.style.touchAction = 'none';
  return c;
}

/** Canvas-local pointer coordinates, scaled for CSS sizing. */
export function localPoint(canvas, ev) {
  const r = canvas.getBoundingClientRect();
  return {
    x: (ev.clientX - r.left) * (canvas.width / r.width),
    y: (ev.clientY - r.top) * (canvas.height / r.height),
  };
}

export function bar(initial = 0) {
  const fill = el('div');
  fill.style.width = `${initial * 100}%`;
  const wrap = el('div', { class: 'mg-bar' }, fill);
  return { el: wrap, set: (v) => { fill.style.width = `${Math.max(0, Math.min(1, v)) * 100}%`; } };
}

/**
 * Open a minigame in the overlay.
 * @returns {boolean} false when the minigame kind is unknown.
 */
export function openMinigame(kind, { title, onComplete, onClose, data } = {}) {
  const overlay = document.getElementById('minigame-overlay');
  const body = document.getElementById('minigame-body');
  const titleEl = document.getElementById('minigame-title');
  const factory = GAMES[kind];
  if (!factory) return false;

  closeMinigame(true);

  let finished = false;
  const ctx = {
    data: data || {},
    done() {
      if (finished) return;
      finished = true;
      sfx.taskDone();
      setTimeout(() => { closeMinigame(); onComplete?.(); }, 260);
    },
    progress() { sfx.taskStep(); },
    close() { closeMinigame(); },
  };

  const game = factory(ctx);
  titleEl.textContent = title || game.title || 'Task';
  body.replaceChildren(game.el);
  overlay.classList.remove('hidden');
  active = { game, onClose, kind };
  game.start?.();
  return true;
}

export function closeMinigame(silent = false) {
  const overlay = document.getElementById('minigame-overlay');
  const body = document.getElementById('minigame-body');
  if (active) {
    active.game.destroy?.();
    const cb = active.onClose;
    active = null;
    if (!silent) cb?.();
  }
  overlay.classList.add('hidden');
  body.replaceChildren();
}

export function minigameOpen() { return !!active; }
export function activeKind() { return active?.kind || null; }
