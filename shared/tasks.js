// shared/tasks.js - task catalogue, console placement and per-player assignment.

/** Wiring panels that "Fix Wiring" picks three of. */
export const WIRE_PANELS = [
  { room: 'electrical', x: 940, y: 840 },
  { room: 'cafeteria', x: 1770, y: 300 },
  { room: 'navigation', x: 2460, y: 590 },
  { room: 'security', x: 600, y: 560 },
  { room: 'admin', x: 2070, y: 700 },
  { room: 'storage', x: 1365, y: 1135 },
];

/** Rooms that can be the source of a "Download Data" step. */
export const DATA_SOURCES = [
  { room: 'cafeteria', x: 1500, y: 185 },
  { room: 'comms', x: 1830, y: 1150 },
  { room: 'electrical', x: 1200, y: 1050 },
  { room: 'navigation', x: 2650, y: 580 },
  { room: 'weapons', x: 2200, y: 370 },
];

/** Destinations for "Accept Diverted Power". */
export const POWER_NODES = [
  { room: 'upperEngine', x: 800, y: 210 },
  { room: 'lowerEngine', x: 800, y: 1050 },
  { room: 'security', x: 760, y: 710 },
  { room: 'shields', x: 2430, y: 940 },
  { room: 'weapons', x: 1950, y: 370 },
  { room: 'o2', x: 2090, y: 470 },
  { room: 'navigation', x: 2460, y: 370 },
  { room: 'comms', x: 2030, y: 1010 },
];

const step = (minigame, room, x, y, label, opts = {}) => ({ minigame, room, x, y, label, ...opts });

/**
 * Task catalogue. `steps` is either a static array or a function of a RNG for
 * tasks whose consoles move between games.
 */
export const TASK_DEFS = [
  // ---- common (every crewmate gets the same one) ----
  {
    id: 'swipe_card', name: 'Swipe Card', category: 'common',
    steps: () => [step('swipe', 'admin', 1830, 840, 'Swipe Card')],
  },
  {
    id: 'fix_wiring', name: 'Fix Wiring', category: 'common',
    steps: (rng) => pick(WIRE_PANELS, 3, rng).map((p, i) =>
      step('wires', p.room, p.x, p.y, `Fix Wiring (${i + 1}/3)`)),
  },

  // ---- short ----
  {
    id: 'calibrate_distributor', name: 'Calibrate Distributor', category: 'short',
    steps: () => [step('calibrate', 'electrical', 1080, 840, 'Calibrate Distributor')],
  },
  {
    id: 'chart_course', name: 'Chart Course', category: 'short',
    steps: () => [step('chart', 'navigation', 2560, 390, 'Chart Course')],
  },
  {
    id: 'clean_o2', name: 'Clean O2 Filter', category: 'short',
    steps: () => [step('leaves', 'o2', 1930, 590, 'Clean O2 Filter')],
  },
  {
    id: 'clear_asteroids', name: 'Clear Asteroids', category: 'short', visual: true,
    steps: () => [step('asteroids', 'weapons', 2070, 210, 'Clear Asteroids', { visual: true })],
  },
  {
    id: 'prime_shields', name: 'Prime Shields', category: 'short', visual: true,
    steps: () => [step('shields', 'shields', 2310, 1120, 'Prime Shields', { visual: true })],
  },
  {
    id: 'stabilize_steering', name: 'Stabilize Steering', category: 'short',
    steps: () => [step('steering', 'navigation', 2560, 570, 'Stabilize Steering')],
  },
  {
    id: 'unlock_manifolds', name: 'Unlock Manifolds', category: 'short',
    steps: () => [step('manifolds', 'reactor', 270, 470, 'Unlock Manifolds')],
  },
  {
    id: 'align_engine', name: 'Align Engine Output', category: 'short',
    steps: () => [
      step('align', 'upperEngine', 670, 230, 'Align Engine Output (Upper)'),
      step('align', 'lowerEngine', 670, 1030, 'Align Engine Output (Lower)'),
    ],
  },
  {
    id: 'divert_power', name: 'Divert Power', category: 'short',
    steps: (rng) => {
      const dest = pick(POWER_NODES, 1, rng)[0];
      return [
        step('divert', 'electrical', 1000, 1040, `Divert Power to ${titleOf(dest.room)}`, { target: dest.room }),
        step('accept', dest.room, dest.x, dest.y, `Accept Diverted Power (${titleOf(dest.room)})`),
      ];
    },
  },
  {
    id: 'download_data', name: 'Download Data', category: 'short',
    steps: (rng) => {
      const src = pick(DATA_SOURCES, 1, rng)[0];
      return [
        step('download', src.room, src.x, src.y, `Download Data (${titleOf(src.room)})`),
        step('upload', 'admin', 1900, 700, 'Upload Data (Admin)'),
      ];
    },
  },

  // ---- long ----
  {
    id: 'empty_garbage', name: 'Empty Garbage', category: 'long',
    steps: () => [
      step('garbage', 'cafeteria', 1420, 520, 'Empty Garbage (Cafeteria)'),
      step('garbage', 'storage', 1660, 830, 'Empty Garbage (Storage)'),
    ],
  },
  {
    id: 'fuel_engines', name: 'Fuel Engines', category: 'long',
    steps: () => [
      step('fuelpick', 'storage', 1370, 1120, 'Fuel Engines (Storage)'),
      step('fuel', 'upperEngine', 760, 400, 'Fuel Engines (Upper)'),
      step('fuelpick', 'storage', 1370, 1120, 'Fuel Engines (Storage)'),
      step('fuel', 'lowerEngine', 760, 850, 'Fuel Engines (Lower)'),
    ],
  },
  {
    id: 'inspect_sample', name: 'Inspect Sample', category: 'long',
    steps: () => [step('sample', 'medbay', 1130, 260, 'Inspect Sample')],
  },
  {
    id: 'start_reactor', name: 'Start Reactor', category: 'long',
    steps: () => [step('simon', 'reactor', 270, 770, 'Start Reactor')],
  },
  {
    id: 'submit_scan', name: 'Submit Scan', category: 'long', visual: true,
    steps: () => [step('scan', 'medbay', 960, 270, 'Submit Scan', { visual: true })],
  },
];

