// Every Red Alert action the plugin offers: what a press asks Red Alert to do (a /deck/... path and
// its query) and what the key shows (a view, drawn by svg.js). Pure functions of the key's settings and
// the latest /deck/state, so the tests cover the whole mapping without a Stream Deck.

import {
	COLOURS,
	camChoice,
	camOnProgram,
	findPlayer,
	lobbyByName,
	lobbyByNo,
	normLayout,
	playerById,
	playerHex,
	previewLobby,
	programLobby,
	sabotageLeft,
	stepLobby,
	stepPlayer,
	targetLobby,
	toggleOn,
} from "./model.js";

export const PLUGIN_UUID = "com.ljbutton.redalert";
const id = (name) => `${PLUGIN_UUID}.${name}`;

const LAYOUTS = ["full", "2up", "quad", "grid", "cams"];
const LAYOUT_TITLES = { full: "FULL", "2up": "2-UP", quad: "QUAD", grid: "GRID", cams: "CAMS" };
const TRANSITIONS = ["cut", "swoosh"];

/**
 * The toggles: the path each one's on/off/toggle goes to, and how its key words its state.
 * Red Alert's own state says which way each one is set, so the key never guesses.
 */
export const TOGGLES = {
	mute: { path: "voice/mute", label: "MUTE ALL", onText: "MUTED", onColour: "#e10600" },
	cue: { path: "voice/cue", label: "HEAR PREVIEW", onColour: "#46c28b", onInk: "#0b0b0d" },
	hold: { path: "hold", label: "HOLD", onText: "HELD", onColour: "#ffc15a", onInk: "#0b0b0d" },
	autogrid: { path: "auto/grid", label: "AUTO GRID" },
	autointermission: { path: "auto/intermission", label: "AUTO INTERMISSION" },
	autoswitch: { path: "auto/switch", label: "AUTO SWITCH" },
	standings: { path: "gfx/standings", label: "STANDINGS" },
	alerts: { path: "gfx/alerts", label: "ALERTS", offText: "PAUSED" },
};

const REPLAY_OPS = {
	last: { title: "PLAY LAST", icon: "play" },
	that: { title: "REPLAY THAT", icon: "replay" },
	live: { title: "BACK TO LIVE", icon: "live" },
	toggle: { title: "PLAY/PAUSE", icon: "play" },
	back: { title: "BACK", icon: "back" },
	forward: { title: "FORWARD", icon: "forward" },
	frameback: { title: "FRAME -", icon: "frameback" },
	frameforward: { title: "FRAME +", icon: "frameforward" },
	restart: { title: "RESTART", icon: "restart" },
	zoomin: { title: "ZOOM IN", icon: "zoomin" },
	zoomout: { title: "ZOOM OUT", icon: "zoomout" },
	follow: { title: "FOLLOW", icon: "follow" },
	angle: { title: "ANGLE", icon: "angle" },
	slow: { title: "SPEED", icon: null },
};

const BREAKS = {
	brb: { word: "BRB", title: "BE RIGHT BACK" },
	intermission: { word: "BREAK", title: "INTERMISSION" },
	sponsor: { word: "AD", title: "SPONSOR" },
	off: { word: "BACK", title: "END BREAK" },
};

/** A request for Red Alert, or why there isn't one (shown as an alert on the key). */
const go = (path, query = {}) => ({ path, query });
const refuse = (message) => ({ error: message });

const pick = (value, allowed, fallback) => (allowed.includes(value) ? value : fallback);
const posInt = (value) => {
	const n = Number.parseInt(String(value ?? "").trim(), 10);
	return Number.isFinite(n) && n > 0 ? n : null;
};
const withTransition = (settings, query = {}) => {
	const t = pick(settings.t, TRANSITIONS, null);
	return t ? { ...query, t } : query;
};

