// Key art, drawn here as SVG and handed to Stream Deck with setImage, so every key shows Red Alert's
// live state: the lobby's number and phase with a red (Program) or green (Preview) tally border, a
// player's crewmate and name, the toggles' real on/off state. Stream Deck renders SVG with Qt, which
// only knows SVG Tiny, so this sticks to plain shapes, presentation attributes and text (no CSS, no
// filters, no arcs in paths, no dominant-baseline).

import { fit } from "./model.js";

export const KEY = 144;
export const DIAL_W = 200;
export const DIAL_H = 100;

const FONT = "Arial, Helvetica, sans-serif";

/** Red Alert's colours (caster.html's Red Alert theme). */
export const C = {
	bg: "#141417",
	bgDim: "#0b0b0d",
	fg: "#f4f4f6",
	muted: "#9a9aa4",
	line: "#2c2c33",
	red: "#e10600",
	redDark: "#4a0806",
	green: "#1fa143",
	amber: "#ffc15a",
	good: "#46c28b",
	danger: "#ff3b30",
	visor: "#9fd8ea",
	ink: "#0b0b0d",
};

const PHASES = {
	ingame: { label: "IN PLAY", fill: C.good },
	meeting: { label: "MEETING", fill: C.amber },
	lobby: { label: "LOBBY", fill: C.muted },
	ended: { label: "ENDED", fill: C.muted },
	menu: { label: "MENU", fill: C.muted },
};

/** A lobby's phase as a key words and colours it (an unknown one in its own words). */
const phaseOf = (phase) => PHASES[phase] ?? { label: String(phase ?? "").toUpperCase(), fill: C.muted };

const HEALTH = { green: C.good, yellow: C.amber, red: C.danger };