export const TASK_BY_ID = new Map(TASK_DEFS.map((t) => [t.id, t]));

const ROOM_TITLES = {
  reactor: 'Reactor', upperEngine: 'Upper Engine', lowerEngine: 'Lower Engine',
  security: 'Security', medbay: 'MedBay', electrical: 'Electrical', cafeteria: 'Cafeteria',
  storage: 'Storage', admin: 'Admin', weapons: 'Weapons', o2: 'O2', navigation: 'Navigation',
  shields: 'Shields', comms: 'Communications', communications: 'Communications',
};
function titleOf(room) { return ROOM_TITLES[room] || room; }

function pick(list, n, rng = Math.random) {
  const copy = list.slice();
  for (let i = copy.length - 1; i > 0; i--) {
    const j = Math.floor(rng() * (i + 1));
    [copy[i], copy[j]] = [copy[j], copy[i]];
  }
  return copy.slice(0, n);
}

function resolve(def, rng) {
  const steps = typeof def.steps === 'function' ? def.steps(rng) : def.steps;
  return {
    id: def.id,
    name: def.name,
    category: def.category,
    visual: !!def.visual,
    steps: steps.map((s, i) => ({ ...s, index: i, done: false })),
    step: 0,
    complete: false,
  };
}

/**
 * Build the task list for one game.
 * Common tasks are identical for every crewmate (that is the point of them);
 * short/long tasks are drawn per player without repeats.
 */
export function assignTasks(playerIds, settings, rng = Math.random) {
  const commons = pick(TASK_DEFS.filter((t) => t.category === 'common'), settings.commonTasks, rng)
    .map((def) => resolve(def, rng));

  const shortPool = TASK_DEFS.filter((t) => t.category === 'short');
  const longPool = TASK_DEFS.filter((t) => t.category === 'long');

  const out = new Map();
  for (const id of playerIds) {
    const tasks = [];
    for (const c of commons) tasks.push(JSON.parse(JSON.stringify(c)));
    for (const def of pick(longPool, Math.min(settings.longTasks, longPool.length), rng)) tasks.push(resolve(def, rng));
    for (const def of pick(shortPool, Math.min(settings.shortTasks, shortPool.length), rng)) tasks.push(resolve(def, rng));
    tasks.forEach((t, i) => { t.uid = `${id}:${t.id}:${i}`; });
    out.set(id, tasks);
  }
  return out;
}

/** Total + completed step counts, used for the task bar. */
export function taskProgress(taskLists) {
  let total = 0, done = 0;
  for (const tasks of taskLists) {
    for (const t of tasks) {
      total += t.steps.length;
      done += t.steps.filter((s) => s.done).length;
    }
  }
  return { total, done, ratio: total === 0 ? 1 : done / total };
}

/** The next uncompleted step of a task, or null. */
export function currentStep(task) {
  return task.steps.find((s) => !s.done) || null;
}

/** All consoles a player still needs to visit (for map markers + arrows). */
export function pendingConsoles(tasks) {
  const out = [];
  for (const t of tasks) {
    const s = currentStep(t);
    if (s) out.push({ uid: t.uid, taskId: t.id, name: t.name, ...s });
  }
  return out;
}
