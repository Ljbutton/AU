// Reading Red Alert's Stream Deck state (GET /deck/state): which lobby a key is about, which player
// a camera key shows, and what the toggles are set to. Plain functions with no Stream Deck or network
// code, so the tests can feed them a state document and check the answers.

/** The colours by the game's colour number, as Red Alert's links name them (BroadcastDeck.cs Colours). */
export const COLOURS = ["red", "blue", "green", "pink", "orange", "yellow", "black", "white", "purple", "brown", "cyan", "lime", "maroon", "rose", "banana", "gray", "tan", "coral"];

/** The crewmate colours by colour number, the same hex values Red Alert's own pages draw with. */
export const CREW = ["#c51111", "#132ed1", "#117f2d", "#ed54ba", "#ef7d0d", "#f5f557", "#3f474e", "#d6e0f0", "#6b2fbb", "#71491e", "#38fedc", "#50ef39", "#5f1d2e", "#ecc0d3", "#f0e7a8", "#758593", "#918877", "#d76464"];

const same = (a, b) => typeof a === "string" && typeof b === "string" && a.toLowerCase() === b.toLowerCase();

/** The lobbies in the state, or none (a state from before Red Alert had lobbies, or no state yet). */
export function lobbies(state) {
	return Array.isArray(state?.lobbies) ? state.lobbies : [];
}

/** The online lobbies by number: the order next/prev and the dials step through. */
export function onlineLobbies(state) {
	return lobbies(state).filter((l) => l && l.online !== false && Number.isFinite(l.no)).sort((a, b) => a.no - b.no);
}

export function lobbyByNo(state, no) {
	const n = Number(no);
	return Number.isFinite(n) ? lobbies(state).find((l) => l.no === n) ?? null : null;
}

export function lobbyByName(state, name) {
	return name ? lobbies(state).find((l) => same(l.name, name)) ?? null : null;
}

function firstSlot(part) {
	return Array.isArray(part?.slots) ? part.slots.find((s) => typeof s === "string" && s.length > 0) ?? null : null;
}

/** The lobby on Program (the first one on stream), by its slot or else by its red tally. */
export function programLobby(state) {
	return lobbyByName(state, firstSlot(state?.program)) ?? lobbies(state).find((l) => l.tally === "pgm") ?? null;
}

/** The lobby in Preview (the first one staged), by its slot or else by its green tally. */
export function previewLobby(state) {
	return lobbyByName(state, firstSlot(state?.preview)) ?? lobbies(state).find((l) => l.tally === "pvw") ?? null;
}

/** A key's target lobby: "pgm", "pvw" or a lobby number. */
export function targetLobby(state, target) {
	if (target === "pgm" || target === undefined || target === null || target === "") return programLobby(state);
	if (target === "pvw") return previewLobby(state);
	return lobbyByNo(state, target);
}

/**
 * The online lobby `step` places on from lobby number `fromNo`, wrapping
 * round. With no starting lobby (or one that went offline) it starts from the first or the last.
 */
export function stepLobby(state, fromNo, step) {
	const list = onlineLobbies(state);
	return stepFrom(list, list.findIndex((l) => l.no === Number(fromNo)), step);
}

/** The item `step` places on from index `at` (wrapping round); from the first or the last when `at` is -1. */
function stepFrom(list, at, step) {
	if (list.length === 0) return null;
	if (at < 0) return step >= 0 ? list[0] : list[list.length - 1];
	const n = list.length;
	return list[(((at + step) % n) + n) % n];
}

/** The players of a lobby by slot (living and dead, in colour order, as Red Alert numbers them). */
export function players(lobby) {
	return Array.isArray(lobby?.players) ? [...lobby.players].sort((a, b) => (a.slot ?? 0) - (b.slot ?? 0)) : [];
}

export function livingPlayers(lobby) {
	return players(lobby).filter((p) => !p.dead);
}

