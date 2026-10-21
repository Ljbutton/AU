import assert from "node:assert/strict";
import { test } from "node:test";

import { ACTIONS, settingsOf } from "../src/actions.js";
import { C, camKey, camStepKey, crewmate, dataUrl, esc, labelKey, lobbyKey, render, shade, statusStrip, toggleKey } from "../src/svg.js";
import { sampleState } from "./fixtures.js";

/** Checks the markup is well-formed XML: every tag closed in order, attributes quoted, entities known. */
function assertWellFormed(markup) {
	const stack = [];
	const tag = /<(\/?)([a-zA-Z][\w:-]*)((?:\s+[\w:-]+="[^"<]*")*)\s*(\/?)>/g;
	let at = 0;
	let m;
	while ((m = tag.exec(markup))) {
		const between = markup.slice(at, m.index);
		assert.ok(!between.includes("<") && !between.includes(">"), `stray bracket near: ${between.slice(0, 40)}`);
		assert.ok(!/&(?!amp;|lt;|gt;|quot;|#39;)/.test(between), `bad entity in: ${between.slice(0, 40)}`);
		at = tag.lastIndex;
		if (m[1]) assert.equal(stack.pop(), m[2], `closing ${m[2]}`);
		else if (!m[4]) stack.push(m[2]);
	}
	assert.equal(markup.slice(at), "", "nothing after the last tag");
	assert.deepEqual(stack, [], "every tag closed");
}

/** Stream Deck draws SVG with Qt (SVG Tiny): no CSS, filters, clip paths, masks or arcs in paths. */
function assertQtSafe(markup) {
	for (const bad of ["<style", "style=", "filter", "clipPath", "mask", "dominant-baseline", "letter-spacing", "<foreignObject", "<image"]) assert.ok(!markup.includes(bad), `uses ${bad}`);
	for (const d of markup.matchAll(/ d="([^"]*)"/g)) assert.ok(!/[Aa]/.test(d[1]), `arc in path ${d[1]}`);
	assert.match(markup, /^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" width="\d+" height="\d+" viewBox="0 0 \d+ \d+">/);
}

const ctx = (o = {}) => ({ status: "ok", receivedAt: 0, now: 0, ...o });

test("every action's key art, in every status, is well-formed SVG Qt can draw", () => {
	const s = sampleState();
	for (const status of ["ok", "offline", "badkey", "nokey", "old", "locked"]) {
		for (const a of ACTIONS) {
			for (const settings of [{}, { lobby: "3" }, { lobby: "next" }, { lobby: "action" }, { op: "best" }, { who: "next" }, { who: "next", mode: "preview" }, { which: "alerts" }, { op: "slow" }, { target: "lobby", lobbyNo: "3" }, { path: "a/b", label: "<Hi & bye>" }]) {
				for (const state of [s, null]) {
					const markup = render(a.view(settingsOf(a, settings), state, ctx({ status }), a.dial ? 3 : null));
					assertWellFormed(markup);
					assertQtSafe(markup);
				}
			}
		}
	}
});

test("names from players are escaped", () => {
	assert.equal(esc(`<b>"Tom" & 'Jerry'</b>`), "&lt;b&gt;&quot;Tom&quot; &amp; &#39;Jerry&#39;&lt;/b&gt;");
	const markup = camKey({ lobbyNo: 3, slotLabel: "#1", player: { name: "<script>", hex: "#c51111", dead: false } });
	assert.ok(!markup.includes("<script>"));
	assert.ok(markup.includes("&lt;script&gt;"));
	assertWellFormed(markup);
});

test("the data URL is base64 SVG that decodes back to the markup", () => {
	const markup = labelKey({ title: "TAKE", icon: "take" });
	const url = dataUrl(markup);
	assert.ok(url.startsWith("data:image/svg+xml;base64,"));
	assert.equal(Buffer.from(url.split(",")[1], "base64").toString("utf8"), markup);
	assert.equal(Buffer.from(dataUrl("é").split(",")[1], "base64").toString("utf8"), "é");
});

test("a lobby key has a red border on Program and a green one in Preview", () => {
	const pgm = lobbyKey({ no: 3, name: "Lobby 3", phase: "ingame", online: true, tally: "pgm", crew: 4, imps: 1, tasks: 72.6 });
	assert.ok(pgm.includes(`stroke="${C.red}" stroke-width="8"`));
	assert.ok(pgm.includes(">PGM<"));
	assert.ok(pgm.includes(">4v1<"));
	assert.ok(pgm.includes(">73%<"));
	assert.ok(pgm.includes(">IN PLAY<"));
	const pvw = lobbyKey({ no: 1, name: "Lobby 1", phase: "meeting", online: true, tally: "pvw" });
	assert.ok(pvw.includes(`stroke="${C.green}" stroke-width="8"`));
	assert.ok(pvw.includes(">MEETING<"));
	const none = lobbyKey({ no: 2, name: "Lobby 2", phase: "lobby", online: true, tally: "none" });
	assert.ok(!none.includes(`stroke="${C.red}"`) && !none.includes(`stroke="${C.green}"`));
});

test("a sabotage shows its system and seconds, flashing between two reds", () => {
	const on = lobbyKey({ no: 3, phase: "ingame", online: true, sabotage: { system: "Reactor", left: 20.2 }, flash: true });
	const off = lobbyKey({ no: 3, phase: "ingame", online: true, sabotage: { system: "Reactor", left: 20.2 }, flash: false });
	assert.ok(on.includes(">REACTOR 21<"));
	assert.ok(on.includes(`fill="${C.red}"`));
	assert.ok(off.includes(`fill="${C.redDark}"`));
	assert.notEqual(on, off);
});

test("an offline lobby says so and has no tally or health", () => {
	const markup = lobbyKey({ no: 4, name: "Lobby 4", phase: "menu", online: false, health: "red" });
	assert.ok(markup.includes(">OFFLINE<"));
	assert.ok(!markup.includes("<circle"));
	assert.ok(lobbyKey({ no: 9, missing: true }).includes(">NO LOBBY<"));
});

test("a camera key: crewmate in colour, ring when followed (red on stream), ARMED, dead dimmed", () => {
	const player = { name: "Ann", hex: "#c51111", dead: false };
	const followed = camKey({ lobbyNo: 3, slotLabel: "#1", player, ring: "on" });
	assert.ok(followed.includes('fill="#c51111"'));
	assert.ok(followed.includes(`<circle cx="72" cy="66" r="48" fill="none" stroke="${C.fg}"`));
	assert.ok(followed.includes(">Ann<") && followed.includes(">L3<") && followed.includes(">#1<"));
	assert.ok(camKey({ lobbyNo: 3, player, ring: "pgm" }).includes(`r="48" fill="none" stroke="${C.red}"`));
	assert.ok(!camKey({ lobbyNo: 3, player }).includes('r="48"'));
	assert.ok(camKey({ lobbyNo: 3, player, armed: true }).includes(">ARMED<"));
	const dead = camKey({ lobbyNo: 3, player: { ...player, dead: true } });
	assert.ok(dead.includes('opacity="0.3"') && dead.includes(">DEAD<"));
	assert.ok(camKey({ lobbyNo: 3, player: null }).includes(">EMPTY<"));
	assert.ok(camKey({ lobbyNo: 3, player, noCam: true }).includes(">NO CAM<"));
});

test("a camera step key: green into Preview (the default), amber armed, plain for a cut", () => {
	const v = { lobbyNo: 3, label: "NEXT", current: { name: "Ann", hex: "#c51111", dead: false } };
	assert.ok(camStepKey({ ...v, mode: "preview" }).includes(`stroke="${C.green}" stroke-width="4"`));
	assert.ok(camStepKey({ ...v, mode: "arm" }).includes(`stroke="${C.amber}" stroke-width="4"`));
	const cut = camStepKey({ ...v, mode: "cut" });
	assert.ok(!cut.includes(`stroke="${C.green}"`) && !cut.includes(`stroke="${C.amber}"`));
});

test("the Best replay key and the Take key with a replay in Preview", () => {
	const best = labelKey({ icon: "replay", title: "BEST REPLAY", sub: "Lobby 2: kill", accent: "green" });
	assert.ok(best.includes(">BEST REPLAY<") && best.includes(">Lobby 2: kill<"));
	assertWellFormed(best);
	const take = labelKey({ icon: "take", title: "TAKE", sub: "REPLAY L3", accent: "green", lit: true });
	assert.ok(take.includes(">TAKE<") && take.includes(">REPLAY L3<") && take.includes(`stroke="${C.green}" stroke-width="8"`));
	assertQtSafe(take);
});

test("a toggle shows its state in words and colour, and ? when unknown", () => {
	const on = toggleKey({ label: "MUTE ALL", on: true, onText: "MUTED", onColour: C.red });
	assert.ok(on.includes(">MUTED<") && on.includes(`fill="${C.red}"`));
	const off = toggleKey({ label: "AUTO GRID", on: false });
	assert.ok(off.includes(">OFF<") && off.includes(">AUTO<") && off.includes(">GRID<"));
	assert.ok(toggleKey({ label: "HOLD", on: null }).includes(">?<"));
	assert.ok(toggleKey({ label: "ALERTS", on: false, offText: "PAUSED", offColour: C.amber }).includes(`fill="${C.amber}"`));
});

test("the status strip names the problem and fades the stale picture", () => {
	assert.equal(statusStrip("ok", 144), "");
	assert.ok(statusStrip("offline", 144).includes(">OFFLINE<"));
	assert.ok(statusStrip("badkey", 144).includes(">WRONG KEY<"));
	assert.ok(statusStrip("nokey", 200).includes(">SET UP<"));
	const markup = labelKey({ title: "WIDE", icon: "wide", status: "offline" });
	assert.ok(markup.includes('<g opacity="0.45">'));
	assert.ok(!labelKey({ title: "WIDE", icon: "wide", status: "ok" }).includes('opacity="0.45"'));
});

test("dial strips are 200 x 100", () => {
	const s = sampleState();
	for (const a of ACTIONS.filter((x) => x.dial)) assert.match(render(a.view(settingsOf(a, {}), s, ctx(), null)), /width="200" height="100"/);
	for (const a of ACTIONS.filter((x) => !x.dial)) assert.match(render(a.view(settingsOf(a, {}), s, ctx(), null)), /width="144" height="144"/);
});

test("the replay dial's lines stay clear of the strip's border", () => {
	const s = sampleState();
	const a = ACTIONS.find((x) => x.dial && /replay/.test(x.uuid));
	const markup = render(a.view(settingsOf(a, {}), s, ctx(), null));
	assert.ok(markup.includes(">turn: scrub · push: play/pause<"));
	// The border's inner edge is at y 94.5: a 12 px line's descenders end about 3 px under its baseline.
	for (const m of markup.matchAll(/<text x="\d+" y="(\d+)"[^>]*font-size="(\d+)"/g)) assert.ok(+m[1] + +m[2] / 4 <= 94, `a line at y ${m[1]} (size ${m[2]}) touches the border`);
});

test("helpers: shade and the crewmate", () => {
	assert.equal(shade("#808080", 0.5), "#404040");
	assert.equal(shade("#ffffff", 2), "#ffffff");
	assert.equal(shade("nope", 0.5), "nope");
	assert.ok(crewmate(0, 0, 1, "#132ed1").includes('fill="#132ed1"'));
	assert.ok(crewmate(0, 0, 1, "#132ed1", true).includes('opacity="0.3"'));
});