/** A key's target lobby setting as the path segment Red Alert takes: "pgm", "pvw" or the number. */
export function targetSegment(settings) {
	if (settings.target === "pvw") return "pvw";
	if (settings.target === "lobby") return posInt(settings.lobbyNo);
	return "pgm";
}

function targetOf(settings, state) {
	const seg = targetSegment(settings);
	return seg === null ? null : targetLobby(state, seg);
}

/** The status the strip at the top of a key shows ("ok" shows nothing). */
const base = (ctx) => ({ status: ctx?.status ?? "ok" });

// For the camera keys and the Player dial: the lobby's camera is off or not sending, a player as a key
// draws them, and the camera armed to follow that player when play resumes.
const noCamIn = (lobby) => Boolean(lobby && (lobby.cam?.on === false || lobby.cam?.sending === false));
const drawn = (p) => (p ? { name: p.name, hex: playerHex(p), dead: Boolean(p.dead) } : null);
const armedOn = (lobby, player) => Boolean(player && lobby?.cam?.armed && lobby.cam.pick === player.id);

/**
 * Cutting to a lobby's camera keeps who it follows: "cam/cut" for the lobby on Program, and for any
 * other lobby the camera's own pick again (or "auto" when it picks by itself), without arming.
 */
export function camCutRequest(settings, state) {
	const seg = targetSegment(settings);
	if (seg === null) return refuse("Set the lobby number.");
	if (seg === "pgm") return go("cam/cut");
	const lobby = targetLobby(state, seg);
	const picked = playerById(lobby, lobby?.cam?.pick ?? null);
	return go(`cam/${seg}/${picked ? `slot${picked.slot}` : "auto"}`);
}

/**
 * The Custom path key's path, tidied: no leading /deck/, and never a key or the impostor flag. A path
 * copied from a link (lobby/Lobby%201) is decoded here, since the request encodes each part once.
 */
