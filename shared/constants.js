// shared/constants.js - tuning values and enums shared by client and server.

export const TICK_RATE = 20;              // authoritative simulation ticks / second
export const TICK_MS = 1000 / TICK_RATE;
export const SNAPSHOT_RATE = 20;          // state broadcasts / second
export const PLAYER_RADIUS = 22;
export const PLAYER_BASE_SPEED = 230;     // px/s at speed multiplier 1.0
export const GHOST_SPEED_MULT = 1.25;
export const INTERACT_RANGE = 90;         // how close to a console to use it
export const BODY_REPORT_RANGE = 110;
export const VENT_RANGE = 55;
export const CORPSE_RADIUS = 26;

export const COLORS = [
  { id: 'red',     name: 'Red',     hex: '#c51111', shadow: '#7a0838' },
  { id: 'blue',    name: 'Blue',    hex: '#132ed1', shadow: '#09158e' },
  { id: 'green',   name: 'Green',   hex: '#117f2d', shadow: '#0a4d2e' },
  { id: 'pink',    name: 'Pink',    hex: '#ed54ba', shadow: '#ac2bad' },
  { id: 'orange',  name: 'Orange',  hex: '#ef7d0d', shadow: '#b3590f' },
  { id: 'yellow',  name: 'Yellow',  hex: '#f5f557', shadow: '#c38823' },
  { id: 'black',   name: 'Black',   hex: '#3f474e', shadow: '#1e1f26' },
  { id: 'white',   name: 'White',   hex: '#d6e0f0', shadow: '#8394bf' },
  { id: 'purple',  name: 'Purple',  hex: '#6b2fbb', shadow: '#3b177c' },
  { id: 'brown',   name: 'Brown',   hex: '#71491e', shadow: '#5e2615' },
  { id: 'cyan',    name: 'Cyan',    hex: '#38fedc', shadow: '#24a8be' },
  { id: 'lime',    name: 'Lime',    hex: '#50ef39', shadow: '#15a742' },
];
export const COLOR_BY_ID = new Map(COLORS.map((c) => [c.id, c]));

/** Cosmetic hats. Drawn procedurally in public/js/sprites.js. */
export const HATS = [
  { id: 'none', name: 'No Hat' },
  { id: 'band', name: 'Head Band' },
  { id: 'cap', name: 'Cap' },
  { id: 'tophat', name: 'Top Hat' },
  { id: 'crown', name: 'Crown' },
  { id: 'horns', name: 'Horns' },
  { id: 'antenna', name: 'Antenna' },
  { id: 'flower', name: 'Flower' },
  { id: 'cone', name: 'Traffic Cone' },
  { id: 'egg', name: 'Egg' },
];
export const HAT_IDS = new Set(HATS.map((h) => h.id));

export const MAX_PLAYERS = 12;
export const MIN_PLAYERS = 4;

export const DEFAULT_SETTINGS = {
  impostors: 1,
  playerSpeed: 1.0,
  crewVision: 1.0,
  impostorVision: 1.5,
  killCooldown: 25,        // seconds
  killDistance: 'normal',  // short | normal | long
  emergencyMeetings: 1,    // per player
  emergencyCooldown: 15,   // seconds
  discussionTime: 15,      // seconds
  votingTime: 90,          // seconds
  confirmEjects: true,
  visualTasks: true,
  anonymousVotes: false,
  taskBarUpdates: 'always', // always | meetings | never
  commonTasks: 1,
  longTasks: 2,
  shortTasks: 3,
};

export const KILL_DISTANCES = { short: 110, normal: 150, long: 200 };

export const VISION = {
  crew: 380,
  impostor: 380,           // multiplied by settings.impostorVision
  lightsOut: 0.33,         // multiplier applied to crew vision when lights are sabotaged
};

export const SABOTAGE = {
  cooldown: 25,            // seconds between sabotages
  doorCooldown: 10,        // per-room door cooldown
  doorDuration: 10,        // seconds doors stay shut
  reactorTime: 45,         // countdown to defeat
  o2Time: 45,
  reactorHoldTime: 3,      // seconds a hand must stay on a pad
};

export const MEETING = {
  ejectAnimation: 6,       // seconds of the "X was ejected" cutscene
  resultsTime: 5,
  postMeetingKillCooldown: 10,
};

export const ROLE = { CREW: 'crew', IMPOSTOR: 'impostor' };
export const PHASE = { LOBBY: 'lobby', PLAYING: 'playing', MEETING: 'meeting', ENDED: 'ended' };
export const MEETING_PHASE = { DISCUSS: 'discuss', VOTE: 'vote', RESULTS: 'results' };

export const WIN = {
  CREW_TASKS: 'crew_tasks',
  CREW_VOTE: 'crew_vote',
  IMPOSTOR_KILL: 'impostor_kill',
  IMPOSTOR_VOTE: 'impostor_vote',
  IMPOSTOR_SABOTAGE: 'impostor_sabotage',
};

export const PET_NONE = 'none';

/** Clamp incoming lobby settings so a malicious host cannot break the game. */
export function sanitizeSettings(raw, playerCount = MAX_PLAYERS) {
  const s = { ...DEFAULT_SETTINGS, ...(raw || {}) };
  const num = (v, lo, hi, dflt) => {
    const n = Number(v);
    return Number.isFinite(n) ? Math.min(hi, Math.max(lo, n)) : dflt;
  };
  const maxImp = Math.max(1, Math.min(3, Math.floor((Math.max(playerCount, MIN_PLAYERS) - 1) / 2)));
  return {
    impostors: Math.min(maxImp, num(s.impostors, 1, 3, 1)),
    playerSpeed: num(s.playerSpeed, 0.5, 3, 1),
    crewVision: num(s.crewVision, 0.25, 5, 1),
    impostorVision: num(s.impostorVision, 0.25, 5, 1.5),
    killCooldown: num(s.killCooldown, 5, 60, 25),
    killDistance: ['short', 'normal', 'long'].includes(s.killDistance) ? s.killDistance : 'normal',
    emergencyMeetings: num(s.emergencyMeetings, 0, 9, 1),
    emergencyCooldown: num(s.emergencyCooldown, 0, 60, 15),
    discussionTime: num(s.discussionTime, 0, 120, 15),
    votingTime: num(s.votingTime, 15, 300, 90),
    confirmEjects: !!s.confirmEjects,
    visualTasks: !!s.visualTasks,
    anonymousVotes: !!s.anonymousVotes,
    taskBarUpdates: ['always', 'meetings', 'never'].includes(s.taskBarUpdates) ? s.taskBarUpdates : 'always',
    commonTasks: num(s.commonTasks, 0, 2, 1),
    longTasks: num(s.longTasks, 0, 3, 2),
    shortTasks: num(s.shortTasks, 0, 5, 3),
  };
}