/** Text made safe to put inside SVG (names come from players and organisers). */
export function esc(value) {
	return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

/** A text element; `o` takes size, weight, fill, anchor and opacity. */
export function text(x, y, value, o = {}) {
	const attrs = [
		`x="${x}"`,
		`y="${y}"`,
		`font-family="${FONT}"`,
		`font-size="${o.size ?? 20}"`,
		`font-weight="${o.weight ?? "bold"}"`,
		`fill="${o.fill ?? C.fg}"`,
	];
	if (o.anchor) attrs.push(`text-anchor="${o.anchor}"`);
	if (o.opacity !== undefined) attrs.push(`opacity="${o.opacity}"`);
	return `<text ${attrs.join(" ")}>${esc(value)}</text>`;
}

function svg(width, height, body) {
	return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">${body}</svg>`;
}

/** A finished SVG as the data URL setImage and setFeedback take (base64, as Elgato's own examples). */
export function dataUrl(markup) {
	return "data:image/svg+xml;base64," + Buffer.from(markup, "utf8").toString("base64");
}

/** A colour made darker (factor below 1) or lighter (above 1). */
export function shade(hex, factor) {
	const m = /^#?([0-9a-f]{6})$/i.exec(hex ?? "");
	if (!m) return hex;
	const n = parseInt(m[1], 16);
	const part = (v) => Math.max(0, Math.min(255, Math.round(v * factor)));
	const out = (part(n >> 16) << 16) | (part((n >> 8) & 255) << 8) | part(n & 255);
	return "#" + out.toString(16).padStart(6, "0");
}

/**
 * A crewmate, facing right, in the player's colour: about 100 wide and 120 tall at scale 1, placed
 * with its top-left at (x, y). Dead players are dimmed.
 */
export function crewmate(x, y, scale, hex, dead = false) {
	const body = hex ?? C.line;
	const shape = [
		`<rect x="4" y="40" width="22" height="52" rx="10" fill="${shade(body, 0.7)}" stroke="${C.ink}" stroke-width="5"/>`,
		`<path d="M18 46 C18 22 34 8 50 8 C66 8 82 22 82 46 L82 108 Q82 116 74 116 L62 116 Q56 116 56 110 L56 98 L44 98 L44 110 Q44 116 38 116 L26 116 Q18 116 18 108 Z" fill="${body}" stroke="${C.ink}" stroke-width="5" stroke-linejoin="round"/>`,
		`<rect x="46" y="28" width="46" height="28" rx="14" fill="${C.visor}" stroke="${C.ink}" stroke-width="5"/>`,
		`<rect x="60" y="34" width="22" height="7" rx="3.5" fill="#ffffff" opacity="0.7"/>`,
	].join("");
	return `<g transform="translate(${x} ${y}) scale(${scale})"${dead ? ' opacity="0.3"' : ""}>${shape}</g>`;
}

/** A key or a dial strip: the background, the body, a border `inset` in from the edge and the status strip. */
function canvas(width, height, inset, rx, body, o, strokeWidth) {
	const bg = `<rect x="0" y="0" width="${width}" height="${height}" fill="${o.bg ?? C.bg}"/>`;
	const border = o.border ? `<rect x="${inset}" y="${inset}" width="${width - 2 * inset}" height="${height - 2 * inset}" rx="${rx}" fill="none" stroke="${o.border}" stroke-width="${strokeWidth}"${o.dashed ? ' stroke-dasharray="10 6"' : ""}/>` : "";
	return svg(width, height, bg + faded(body + border, o.status) + statusStrip(o.status, width));
}

const frame = (body, o = {}) => canvas(KEY, KEY, 4, 12, body, o, o.borderWidth ?? 8);

/** Without a live connection the last state is stale, so the key shows it faded under the strip. */
function faded(body, status) {
	return STATUS[status] ? `<g opacity="0.45">${body}</g>` : body;
}

/**
 * The strip across the top of every key when Red Alert can't be reached or the key is wrong, so a
 * dead connection never looks like a quiet stream.
 */
const STATUS = { offline: "OFFLINE", badkey: "WRONG KEY", nokey: "SET UP", old: "UPDATE RED ALERT", locked: "RED ALERT LOCKED" };

export function statusStrip(status, width) {
	const label = STATUS[status];
	if (!label) return "";
	return `<rect x="0" y="0" width="${width}" height="28" fill="${C.redDark}"/>` + text(width / 2, 20, label, { size: label.length > 10 ? 13 : 16, fill: "#ff8a80", anchor: "middle" });
}

function tallyColour(tally) {
	return tally === "pgm" ? C.red : tally === "pvw" ? C.green : null;
}

function bigLabel(value, y = 70, fill = C.fg) {
	const n = [...String(value)].length;
	const size = n <= 2 ? 52 : n <= 3 ? 44 : n <= 5 ? 34 : n <= 7 ? 26 : 20;
	return text(KEY / 2, y, value, { size, anchor: "middle", fill });
}

// ---- Lobby keys ------------------------------------------------------------------------------

/** A lobby key: number, short name, phase, crew v impostors, tally border and a flashing sabotage. */
export function lobbyKey(v) {
	const parts = [];
	if (v.missing) {
		parts.push(text(16, 60, v.no ?? "?", { size: 52, fill: C.muted }));
		parts.push(text(14, 96, "NO LOBBY", { size: 18, fill: C.muted }));
		parts.push(text(14, 122, "not live yet", { size: 15, weight: "normal", fill: C.muted }));
		return frame(parts.join(""), { status: v.status, border: C.line, borderWidth: 2 });
	}
	const dim = v.online === false;
	const phase = dim ? { label: "OFFLINE", fill: C.muted } : phaseOf(v.phase);
	parts.push(text(16, 56, v.no ?? "?", { size: 52, fill: dim ? C.muted : C.fg }));
	const tally = tallyColour(v.tally);
	if (tally) {
		parts.push(`<rect x="88" y="12" width="44" height="24" rx="5" fill="${tally}"/>`);
		parts.push(text(110, 30, v.tally === "pgm" ? "PGM" : "PVW", { size: 16, anchor: "middle" }));
	}
	if (!dim && HEALTH[v.health]) parts.push(`<circle cx="122" cy="52" r="7" fill="${HEALTH[v.health]}"/>`);
	parts.push(text(14, 82, phase.label, { size: 18, fill: phase.fill }));
	parts.push(text(14, 106, fit(v.name, 12), { size: 18, weight: "normal", fill: dim ? C.muted : C.fg }));
	if (v.sabotage && !dim) {
		const secs = Math.ceil(v.sabotage.left ?? 0);
		parts.push(`<rect x="8" y="112" width="128" height="26" rx="5" fill="${v.flash ? C.red : C.redDark}"/>`);
		parts.push(text(72, 131, `${fit(String(v.sabotage.system ?? "SABOTAGE").toUpperCase(), 8)} ${secs}`, { size: 17, anchor: "middle" }));
	} else if (!dim && Number.isFinite(v.crew)) {
		parts.push(text(14, 132, `${v.crew}v${Number.isFinite(v.imps) ? v.imps : "?"}`, { size: 22 }));
		if (Number.isFinite(v.tasks)) parts.push(text(132, 132, `${Math.round(v.tasks)}%`, { size: 17, weight: "normal", fill: C.muted, anchor: "end" }));
	}
	return frame(parts.join(""), { status: v.status, bg: dim ? C.bgDim : C.bg, border: tally ?? C.line, borderWidth: tally ? 8 : 2 });
}

/** A Lobby key set to next or prev: the word, an arrow and where it goes (Best replay is a label key). */
export function lobbyStepKey(v) {
	const parts = [];
	if (v.label === "PREV") parts.push(`<path d="M92 30 L52 52 L92 74 Z" fill="${C.fg}"/>`);
	else parts.push(`<path d="M52 30 L92 52 L52 74 Z" fill="${C.fg}"/>`);
	parts.push(text(72, 108, v.label, { size: 22, anchor: "middle" }));
	if (v.target) parts.push(text(72, 132, fit(`${v.target.no} ${v.target.name ?? ""}`, 13), { size: 15, weight: "normal", fill: C.muted, anchor: "middle" }));
	return frame(parts.join(""), { status: v.status, border: v.mode === "preview" ? C.green : C.line, borderWidth: v.mode === "preview" ? 4 : 2 });
}

// ---- Player camera keys ---------------------------------------------------------------------

// Both camera keys: the lobby's number top left, and NO CAM when its camera is off or not sending.
const lobbyTag = (v) => text(10, 22, v.lobbyNo ? `L${v.lobbyNo}` : "L?", { size: 16, fill: C.muted });
const noCamTag = (v) => (v.noCam ? text(72, 42, "NO CAM", { size: 14, fill: C.danger, anchor: "middle" }) : "");

/**
 * A player camera key: the crewmate in the player's colour and their name, dimmed when dead, a ring
 * when the camera follows them (red while that camera is on stream) and ARMED while the pick waits for
 * play to resume.
 */
export function camKey(v) {
	const parts = [lobbyTag(v)];
	if (v.slotLabel) parts.push(text(134, 22, v.slotLabel, { size: 16, fill: C.muted, anchor: "end" }));
	if (!v.player) {
		parts.push(crewmate(42, 30, 0.6, C.line));
		parts.push(text(72, 132, "EMPTY", { size: 18, fill: C.muted, anchor: "middle" }));
		return frame(parts.join(""), { status: v.status, border: C.line, borderWidth: 2 });
	}
	if (v.ring) parts.push(`<circle cx="72" cy="66" r="48" fill="none" stroke="${v.ring === "pgm" ? C.red : C.fg}" stroke-width="6"/>`);
	parts.push(crewmate(42, 30, 0.6, v.player.hex, v.player.dead));
	if (v.player.dead) parts.push(text(72, 72, "DEAD", { size: 20, fill: C.muted, anchor: "middle" }));
	parts.push(text(72, 132, fit(v.player.name, 10), { size: 20, anchor: "middle", fill: v.player.dead ? C.muted : C.fg }));
	if (v.armed) {
		parts.push(`<rect x="24" y="80" width="96" height="26" rx="5" fill="${C.amber}"/>`);
		parts.push(text(72, 99, "ARMED", { size: 18, fill: C.ink, anchor: "middle" }));
	}
	parts.push(noCamTag(v));
	return frame(parts.join(""), { status: v.status, border: v.ring === "pgm" ? C.red : C.line, borderWidth: v.ring === "pgm" ? 6 : 2 });
}

/** A Player cam key set to next, prev or auto: the word, and who the camera follows now. */
export function camStepKey(v) {
	const parts = [lobbyTag(v)];
	parts.push(text(134, 22, v.label, { size: 18, anchor: "end" }));
	if (v.current) {
		parts.push(crewmate(48, 32, 0.5, v.current.hex, v.current.dead));
		parts.push(text(72, 132, fit(v.current.name, 10), { size: 18, anchor: "middle" }));
	} else {
		parts.push(crewmate(48, 32, 0.5, C.line));
		parts.push(text(72, 132, v.label === "AUTO" ? "it picks" : "nobody", { size: 16, weight: "normal", fill: C.muted, anchor: "middle" }));
	}
	if (v.label === "NEXT") parts.push(`<path d="M112 58 L132 72 L112 86 Z" fill="${C.fg}"/>`);
	if (v.label === "PREV") parts.push(`<path d="M32 58 L12 72 L32 86 Z" fill="${C.fg}"/>`);
	parts.push(noCamTag(v));
	// Arm: amber; into Preview (the default): green, as a Lobby key set to Preview; a cut: plain.
	const border = v.mode === "arm" ? C.amber : v.mode === "preview" ? C.green : C.line;
	return frame(parts.join(""), { status: v.status, border, borderWidth: border === C.line ? 2 : 4 });
}

// ---- Toggles and labelled keys --------------------------------------------------------------

/** A toggle: its name and a big ON/OFF (or the toggle's own words) in its state's colour. */
export function toggleKey(v) {
	const on = v.on;
	const known = on === true || on === false;
	const fill = !known ? C.line : on ? (v.onColour ?? C.green) : (v.offColour ?? C.bgDim);
	const ink = !known ? C.muted : on ? (v.onInk ?? C.fg) : (v.offInk ?? C.muted);
	const word = !known ? "?" : on ? (v.onText ?? "ON") : (v.offText ?? "OFF");
	const parts = [];
	// "AUTO GRID", "AUTO SWITCH"…: a small AUTO over the bigger name, so the long ones fit.
	const auto = /^AUTO (.+)$/.exec(v.label ?? "");
	if (auto) {
		parts.push(text(72, 24, "AUTO", { size: 14, fill: C.muted, anchor: "middle" }));
		parts.push(text(72, 46, auto[1], { size: [...auto[1]].length > 9 ? 17 : 20, anchor: "middle" }));
	} else parts.push(text(72, 40, v.label ?? "", { size: [...String(v.label ?? "")].length > 9 ? 16 : 20, anchor: "middle" }));
	parts.push(`<rect x="14" y="58" width="116" height="62" rx="10" fill="${fill}" stroke="${known && (on || v.offColour) ? fill : C.line}" stroke-width="3"/>`);
	const n = [...word].length;
	parts.push(text(72, 101, word, { size: n <= 3 ? 32 : n <= 5 ? 27 : 22, anchor: "middle", fill: ink }));
	return frame(parts.join(""), { status: v.status });
}

const ICONS = {
	take: `<rect x="22" y="34" width="38" height="30" rx="4" fill="none" stroke="${C.green}" stroke-width="6"/><path d="M64 49 H84 M76 40 L86 49 L76 58" fill="none" stroke="${C.fg}" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/><rect x="90" y="34" width="34" height="30" rx="4" fill="${C.red}"/>`,
	wide: `<path d="M34 42 V28 H50 M94 28 H110 V42 M110 64 V78 H94 M50 78 H34 V64" fill="none" stroke="${C.fg}" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/><rect x="54" y="44" width="36" height="18" rx="3" fill="${C.muted}"/>`,
	cam: `<rect x="30" y="34" width="60" height="40" rx="7" fill="${C.fg}"/><path d="M92 48 L116 36 V72 L92 60 Z" fill="${C.fg}"/><circle cx="48" cy="54" r="8" fill="${C.red}"/>`,
	full: `<rect x="32" y="28" width="80" height="50" rx="4" fill="${C.fg}"/>`,
	"2up": `<rect x="26" y="34" width="44" height="34" rx="3" fill="${C.fg}"/><rect x="74" y="34" width="44" height="34" rx="3" fill="${C.fg}"/>`,
	quad: `<rect x="34" y="26" width="36" height="25" rx="3" fill="${C.fg}"/><rect x="74" y="26" width="36" height="25" rx="3" fill="${C.fg}"/><rect x="34" y="55" width="36" height="25" rx="3" fill="${C.fg}"/><rect x="74" y="55" width="36" height="25" rx="3" fill="${C.fg}"/>`,
	grid: [0, 1, 2].flatMap((r) => [0, 1, 2].map((c) => `<rect x="${32 + c * 28}" y="${24 + r * 19}" width="24" height="16" rx="2" fill="${C.fg}"/>`)).join(""),
	cams: `<rect x="28" y="30" width="40" height="28" rx="5" fill="${C.fg}"/><path d="M70 40 L82 34 V54 L70 48 Z" fill="${C.fg}"/><rect x="62" y="58" width="40" height="28" rx="5" fill="${C.muted}"/><path d="M104 68 L116 62 V82 L104 76 Z" fill="${C.muted}"/>`,
	replay: `<circle cx="72" cy="52" r="26" fill="none" stroke="${C.fg}" stroke-width="7" stroke-dasharray="130 34"/><path d="M40 30 L58 30 L52 46 Z" fill="${C.fg}"/><path d="M64 40 L84 52 L64 64 Z" fill="${C.red}"/>`,
	play: `<path d="M56 28 L96 52 L56 76 Z" fill="${C.fg}"/>`,
	pause: `<rect x="50" y="28" width="15" height="48" rx="3" fill="${C.fg}"/><rect x="79" y="28" width="15" height="48" rx="3" fill="${C.fg}"/>`,
	back: `<path d="M74 28 L36 52 L74 76 Z M112 28 L74 52 L112 76 Z" fill="${C.fg}"/>`,
	forward: `<path d="M32 28 L70 52 L32 76 Z M70 28 L108 52 L70 76 Z" fill="${C.fg}"/>`,
	frameback: `<rect x="40" y="28" width="10" height="48" rx="2" fill="${C.fg}"/><path d="M100 28 L56 52 L100 76 Z" fill="${C.fg}"/>`,
	frameforward: `<path d="M44 28 L88 52 L44 76 Z" fill="${C.fg}"/><rect x="94" y="28" width="10" height="48" rx="2" fill="${C.fg}"/>`,
	restart: `<rect x="34" y="28" width="10" height="48" rx="2" fill="${C.fg}"/><path d="M80 28 L48 52 L80 76 Z M112 28 L80 52 L112 76 Z" fill="${C.fg}"/>`,
	zoomin: `<circle cx="64" cy="48" r="22" fill="none" stroke="${C.fg}" stroke-width="7"/><path d="M80 64 L102 86 M54 48 H74 M64 38 V58" stroke="${C.fg}" stroke-width="7" stroke-linecap="round"/>`,
	zoomout: `<circle cx="64" cy="48" r="22" fill="none" stroke="${C.fg}" stroke-width="7"/><path d="M80 64 L102 86 M54 48 H74" stroke="${C.fg}" stroke-width="7" stroke-linecap="round"/>`,
	follow: `<circle cx="72" cy="52" r="22" fill="none" stroke="${C.fg}" stroke-width="6"/><circle cx="72" cy="52" r="6" fill="${C.red}"/><path d="M72 18 V32 M72 72 V86 M38 52 H52 M92 52 H106" stroke="${C.fg}" stroke-width="6" stroke-linecap="round"/>`,
	angle: `<rect x="26" y="30" width="40" height="28" rx="5" fill="${C.fg}"/><path d="M68 40 L80 34 V54 L68 48 Z" fill="${C.fg}"/><path d="M84 66 H112 M104 58 L112 66 L104 74 M60 82 H32 M40 74 L32 82 L40 90" fill="none" stroke="${C.amber}" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>`,
	live: `<circle cx="34" cy="52" r="11" fill="${C.red}"/>` + text(52, 63, "LIVE", { size: 30 }),
	panic: `<path d="M72 18 L116 88 H28 Z" fill="${C.fg}"/><rect x="67" y="40" width="10" height="28" rx="3" fill="${C.red}"/><circle cx="72" cy="78" r="5" fill="${C.red}"/>`,
	undo: `<path d="M50 40 H86 C102 40 112 50 112 63 C112 76 102 86 86 86 H52" fill="none" stroke="${C.fg}" stroke-width="8" stroke-linecap="round"/><path d="M58 22 L34 40 L58 58 Z" fill="${C.fg}"/>`,
	link: `<rect x="26" y="40" width="50" height="24" rx="12" fill="none" stroke="${C.fg}" stroke-width="7"/><rect x="68" y="40" width="50" height="24" rx="12" fill="none" stroke="${C.fg}" stroke-width="7"/>`,
};

/**
 * A key with an icon (or a big word when it has none), a title and a small line under it: Take, Wide,
 * Cut to cam, Layout, Replay, Break, Panic, Undo and Custom path.
 */
export function labelKey(v) {
	const parts = [];
	const lit = v.lit ? (v.accent === "green" ? C.green : v.accent === "amber" ? C.amber : C.red) : null;
	const bg = v.fill === "red" ? C.red : v.dim ? C.bgDim : C.bg;
	if (v.icon && ICONS[v.icon]) parts.push(`<g${v.dim ? ' opacity="0.4"' : ""}>${ICONS[v.icon]}</g>`);
	else if (v.word) parts.push(bigLabel(v.word, 70, v.dim ? C.muted : C.fg));
	if (v.title) {
		const n = [...String(v.title)].length;
		parts.push(text(72, v.sub ? 112 : 122, fit(v.title, 14), { size: n > 10 ? 16 : n > 8 ? 18 : 22, anchor: "middle", fill: v.dim ? C.muted : C.fg }));
	}
	if (v.sub) parts.push(text(72, 134, fit(v.sub, 15), { size: 14, weight: "normal", anchor: "middle", fill: v.fill === "red" ? C.fg : C.muted }));
	return frame(parts.join(""), { status: v.status, bg, border: lit ?? (v.fill === "red" ? null : C.line), borderWidth: lit ? 8 : 2 });
}

// ---- Stream Deck + dials (the 200 x 100 touch strip under each dial) ---------------------------

const strip = (body, o = {}) => canvas(DIAL_W, DIAL_H, 3, 8, body, o, 5);

/** The Preview dial: the lobby in (or about to go into) Preview. */
export function previewDial(v) {
	const parts = [text(12, 24, "PREVIEW", { size: 14, fill: C.green })];
	if (v.lobby) {
		parts.push(text(12, 74, v.lobby.no ?? "?", { size: 46 }));
		parts.push(text(78, 52, fit(v.lobby.name, 11), { size: 18 }));
		const phase = phaseOf(v.lobby.phase);
		parts.push(text(78, 74, v.lobby.online === false ? "OFFLINE" : phase.label, { size: 14, fill: v.lobby.online === false ? C.muted : phase.fill }));
	} else {
		parts.push(text(12, 66, "empty", { size: 22, weight: "normal", fill: C.muted }));
	}
	parts.push(text(188, 24, v.layout ? String(v.layout).toUpperCase() : "", { size: 13, fill: C.muted, anchor: "end" }));
	parts.push(text(12, 92, "turn: pick · push: TAKE", { size: 12, weight: "normal", fill: C.muted }));
	return strip(parts.join(""), { status: v.status, border: v.lobby ? C.green : C.line, dashed: v.pending });
}

/** The Player dial: who the target lobby's camera follows (or is about to). */
export function playerDial(v) {
	const parts = [text(12, 24, v.lobbyNo ? `CAM L${v.lobbyNo}` : "CAM", { size: 14, fill: C.muted })];
	if (v.player) {
		parts.push(crewmate(14, 32, 0.42, v.player.hex, v.player.dead));
		parts.push(text(70, 56, fit(v.player.name, 10), { size: 20, fill: v.player.dead ? C.muted : C.fg }));
		const note = v.armed ? "ARMED" : v.onAir ? "ON STREAM" : v.following ? "following" : "picked";
		parts.push(text(70, 76, note, { size: 14, fill: v.armed ? C.amber : v.onAir ? C.red : C.muted }));
	} else {
		parts.push(text(70, 60, v.lobbyNo ? "auto" : "no lobby", { size: 20, weight: "normal", fill: C.muted }));
	}
	if (v.noCam) parts.push(text(188, 24, "NO CAM", { size: 13, fill: C.danger, anchor: "end" }));
	parts.push(text(70, 92, "push: CUT · tap: WIDE", { size: 12, weight: "normal", fill: C.muted }));
	return strip(parts.join(""), { status: v.status, border: v.onAir ? C.red : v.armed ? C.amber : C.line, dashed: v.pending });
}

/** The Replay dial: playing or paused, the speed and the newest clip. */
export function replayDial(v) {
	const parts = [text(12, 24, "REPLAY", { size: 14, fill: v.onAir ? C.red : C.muted })];
	parts.push(text(188, 24, `${v.speed ?? 100}%`, { size: 14, fill: C.muted, anchor: "end" }));
	if (v.onAir) {
		parts.push(v.playing ? `<path d="M14 38 L40 52 L14 66 Z" fill="${C.fg}"/>` : `<rect x="14" y="38" width="9" height="28" fill="${C.fg}"/><rect x="29" y="38" width="9" height="28" fill="${C.fg}"/>`);
		parts.push(text(50, 60, v.playing ? "PLAYING" : "PAUSED", { size: 20 }));
	} else {
		parts.push(text(12, 60, v.ready ? `${v.ready} ready` : "no clips", { size: 20, fill: v.ready ? C.fg : C.muted }));
	}
	if (v.last) parts.push(text(12, 78, fit(v.last, 26), { size: 13, weight: "normal", fill: C.muted }));
	parts.push(text(12, 94, "turn: scrub · push: play/pause", { size: 12, weight: "normal", fill: C.muted }));
	return strip(parts.join(""), { status: v.status, border: v.onAir ? C.red : C.line });
}

const DRAW = { lobby: lobbyKey, lobbyStep: lobbyStepKey, cam: camKey, camStep: camStepKey, toggle: toggleKey, previewDial, playerDial, replayDial };

/** Any view, drawn (anything else as a labelled key). */
export function render(view) {
	return (Object.hasOwn(DRAW, view?.type) ? DRAW[view.type] : labelKey)(view ?? {});
}
