// Writes the plugin's pictures into the .sdPlugin folder: the plugin icon (PNG, drawn by png.mjs),
// the category and action-list icons (small white SVGs, as Stream Deck's list wants), and each key's
// first picture before Red Alert's state arrives (drawn with the same code as the live keys).
// `npm run build` runs it; the pictures aren't kept in the repository.

import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { camKey, labelKey, lobbyKey, toggleKey } from "../src/svg.js";
import { logoPng } from "./png.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "com.ljbutton.redalert.sdPlugin");

function write(path, content) {
	const full = join(root, path);
	mkdirSync(dirname(full), { recursive: true });
	writeFileSync(full, content);
}

const W = "#ffffff";
const small = (body, size = 20) => `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 20 20">${body}</svg>`;

/** The action list's icons: white on nothing, 20 × 20. */
const ICONS = {
	lobby: `<rect x="2" y="4" width="16" height="12" rx="2" fill="none" stroke="${W}" stroke-width="2"/><rect x="5" y="7" width="4" height="6" fill="${W}"/>`,
	cam: `<path d="M5 17 V9 C5 5 7.5 3 10 3 C12.5 3 15 5 15 9 V17 H11 V14 H9 V17 Z" fill="${W}"/><rect x="9" y="6" width="7" height="4" rx="2" fill="#000000" opacity="0.6"/>`,
	take: `<rect x="1.5" y="6" width="7" height="8" rx="1" fill="none" stroke="${W}" stroke-width="1.6"/><path d="M9.5 10 H13 M11.5 8 L13.5 10 L11.5 12" fill="none" stroke="${W}" stroke-width="1.6"/><rect x="14" y="6" width="5" height="8" rx="1" fill="${W}"/>`,
	wide: `<path d="M3 7 V3 H7 M13 3 H17 V7 M17 13 V17 H13 M7 17 H3 V13" fill="none" stroke="${W}" stroke-width="2"/>`,
	camcut: `<rect x="2" y="6" width="11" height="8" rx="1.5" fill="${W}"/><path d="M13 9 L18 6 V14 L13 11 Z" fill="${W}"/>`,
	layout: `<rect x="2" y="3" width="7" height="6" fill="${W}"/><rect x="11" y="3" width="7" height="6" fill="${W}"/><rect x="2" y="11" width="7" height="6" fill="${W}"/><rect x="11" y="11" width="7" height="6" fill="${W}"/>`,
	replay: `<circle cx="10" cy="10" r="6.5" fill="none" stroke="${W}" stroke-width="2" stroke-dasharray="32 9"/><path d="M8 7 L13 10 L8 13 Z" fill="${W}"/>`,
	toggle: `<rect x="2" y="6" width="16" height="8" rx="4" fill="none" stroke="${W}" stroke-width="2"/><circle cx="14" cy="10" r="2.5" fill="${W}"/>`,
	break: `<rect x="4" y="7" width="9" height="9" rx="1.5" fill="${W}"/><path d="M13 9 H15 C17 9 17 13 15 13 H13" fill="none" stroke="${W}" stroke-width="1.6"/><path d="M6 2 V5 M9 2 V5" stroke="${W}" stroke-width="1.4"/>`,
	panic: `<path d="M10 2 L18.5 17 H1.5 Z" fill="${W}"/><rect x="9.1" y="7" width="1.8" height="5.5" fill="#000000"/><rect x="9.1" y="13.6" width="1.8" height="1.8" fill="#000000"/>`,
	custom: `<rect x="2" y="7" width="9" height="6" rx="3" fill="none" stroke="${W}" stroke-width="1.8"/><rect x="9" y="7" width="9" height="6" rx="3" fill="none" stroke="${W}" stroke-width="1.8"/>`,
	"dial-preview": `<circle cx="10" cy="10" r="7.5" fill="none" stroke="${W}" stroke-width="2"/><rect x="7" y="7.5" width="6" height="5" rx="1" fill="${W}"/>`,
	"dial-player": `<circle cx="10" cy="10" r="7.5" fill="none" stroke="${W}" stroke-width="2"/><path d="M7 14 V9.5 C7 7.5 8.5 6 10 6 C11.5 6 13 7.5 13 9.5 V14 Z" fill="${W}"/>`,
	"dial-replay": `<circle cx="10" cy="10" r="7.5" fill="none" stroke="${W}" stroke-width="2"/><path d="M8 6.5 L13.5 10 L8 13.5 Z" fill="${W}"/>`,
};

/** Each key's first picture. */
const KEYS = {
	lobby: lobbyKey({ no: 1, name: "Lobby", phase: "lobby", online: true }),
	cam: camKey({ lobbyNo: null, slotLabel: "#1", player: { name: "Player", hex: "#c51111", dead: false } }),
	take: labelKey({ icon: "take", title: "TAKE" }),
	wide: labelKey({ icon: "wide", title: "WIDE" }),
	camcut: labelKey({ icon: "cam", title: "CUT TO CAM" }),
	layout: labelKey({ icon: "quad", title: "LAYOUT" }),
	replay: labelKey({ icon: "replay", title: "REPLAY" }),
	toggle: toggleKey({ label: "TOGGLE", on: null }),
	break: labelKey({ word: "BRB", title: "BREAK" }),
	panic: labelKey({ icon: "panic", title: "PANIC", fill: "red" }),
	custom: labelKey({ icon: "link", title: "CUSTOM" }),
	"dial-preview": labelKey({ icon: "take", title: "PREVIEW" }),
	"dial-player": camKey({ lobbyNo: null, slotLabel: "", player: { name: "Player", hex: "#132ed1", dead: false } }),
	"dial-replay": labelKey({ icon: "replay", title: "REPLAY" }),
};

write("imgs/plugin/icon.png", logoPng(256));
write("imgs/plugin/icon@2x.png", logoPng(512));
const category = `<path d="M10 2 V4.5 M3.5 5 L5.2 6.7 M16.5 5 L14.8 6.7 M1.5 11 H4 M18.5 11 H16" stroke="${W}" stroke-width="1.6" stroke-linecap="round"/><path d="M5.5 16 V11.5 C5.5 9 7.5 7 10 7 C12.5 7 14.5 9 14.5 11.5 V16 Z" fill="${W}"/><rect x="3.5" y="16" width="13" height="2.5" rx="0.8" fill="${W}"/>`;
write("imgs/plugin/category.svg", small(category, 28));
for (const [name, body] of Object.entries(ICONS)) write(`imgs/actions/${name}.svg`, small(body));
for (const [name, markup] of Object.entries(KEYS)) write(`imgs/keys/${name}.svg`, markup);

console.log(`Pictures written to ${root}/imgs`);