/** The player a camera key names: "slotK" (or a bare number K) or a colour name. */
export function findPlayer(lobby, who) {
	if (!lobby || who === undefined || who === null) return null;
	const text = String(who).trim().toLowerCase();
	const slot = /^(?:slot)?(\d{1,2})$/.exec(text);
	if (slot) return players(lobby).find((p) => p.slot === Number(slot[1])) ?? null;
	const colour = COLOURS.indexOf(text);
	return players(lobby).find((p) => same(p.colour, text) || (colour >= 0 && p.color === colour)) ?? null;
}

export function playerById(lobby, id) {
	return id === null || id === undefined ? null : players(lobby).find((p) => p.id === id) ?? null;
}

/**
 * The living player `step` places on from player `fromId` (wrapping),
 * for the player dial. It starts from the first (or last) when the camera follows nobody living.
 */
export function stepPlayer(lobby, fromId, step) {
	const list = livingPlayers(lobby);
	return stepFrom(list, list.findIndex((p) => p.id === fromId), step);
}

/** Who a lobby's camera is set to: the armed or picked player first, else who it follows now. */
export function camChoice(lobby) {
	const cam = lobby?.cam ?? {};
	return playerById(lobby, cam.pick ?? null) ?? playerById(lobby, cam.following ?? null);
}

/** The hex colour a player is drawn in. */
export function playerHex(player) {
	if (!player) return "#4a4a54";
	if (Number.isInteger(player.color) && CREW[player.color]) return CREW[player.color];
	const i = COLOURS.indexOf(String(player.colour ?? "").toLowerCase());
	return i >= 0 ? CREW[i] : "#4a4a54";
}

/** Red Alert's layout names, folded to the five the Layout key offers. */
export function normLayout(layout) {
	const l = String(layout ?? "").toLowerCase();
	if (l === "4up" || l === "quad") return "quad";
	if (l === "cam" || l === "cams") return "cams";
	if (l === "2up" || l === "full" || l === "grid") return l;
	return l || "none";
}

/** True when this lobby's player camera is on stream now (a cam layout with the lobby in it). */
export function camOnProgram(state, lobby) {
	if (!lobby || normLayout(state?.program?.layout) !== "cams") return false;
	const slots = Array.isArray(state?.program?.slots) ? state.program.slots : [];
	return slots.some((s) => same(s, lobby.name));
}

/** Seconds left on a lobby's sabotage, counted down locally since the state arrived; null when none. */
export function sabotageLeft(lobby, receivedAt, now) {
	const s = lobby?.sabotage;
	if (!s || !Number.isFinite(s.left)) return null;
	const left = s.left - Math.max(0, (now - receivedAt) / 1000);
	return left > 0 ? left : 0;
}

/**
 * Whether a toggle is on, from the state: null when the state doesn't say (so the key shows "?"
 * rather than a guess).
 */
export function toggleOn(state, which) {
	if (!state || !Object.hasOwn(TOGGLE_STATE, which)) return null;
	const on = TOGGLE_STATE[which](state);
	return typeof on === "boolean" ? on : null;
}

/** Where each toggle's state is in /deck/state (alerts are on while they aren't paused). */
const TOGGLE_STATE = {
	mute: (s) => s.voice?.muteAll,
	cue: (s) => s.voice?.cue,
	hold: (s) => s.hold,
	autogrid: (s) => s.auto?.grid,
	autoswitch: (s) => s.auto?.switch,
	standings: (s) => s.graphics?.standings,
	alerts: (s) => (typeof s.graphics?.alertsPaused === "boolean" ? !s.graphics.alertsPaused : null),
};

/** A name cut to fit a key: whole characters (emoji included), with an ellipsis when cut. */
export function fit(text, max) {
	const chars = [...String(text ?? "").replace(/[\u0000-\u001f\u007f]/g, "").trim()];
	return chars.length <= max ? chars.join("") : chars.slice(0, Math.max(1, max - 1)).join("") + "…";
}