export function customRequest(settings) {
	let raw = String(settings.path ?? "").trim();
	raw = raw.replace(/^https?:\/\/[^/]+/i, "").replace(/^\/+/, "").replace(/^deck\//i, "");
	const [pathPart, queryPart = ""] = raw.split("?");
	const segments = pathPart.split("/").filter((s) => s.length > 0);
	if (segments.length === 0) return refuse("Type a path, for example gfx/skip.");
	let path;
	try {
		path = segments.map((s) => decodeURIComponent(s).trim()).join("/");
	} catch {
		return refuse("That path has characters Red Alert doesn't use.");
	}
	if (/[\u0000-\u001f\u007f<>"\\?#]/.test(path) || path.split("/").some((s) => s === "" || s === "." || s === "..")) return refuse("That path has characters Red Alert doesn't use.");
	if (/^state$/i.test(path) || /^img\//i.test(path)) return refuse("That path is for reading, not a press.");
	const query = {};
	for (const [k, v] of new URLSearchParams(queryPart)) if (k !== "key" && k !== "imp") query[k] = v;
	return go(path, query);
}

// ---- The actions ------------------------------------------------------------------------------

const lobbyAction = {
	uuid: id("lobby"),
	defaults: { lobby: "1", mode: "cut", t: "" },
	request(s, state) {
		const which = String(s.lobby ?? "1").trim().toLowerCase();
		// Adding builds a 2-up or quad in Preview one lobby at a time, so it takes a lobby number.
		if (s.mode === "add") {
			const n = posInt(which);
			return n ? go(`pvw/add/${n}`) : refuse("Set a lobby number to add it to Preview.");
		}
		const preview = s.mode === "preview";
		if (!preview) {
			if (which === "next" || which === "prev" || which === "action") return go(`lobby/${which}`, withTransition(s));
			const n = posInt(which);
			return n ? go(`lobby/${n}`, withTransition(s)) : refuse("Set the lobby number.");
		}
		if (which === "action") return refuse("Where the action is goes straight to stream: set this key to Cut.");
		if (which === "next" || which === "prev") {
			const from = previewLobby(state) ?? programLobby(state);
			const to = stepLobby(state, from?.no, which === "next" ? 1 : -1);
			return to ? go(`pvw/lobby/${to.no}`) : refuse("No live lobbies.");
		}
		const n = posInt(which);
		return n ? go(`pvw/lobby/${n}`) : refuse("Set the lobby number.");
	},
	view(s, state, ctx) {
		const which = String(s.lobby ?? "1").trim().toLowerCase();
		const mode = s.mode === "preview" || s.mode === "add" ? "preview" : "cut";
		if (which === "next" || which === "prev" || which === "action") {
			const from = mode === "preview" ? (previewLobby(state) ?? programLobby(state)) : programLobby(state);
			const target = which === "action" ? null : stepLobby(state, from?.no, which === "next" ? 1 : -1);
			return { ...base(ctx), type: "lobbyStep", label: which.toUpperCase(), mode, target: target ? { no: target.no, name: target.name } : null };
		}
		const no = posInt(which);
		const lobby = lobbyByNo(state, no);
		if (!lobby) return { ...base(ctx), type: "lobby", no: no ?? "?", missing: true };
		const left = sabotageLeft(lobby, ctx?.receivedAt ?? 0, ctx?.now ?? 0);
		return {
			...base(ctx),
			type: "lobby",
			no: lobby.no,
			name: lobby.name,
			online: lobby.online !== false,
			phase: lobby.phase,
			tally: lobby.tally,
			health: lobby.health,
			crew: lobby.crew,
			imps: lobby.imps,
			tasks: lobby.tasks,
			sabotage: left === null ? null : { system: lobby.sabotage.system, left },
			flash: Math.floor((ctx?.now ?? 0) / 500) % 2 === 0,
		};
	},
};

const camAction = {
	uuid: id("cam"),
	defaults: { target: "pgm", lobbyNo: "1", who: "slot", slot: "1", colour: "red", mode: "cut" },
	request(s) {
		const seg = targetSegment(s);
		if (seg === null) return refuse("Set the lobby number.");
		let who;
		if (s.who === "colour") who = pick(String(s.colour ?? "").toLowerCase(), COLOURS, null);
		else if (s.who === "next" || s.who === "prev" || s.who === "auto") who = s.who;
		else who = posInt(s.slot) && posInt(s.slot) <= 15 ? `slot${posInt(s.slot)}` : null;
		if (!who) return refuse("Set the player slot (1 to 15) or colour.");
		return go(`cam/${seg}/${who}`, s.mode === "arm" ? { arm: "1" } : {});
	},
	view(s, state, ctx) {
		const lobby = targetOf(s, state);
		const noCam = noCamIn(lobby);
		if (s.who === "next" || s.who === "prev" || s.who === "auto") {
			return {
				...base(ctx),
				type: "camStep",
				label: s.who.toUpperCase(),
				lobbyNo: lobby?.no ?? null,
				mode: s.mode === "arm" ? "arm" : "cut",
				noCam,
				current: drawn(camChoice(lobby)),
			};
		}
		const player = findPlayer(lobby, s.who === "colour" ? s.colour : `slot${posInt(s.slot) ?? 0}`);
		const following = Boolean(player && lobby?.cam?.following === player.id);
		return {
			...base(ctx),
			type: "cam",
			lobbyNo: lobby?.no ?? null,
			slotLabel: s.who === "colour" ? "" : `#${posInt(s.slot) ?? "?"}`,
			player: drawn(player),
			ring: following ? (camOnProgram(state, lobby) ? "pgm" : "on") : null,
			armed: armedOn(lobby, player),
			noCam,
		};
	},
};

/** The lobby numbers in a part of the state (Program or Preview), for the Take key's line. */
function slotNumbers(state, part) {
	const slots = Array.isArray(state?.[part]?.slots) ? state[part].slots : [];
	return slots.map((name) => lobbyByName(state, name)?.no).filter((n) => Number.isFinite(n));
}

const takeAction = {
	uuid: id("take"),
	defaults: { as: "", t: "" },
	request(s) {
		const as = pick(s.as, LAYOUTS, null);
		return go("take", withTransition(s, as ? { as } : {}));
	},
	view(s, state, ctx) {
		const staged = slotNumbers(state, "preview");
		const as = pick(s.as, LAYOUTS, null);
		const layout = as ?? normLayout(state?.preview?.layout);
		const sub = staged.length ? `${staged.map((n) => `L${n}`).join(" ")}${layout && layout !== "none" ? ` ${LAYOUT_TITLES[layout] ?? ""}` : ""}` : "nothing staged";
		return { ...base(ctx), type: "label", icon: "take", title: "TAKE", sub, accent: "green", lit: staged.length > 0, dim: staged.length === 0 };
	},
};

const wideAction = {
	uuid: id("wide"),
	defaults: {},
	request: () => go("wide"),
	view(s, state, ctx) {
		const on = programLobby(state);
		return { ...base(ctx), type: "label", icon: "wide", title: "WIDE", sub: on ? `L${on.no} whole map` : "" };
	},
};

const camCutAction = {
	uuid: id("camcut"),
	defaults: { target: "pgm", lobbyNo: "1" },
	request: (s, state) => camCutRequest(s, state),
	view(s, state, ctx) {
		const lobby = targetOf(s, state);
		const noCam = noCamIn(lobby);
		const who = camChoice(lobby);
		const sub = lobby ? `L${lobby.no}${who ? ` ${who.name}` : ""}` : "no lobby";
		return { ...base(ctx), type: "label", icon: "cam", title: noCam ? "NO CAM" : "CUT TO CAM", sub, accent: "red", lit: camOnProgram(state, lobby), dim: !lobby || noCam };
	},
};

const layoutAction = {
	uuid: id("layout"),
	defaults: { layout: "quad" },
	request: (s) => go(`layout/${pick(s.layout, LAYOUTS, "quad")}`),
	view(s, state, ctx) {
		const layout = pick(s.layout, LAYOUTS, "quad");
		const onProgram = normLayout(state?.program?.layout) === layout;
		const inPreview = !onProgram && normLayout(state?.preview?.layout) === layout && slotNumbers(state, "preview").length > 0;
		return { ...base(ctx), type: "label", icon: layout, title: LAYOUT_TITLES[layout], accent: onProgram ? "red" : "green", lit: onProgram || inPreview };
	},
};

const replayAction = {
	uuid: id("replay"),
	defaults: { op: "last" },
	request: (s) => go(`replay/${pick(s.op, Object.keys(REPLAY_OPS), "last")}`),
	view(s, state, ctx) {
		const op = pick(s.op, Object.keys(REPLAY_OPS), "last");
		const r = state?.replay ?? {};
		const def = REPLAY_OPS[op];
		const v = { ...base(ctx), type: "label", icon: def.icon, title: def.title, accent: "green" };
		if (op === "last") return { ...v, sub: r.last?.title ?? (r.ready ? `${r.ready} ready` : "no clips yet"), lit: (r.ready ?? 0) > 0, dim: !r.ready && !r.last };
		if (op === "that") {
			const on = programLobby(state);
			return { ...v, sub: on ? `L${on.no} last seconds` : "" };
		}
		if (op === "live") return { ...v, accent: "red", lit: Boolean(r.onAir), dim: !r.onAir };
		if (op === "toggle") return { ...v, icon: r.playing ? "pause" : "play", sub: r.onAir ? (r.playing ? "playing" : "paused") : "", dim: !r.onAir };
		if (op === "slow") return { ...v, icon: null, word: `${r.speed ?? 100}%`, sub: "100 / 50 / 25" };
		return { ...v, dim: !r.onAir };
	},
};

const toggleAction = {
	uuid: id("toggle"),
	defaults: { which: "mute", press: "toggle" },
	request(s) {
		const which = pick(s.which, Object.keys(TOGGLES), "mute");
		return go(`${TOGGLES[which].path}/${pick(s.press, ["toggle", "on", "off"], "toggle")}`);
	},
	view(s, state, ctx) {
		const which = pick(s.which, Object.keys(TOGGLES), "mute");
		const t = TOGGLES[which];
		const on = toggleOn(state, which);
		const off = which === "alerts" ? { offColour: "#ffc15a", offInk: "#0b0b0d" } : {};
		return { ...base(ctx), type: "toggle", label: t.label, on, onText: t.onText, offText: t.offText, onColour: t.onColour, onInk: t.onInk, ...off };
	},
};

const breakAction = {
	uuid: id("break"),
	defaults: { kind: "brb" },
	request: (s) => go(`break/${pick(s.kind, Object.keys(BREAKS), "brb")}`),
	view(s, state, ctx) {
		const b = BREAKS[pick(s.kind, Object.keys(BREAKS), "brb")];
		return { ...base(ctx), type: "label", word: b.word, title: b.title };
	},
};

/**
 * Panic: fade to Be right back, mute every lobby voice, hold. While that panic is on in full (Red Alert's
 * state says so), the key shows it and a press undoes it, as the desk's Panic button does. It asks for
 * the panic's own undo (panic/undo), never a plain Undo: a key whose state is a moment behind can't take
 * back the last switch by mistake.
 */
const panicAction = {
	uuid: id("panic"),
	defaults: {},
	request: (s, state) => go(state?.panic ? "panic/undo" : "panic"),
	view: (s, state, ctx) =>
		state?.panic
			? { ...base(ctx), type: "label", icon: "panic", title: "UNDO PANIC", sub: "press: back on air", fill: "red", lit: true, accent: "amber" }
			: { ...base(ctx), type: "label", icon: "panic", title: "PANIC", sub: "BRB + mute", fill: "red" },
};

/**
 * Undo / Back: takes back the panic while there is one, else an automatic switch away from a lobby that
 * dropped, else the last switch made by hand. Lit while a panic is on.
 */
const undoAction = {
	uuid: id("undo"),
	defaults: {},
	request: () => go("undo"),
	view: (s, state, ctx) => ({ ...base(ctx), type: "label", icon: "undo", title: "UNDO", sub: state?.panic ? "the panic" : "back", accent: "amber", lit: Boolean(state?.panic) }),
};

const customAction = {
	uuid: id("custom"),
	defaults: { path: "gfx/skip", label: "" },
	request: (s) => customRequest(s),
	view(s, state, ctx) {
		const label = String(s.label ?? "").trim();
		const path = String(s.path ?? "").trim();
		return label ? { ...base(ctx), type: "label", word: label, sub: path } : { ...base(ctx), type: "label", icon: "link", title: path.split("/").filter(Boolean).slice(-2).join(" ").toUpperCase() || "CUSTOM", sub: path };
	},
};

// ---- Stream Deck + dials ------------------------------------------------------------------------

/**
 * The Preview dial. Turning picks the lobby to stage (shown at once, sent when the dial rests),
 * pushing takes Preview to Program, a tap on the strip clears Preview.
 */
const previewDial = {
	uuid: id("dial.preview"),
	dial: true,
	// Push takes Preview, so a lobby still waiting to be staged goes in first.
	flushFirst: true,
	defaults: { as: "", t: "" },
	/** The lobby number a turn of `ticks` lands on, from the one about to be staged or else Preview. */
	rotate(s, state, pending, ticks) {
		const from = pending ?? previewLobby(state)?.no ?? programLobby(state)?.no ?? null;
		return stepLobby(state, from, ticks)?.no ?? null;
	},
	commit: (s, state, pending) => go(`pvw/lobby/${pending}`),
	request: (s) => takeAction.request(s),
	touch: () => go("pvw/clear"),
	view(s, state, ctx, pending) {
		const lobby = pending != null ? lobbyByNo(state, pending) : previewLobby(state);
		const layout = pick(s.as, LAYOUTS, null) ?? normLayout(state?.preview?.layout);
		return {
			...base(ctx),
			type: "previewDial",
			pending: pending != null,
			layout: layout === "none" ? "" : LAYOUT_TITLES[layout] ?? layout,
			lobby: lobby ? { no: lobby.no, name: lobby.name, phase: lobby.phase, online: lobby.online !== false } : null,
		};
	},
};

/**
 * The Player dial. Turning picks the next living player for the target lobby's camera (armed: it
 * follows them without a cut), pushing cuts to that camera, a tap on the strip goes back wide.
 */
const playerDial = {
	uuid: id("dial.player"),
	dial: true,
	defaults: { target: "pgm", lobbyNo: "1" },
	rotate(s, state, pending, ticks) {
		const lobby = targetOf(s, state);
		const from = pending ?? camChoice(lobby)?.id ?? null;
		return stepPlayer(lobby, from, ticks)?.id ?? null;
	},
	commit(s, state, pending) {
		const seg = targetSegment(s);
		const player = playerById(targetOf(s, state), pending);
		return seg !== null && player ? go(`cam/${seg}/slot${player.slot}`, { arm: "1" }) : refuse("That player has gone.");
	},
	request(s, state, pending) {
		const seg = targetSegment(s);
		const player = playerById(targetOf(s, state), pending);
		return seg !== null && player ? go(`cam/${seg}/slot${player.slot}`) : camCutRequest(s, state);
	},
	touch: () => go("wide"),
	view(s, state, ctx, pending) {
		const lobby = targetOf(s, state);
		const player = pending != null ? playerById(lobby, pending) : camChoice(lobby);
		return {
			...base(ctx),
			type: "playerDial",
			pending: pending != null,
			lobbyNo: lobby?.no ?? null,
			noCam: noCamIn(lobby),
			player: drawn(player),
			armed: armedOn(lobby, player),
			following: Boolean(player && lobby?.cam?.following === player.id),
			onAir: Boolean(player && lobby?.cam?.following === player.id && camOnProgram(state, lobby)),
		};
	},
};

/**
 * How the Replay dial turns into requests: Red Alert ignores the same request twice within 300 ms, so
 * turns are gathered up and sent at most every 320 ms; a slow turn steps frame by frame, a fast one
 * (3 notches or more waiting) jumps by seconds.
 */
export function scrubStep(pending) {
	if (!pending) return null;
	if (Math.abs(pending) >= 3) return { op: pending > 0 ? "forward" : "back", consumed: pending };
	return { op: pending > 0 ? "frameforward" : "frameback", consumed: Math.sign(pending) };
}

const replayDial = {
	uuid: id("dial.replay"),
	dial: true,
	scrub: true,
	defaults: {},
	request: () => go("replay/toggle"),
	touch: () => go("replay/slow"),
	view(s, state, ctx) {
		const r = state?.replay ?? {};
		return { ...base(ctx), type: "replayDial", onAir: Boolean(r.onAir), playing: Boolean(r.playing), speed: r.speed ?? 100, ready: r.ready ?? 0, last: r.last?.title ?? "" };
	},
};

/** Every action, by its manifest UUID. */
export const ACTIONS = [
	lobbyAction,
	camAction,
	takeAction,
	wideAction,
	camCutAction,
	layoutAction,
	replayAction,
	toggleAction,
	breakAction,
	panicAction,
	undoAction,
	customAction,
	previewDial,
	playerDial,
	replayDial,
];

export const BY_UUID = Object.fromEntries(ACTIONS.map((a) => [a.uuid, a]));

/** An action's settings with its defaults filled in (a new key has none of its own yet). */
export function settingsOf(action, settings) {
	return { ...action.defaults, ...(settings ?? {}) };
}
