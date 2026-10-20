import assert from "node:assert/strict";
import { test } from "node:test";

import { PLUGIN_UUID } from "../src/actions.js";
import { Deck, summary } from "../src/deck.js";
import { sampleState } from "./fixtures.js";

const P = `${PLUGIN_UUID}.`;

/** Timers that only fire when the test says so. */
function fakeTimers() {
	let next = 1;
	const pending = new Map();
	return {
		pending,
		setTimeout(fn, ms) {
			const id = next++;
			pending.set(id, { fn, ms });
			return id;
		},
		clearTimeout(id) {
			pending.delete(id);
		},
		setInterval(fn, ms) {
			const id = next++;
			pending.set(id, { fn, ms, every: true });
			return id;
		},
		clearInterval(id) {
			pending.delete(id);
		},
		/** Runs every waiting timeout once (and each interval once). */
		run() {
			for (const [id, t] of [...pending]) {
				if (!t.every) pending.delete(id);
				t.fn();
			}
		},
	};
}

/** A key handle that records what the Deck did to it. */
function fakeKey() {
	const calls = [];
	return {
		calls,
		images: () => calls.filter((c) => c[0] === "image" || c[0] === "feedback"),
		setImage: (url) => calls.push(["image", url]),
		setFeedback: (payload) => calls.push(["feedback", payload.canvas]),
		showOk: () => calls.push(["ok"]),
		showAlert: () => calls.push(["alert"]),
	};
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

function makeDeck(answer = () => ({ status: 200, ok: true, message: "done" })) {
	const sent = [];
	const timers = fakeTimers();
	let release = null;
	const deck = new Deck({
		send: (path, query) => {
			sent.push({ path, query });
			const res = answer(path, query);
			if (res === "hold") return new Promise((resolve) => (release = (late) => resolve(late ?? { status: 200, ok: true, message: "late" })));
			return Promise.resolve(res);
		},
		timers,
		now: () => 0,
		scrubGap: 320,
	});
	return { deck, sent, timers, release: (late) => release?.(late) };
}

test("a key is drawn when it appears, and again only when its picture changes", () => {
	const { deck } = makeDeck();
	const key = fakeKey();
	deck.appear("k1", key, P + "lobby", { lobby: "3" });
	assert.equal(key.images().length, 1);
	assert.ok(key.images()[0][1].startsWith("data:image/svg+xml;base64,"));
	deck.setState(sampleState(), 0);
	assert.equal(key.images().length, 2, "the state changed the picture");
	deck.setState(sampleState(), 0);
	assert.equal(key.images().length, 2, "the same picture isn't sent again");
	deck.setStatus("offline");
	assert.equal(key.images().length, 3);
	deck.appear("x", fakeKey(), "com.other.plugin.thing", {});
	assert.equal(deck.keys.has("x"), false, "not one of ours");
});

test("a press sends the key's request: a tick when done, the alert when refused", async () => {
	const { deck, sent } = makeDeck((path) => (path === "lobby/9" ? { status: 404, ok: false, message: "No lobby 9." } : { status: 200, ok: true, message: "ok" }));
	deck.setState(sampleState(), 0);
	const good = fakeKey();
	const bad = fakeKey();
	deck.appear("a", good, P + "lobby", { lobby: "3", mode: "cut", t: "swoosh" });
	deck.appear("b", bad, P + "lobby", { lobby: "9", mode: "cut" });
	await deck.keyDown("a");
	await deck.keyDown("b");
	assert.deepEqual(sent, [
		{ path: "lobby/3", query: { t: "swoosh" } },
		{ path: "lobby/9", query: {} },
	]);
	assert.deepEqual(good.calls.at(-1), ["ok"]);
	assert.deepEqual(bad.calls.at(-1), ["alert"]);
	const unset = fakeKey();
	deck.appear("c", unset, P + "custom", { path: "" });
	await deck.keyDown("c");
	assert.equal(sent.length, 2, "a key with nothing to send sends nothing");
	assert.deepEqual(unset.calls.at(-1), ["alert"]);
});

test("select, then submit: a new Lobby key puts its lobby in Preview and the Take key puts it on", async () => {
	const { deck, sent } = makeDeck();
	deck.setState(sampleState(), 0);
	deck.appear("l", fakeKey(), P + "lobby", {});
	deck.appear("c", fakeKey(), P + "cam", {});
	deck.appear("t", fakeKey(), P + "take", {});
	await deck.keyDown("l");
	await deck.keyDown("t");
	await deck.keyDown("c");
	assert.deepEqual(sent, [
		{ path: "pvw/lobby/1", query: {} },
		{ path: "take", query: {} },
		{ path: "cam/pgm/slot1", query: { to: "pvw" } },
	]);
});

test("presses on a key are ignored while its last request is on its way", async () => {
	let calls = 0;
	const d = makeDeck(() => (++calls === 1 ? "hold" : { status: 200, ok: true, message: "" }));
	const key = fakeKey();
	d.deck.appear("k", key, P + "take", {});
	const first = d.deck.keyDown("k");
	await d.deck.keyDown("k");
	await d.deck.keyDown("k");
	assert.equal(d.sent.length, 1);
	d.release();
	await first;
	await d.deck.keyDown("k");
	assert.equal(d.sent.length, 2, "the key works again once the answer came");
});

test("the Preview dial shows a turn at once, stages the lobby when it rests, and push stages it before Take", async () => {
	const d = makeDeck();
	d.deck.setState(sampleState(), 0);
	const dial = fakeKey();
	d.deck.appear("d", dial, P + "dial.preview", {}, true);
	assert.equal(dial.calls[0][0], "feedback", "dials draw on the touch strip");
	d.deck.dialRotate("d", 1);
	d.deck.dialRotate("d", 1);
	assert.equal(d.deck.keys.get("d").pending, 5, "Preview is 1: one notch is 3, two are 5");
	assert.equal(d.sent.length, 0, "nothing sent while it turns");
	d.timers.run();
	await flush();
	assert.deepEqual(d.sent, [{ path: "pvw/lobby/5", query: {} }]);
	d.deck.dialRotate("d", -1);
	await d.deck.keyDown("d");
	assert.deepEqual(d.sent.slice(1), [
		{ path: "pvw/lobby/3", query: {} },
		{ path: "take", query: {} },
	]);
	assert.ok(!dial.calls.some((c) => c[0] === "ok"), "no tick on a dial (Stream Deck + has none)");
	await d.deck.touchTap("d");
	assert.deepEqual(d.sent.at(-1), { path: "pvw/clear", query: {} });
});

test("a push whose dialled lobby Red Alert refuses to stage takes nothing", async () => {
	const d = makeDeck((path) => (path === "pvw/lobby/3" ? { status: 404, ok: false, message: "No lobby 3." } : { status: 200, ok: true, message: "ok" }));
	d.deck.setState(sampleState(), 0);
	const dial = fakeKey();
	d.deck.appear("d", dial, P + "dial.preview", {}, true);
	d.deck.dialRotate("d", 1);
	await d.deck.keyDown("d");
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3"], "Preview still holds the old lobby: no Take");
	assert.deepEqual(dial.calls.at(-1), ["alert"]);
	d.timers.run();
	assert.equal(d.deck.keys.get("d").pending, null, "the strip goes back to what's in Preview");
	await d.deck.keyDown("d");
	assert.deepEqual(d.sent.at(-1), { path: "take", query: {} }, "with nothing waiting, push takes");
});

test("a push after the dialled lobby was already refused takes nothing while the strip still shows it", async () => {
	const d = makeDeck((path) => (path === "pvw/lobby/3" ? { status: 404, ok: false, message: "No lobby 3." } : { status: 200, ok: true, message: "ok" }));
	d.deck.setState(sampleState(), 0);
	const dial = fakeKey();
	d.deck.appear("d", dial, P + "dial.preview", {}, true);
	// Turn, rest (the stage goes and is refused at once), then push a moment later: the usual gesture.
	d.deck.dialRotate("d", 1);
	d.timers.run();
	await flush();
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3"]);
	assert.equal(d.deck.keys.get("d").committing, null, "the stage has been answered");
	assert.equal(d.deck.keys.get("d").pending, 3, "the strip still shows the dialled lobby");
	const alerts = dial.calls.filter((c) => c[0] === "alert").length;
	await d.deck.keyDown("d");
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3"], "the old Preview isn't taken");
	assert.equal(dial.calls.filter((c) => c[0] === "alert").length, alerts + 1, "the push shows the alert too");
	// A new turn is a new choice: staged and taken as usual.
	d.deck.dialRotate("d", 1);
	assert.equal(d.deck.keys.get("d").staged, null);
	await d.deck.keyDown("d");
	assert.deepEqual(d.sent.map((s) => s.path).slice(1), ["pvw/lobby/5", "take"]);
	// Once the strip goes back to Red Alert's Preview, push takes what's there.
	d.timers.run();
	assert.equal(d.deck.keys.get("d").staged, null);
	await d.deck.keyDown("d");
	assert.deepEqual(d.sent.at(-1), { path: "take", query: {} });
});

test("a push while the dialled lobby is still being staged waits for it, and takes nothing if it was refused", async () => {
	const d = makeDeck((path) => (path.startsWith("pvw/lobby/") ? "hold" : { status: 200, ok: true, message: "ok" }));
	d.deck.setState(sampleState(), 0);
	d.deck.appear("d", fakeKey(), P + "dial.preview", {}, true);
	d.deck.dialRotate("d", 1);
	d.timers.run();
	await flush();
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3"]);
	d.deck.dialRotate("d", 1);
	d.timers.run();
	await flush();
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3"], "the next stage waits for the one on its way");
	d.release();
	await flush();
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3", "pvw/lobby/5"]);
	const push = d.deck.keyDown("d");
	await flush();
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3", "pvw/lobby/5"], "Take waits for the stage");
	d.release();
	await push;
	assert.deepEqual(d.sent.map((s) => s.path), ["pvw/lobby/3", "pvw/lobby/5", "take"]);

	d.deck.dialRotate("d", -1);
	d.timers.run();
	await flush();
	const refused = d.deck.keyDown("d");
	await flush();
	d.release({ status: 404, ok: false, message: "No lobby 3." });
	await refused;
	assert.deepEqual(d.sent.map((s) => s.path).slice(3), ["pvw/lobby/3"], "the stage was refused: no Take");
	assert.equal(d.deck.keys.get("d").committing, null);
});

test("the Player dial arms who the camera follows when it rests, and push cuts to them", async () => {
	const d = makeDeck();
	d.deck.setState(sampleState(), 0);
	d.deck.appear("p", fakeKey(), P + "dial.player", { target: "lobby", lobbyNo: "3" }, true);
	d.deck.dialRotate("p", 1);
	d.timers.run();
	await flush();
	assert.deepEqual(d.sent, [{ path: "cam/3/slot3", query: { arm: "1" } }]);
	d.deck.dialRotate("p", 1);
	await d.deck.keyDown("p");
	assert.deepEqual(d.sent.slice(1), [{ path: "cam/3/slot4", query: {} }], "push carries the choice itself: no arm first");
	d.timers.run();
	assert.equal(d.deck.keys.get("p").pending, null, "Red Alert's state takes over again");
});

test("the Replay dial scrubs one step at a time, at least 320 ms apart, and stops when nothing plays", async () => {
	let playing = true;
	const d = makeDeck(() => (playing ? { status: 200, ok: true, message: "" } : { status: 409, ok: false, message: "No replay on stream." }));
	d.deck.appear("r", fakeKey(), P + "dial.replay", {}, true);
	d.deck.dialRotate("r", 1);
	d.deck.dialRotate("r", 1);
	await flush();
	assert.deepEqual(d.sent.map((s) => s.path), ["replay/frameforward"]);
	assert.equal([...d.timers.pending.values()].at(-1).ms, 320);
	d.timers.run();
	await flush();
	assert.deepEqual(d.sent.map((s) => s.path), ["replay/frameforward", "replay/frameforward"]);
	d.timers.run();
	await flush();
	assert.equal(d.sent.length, 2, "all turns sent");
	d.deck.dialRotate("r", -4);
	await flush();
	assert.equal(d.sent.at(-1).path, "replay/back", "a fast turn jumps");
	d.timers.run();
	await flush();
	playing = false;
	const before = d.sent.length;
	d.deck.dialRotate("r", 1);
	d.deck.dialRotate("r", 1);
	await flush();
	d.timers.run();
	await flush();
	d.timers.run();
	await flush();
	assert.equal(d.sent.length, before + 1, "after a refused step the rest of the turn is dropped");
	assert.equal(d.deck.keys.get("r").scrubbing, false);
});

test("a sabotage makes the keys tick twice a second, and stops when it's over", () => {
	const { deck, timers } = makeDeck();
	deck.appear("k", fakeKey(), P + "lobby", { lobby: "3" });
	deck.setState(sampleState(), 0);
	assert.ok([...timers.pending.values()].some((t) => t.every && t.ms === 500));
	const calm = sampleState();
	for (const l of calm.lobbies) l.sabotage = null;
	deck.setState(calm, 0);
	assert.ok(![...timers.pending.values()].some((t) => t.every));
});

test("new settings forget a half-made dial choice and redraw", () => {
	const { deck } = makeDeck();
	deck.setState(sampleState(), 0);
	const key = fakeKey();
	deck.appear("d", key, P + "dial.preview", {}, true);
	deck.dialRotate("d", 1);
	const before = key.images().length;
	deck.settings("d", { as: "quad" });
	assert.equal(deck.keys.get("d").pending, null);
	assert.equal(key.images().length, before + 1);
	deck.disappear("d");
	assert.equal(deck.keys.size, 0);
});

test("the settings panel's summary has names and colours, never impostor flags", () => {
	const s = sampleState();
	s.lobbies[1].players[0].imp = true;
	const out = summary(s, "ok");
	assert.deepEqual(out.lobbies.map((l) => l.no), [1, 3, 4, 5]);
	assert.deepEqual(out.lobbies[1].players[0], { slot: 1, colour: "red", name: "Ann", dead: false });
	assert.ok(!JSON.stringify(out).includes("imp"));
	assert.deepEqual(summary(null, "nokey"), { status: "nokey", lobbies: [] });
});
