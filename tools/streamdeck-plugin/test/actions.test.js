import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

import { ACTIONS, BY_UUID, PLUGIN_UUID, TOGGLES, camCutRequest, customRequest, scrubStep, settingsOf } from "../src/actions.js";
import { sampleState } from "./fixtures.js";

const P = `${PLUGIN_UUID}.`;
const req = (name, settings, state = sampleState(), pending = null) => {
	const def = BY_UUID[P + name];
	return def.request(settingsOf(def, settings), state, pending);
};
const view = (name, settings, state = sampleState(), ctx = { status: "ok", receivedAt: 0, now: 0 }, pending = null) => {
	const def = BY_UUID[P + name];
	return def.view(settingsOf(def, settings), state, ctx, pending);
};

test("every action in the manifest is handled, and every handled action is in the manifest", () => {
	const manifest = JSON.parse(readFileSync(new URL("../com.ljbutton.redalert.sdPlugin/manifest.json", import.meta.url), "utf8"));
	assert.equal(manifest.UUID, PLUGIN_UUID);
	assert.equal(manifest.SDKVersion, 2);
	assert.equal(manifest.Nodejs.Version, "20");
	assert.deepEqual(manifest.Actions.map((a) => a.UUID).sort(), ACTIONS.map((a) => a.uuid).sort());
	for (const a of manifest.Actions) {
		const dial = Boolean(BY_UUID[a.UUID].dial);
		assert.deepEqual(a.Controllers, dial ? ["Encoder"] : ["Keypad"], a.UUID);
		if (dial) assert.equal(a.Encoder.layout, "layouts/canvas.json");
	}
});

test("the Property Inspector starts each action from the same defaults as the plugin", () => {
	const html = readFileSync(new URL("../com.ljbutton.redalert.sdPlugin/ui/inspector.html", import.meta.url), "utf8");
	const block = html.split("/* DEFAULTS-START */")[1].split("/* DEFAULTS-END */")[0];
	const json = block.slice(block.indexOf("{"), block.lastIndexOf("}") + 1);
	const defaults = JSON.parse(json);
	for (const a of ACTIONS) assert.deepEqual(defaults[a.uuid.slice(P.length)], a.defaults, a.uuid);
	assert.equal(Object.keys(defaults).length, ACTIONS.length);
});

test("Lobby: cut by number, next, prev and the action, with a transition", () => {
	assert.deepEqual(req("lobby", { lobby: "3" }), { path: "lobby/3", query: {} });
	assert.deepEqual(req("lobby", { lobby: " 12 " }), { path: "lobby/12", query: {} });
	assert.deepEqual(req("lobby", { lobby: "next", t: "swoosh" }), { path: "lobby/next", query: { t: "swoosh" } });
	assert.deepEqual(req("lobby", { lobby: "prev", t: "cut" }), { path: "lobby/prev", query: { t: "cut" } });
	assert.deepEqual(req("lobby", { lobby: "action", t: "bogus" }), { path: "lobby/action", query: {} });
	assert.ok(req("lobby", { lobby: "zero" }).error);
});

test("Lobby in preview mode stages it, stepping next/prev from Preview", () => {
	assert.deepEqual(req("lobby", { lobby: "4", mode: "preview" }), { path: "pvw/lobby/4", query: {} });
	assert.deepEqual(req("lobby", { lobby: "next", mode: "preview" }), { path: "pvw/lobby/3", query: {} }, "Preview is lobby 1; 4 is offline");
	assert.deepEqual(req("lobby", { lobby: "prev", mode: "preview" }), { path: "pvw/lobby/5", query: {} });
	assert.ok(req("lobby", { lobby: "action", mode: "preview" }).error);
	assert.ok(req("lobby", { lobby: "next", mode: "preview" }, { lobbies: [] }).error);
	assert.deepEqual(req("lobby", { lobby: "5", mode: "add" }), { path: "pvw/add/5", query: {} }, "add builds a 2-up or quad in Preview");
	assert.ok(req("lobby", { lobby: "next", mode: "add" }).error);
	assert.equal(view("lobby", { lobby: "prev", mode: "add" }).mode, "preview");
});

test("Player cam: every target and who, cut or arm", () => {
	assert.deepEqual(req("cam", {}), { path: "cam/pgm/slot1", query: {} });
	assert.deepEqual(req("cam", { target: "pvw", who: "slot", slot: "15" }), { path: "cam/pvw/slot15", query: {} });
	assert.deepEqual(req("cam", { target: "lobby", lobbyNo: "3", who: "colour", colour: "Lime", mode: "arm" }), { path: "cam/3/lime", query: { arm: "1" } });
	for (const who of ["next", "prev", "auto"]) assert.deepEqual(req("cam", { target: "lobby", lobbyNo: "2", who }), { path: `cam/2/${who}`, query: {} });
	assert.ok(req("cam", { who: "slot", slot: "16" }).error);
	assert.ok(req("cam", { who: "colour", colour: "mauve" }).error);
	assert.ok(req("cam", { target: "lobby", lobbyNo: "" }).error);
});

test("Cut to cam keeps who the camera follows", () => {
	const s = sampleState();
	assert.deepEqual(camCutRequest({ target: "pgm" }, s), { path: "cam/cut", query: {} });
	assert.deepEqual(camCutRequest({ target: "lobby", lobbyNo: "5" }, s), { path: "cam/5/slot2", query: {} }, "lobby 5 picked Fox (slot 2)");
	assert.deepEqual(camCutRequest({ target: "pvw" }, s), { path: "cam/pvw/auto", query: {} }, "lobby 1's camera picks by itself");
	assert.deepEqual(camCutRequest({ target: "lobby", lobbyNo: "9" }, s), { path: "cam/9/auto", query: {} });
	assert.ok(camCutRequest({ target: "lobby", lobbyNo: "x" }, s).error);
});

test("Take, Wide, Layout, Replay, Break and Panic paths", () => {
	assert.deepEqual(req("take", {}), { path: "take", query: {} });
	assert.deepEqual(req("take", { as: "quad", t: "cut" }), { path: "take", query: { as: "quad", t: "cut" } });
	assert.deepEqual(req("take", { as: "nonsense" }), { path: "take", query: {} });
	assert.deepEqual(req("wide", {}), { path: "wide", query: {} });
	for (const layout of ["full", "2up", "quad", "grid", "cams"]) assert.deepEqual(req("layout", { layout }), { path: `layout/${layout}`, query: {} });
	for (const op of ["last", "that", "live", "toggle", "back", "forward", "frameback", "frameforward", "restart", "zoomin", "zoomout", "follow", "angle", "slow"])
		assert.deepEqual(req("replay", { op }), { path: `replay/${op}`, query: {} });
	for (const kind of ["brb", "soon", "intermission", "sponsor", "off"]) assert.deepEqual(req("break", { kind }), { path: `break/${kind}`, query: {} });
	assert.equal(view("break", { kind: "soon" }).word, "SOON");
	assert.equal(view("break", { kind: "soon" }).title, "STARTING SOON");
	assert.deepEqual(req("panic", {}), { path: "panic", query: {} });
	assert.deepEqual(req("undo", {}), { path: "undo", query: {} });
});

test("Panic shows a panic that is on and undoes only that; Undo is lit while it is", () => {
	const calm = sampleState();
	const panicking = { ...sampleState(), panic: true };
	assert.equal(view("panic", {}, calm).title, "PANIC");
	assert.equal(view("panic", {}, panicking).title, "UNDO PANIC");
	assert.equal(view("panic", {}, panicking).lit, true);
	// The panic's own undo, never a plain Undo (which would take back the last switch if no panic were on).
	assert.deepEqual(req("panic", {}, panicking), { path: "panic/undo", query: {} });
	assert.deepEqual(req("panic", {}, calm), { path: "panic", query: {} });
	assert.equal(view("undo", {}, calm).lit, false);
	assert.equal(view("undo", {}, panicking).lit, true);
	assert.equal(view("undo", {}, panicking).sub, "the panic");
});

test("Toggles go to their own paths: toggle by default, or always on or off", () => {
	const paths = { mute: "voice/mute", hold: "hold", autogrid: "auto/grid", autointermission: "auto/intermission", autoswitch: "auto/switch", standings: "gfx/standings", alerts: "gfx/alerts" };
	assert.deepEqual(Object.keys(TOGGLES).sort(), Object.keys(paths).sort());
	for (const [which, path] of Object.entries(paths)) {
		assert.deepEqual(req("toggle", { which }), { path: `${path}/toggle`, query: {} });
		assert.deepEqual(req("toggle", { which, press: "on" }), { path: `${path}/on`, query: {} });
		assert.deepEqual(req("toggle", { which, press: "off" }), { path: `${path}/off`, query: {} });
	}
});

test("Custom path is tidied, and never carries a key or the impostor flag", () => {
	assert.deepEqual(customRequest({ path: "gfx/skip" }), { path: "gfx/skip", query: {} });
	assert.deepEqual(customRequest({ path: "/deck/voice/listen/3/" }), { path: "voice/listen/3", query: {} });
	assert.deepEqual(customRequest({ path: "http://127.0.0.1:8768/deck/lobby/2?key=SECRET&t=cut&imp=1" }), { path: "lobby/2", query: { t: "cut" } });
	assert.deepEqual(customRequest({ path: "lobby/Lobby%201" }), { path: "lobby/Lobby 1", query: {} }, "decoded once: the request encodes it again");
	assert.deepEqual(customRequest({ path: "lobby/Grand Final" }), { path: "lobby/Grand Final", query: {} });
	assert.ok(customRequest({ path: "lobby/%E0%A4%A" }).error, "a broken escape");
	assert.ok(customRequest({ path: "lobby/%2e%2e/x" }).error, "an encoded ..");
	assert.ok(customRequest({ path: "" }).error);
	assert.ok(customRequest({ path: "../app/admin" }).error);
	assert.ok(customRequest({ path: "state" }).error);
	assert.ok(customRequest({ path: "img/lobby/1.svg" }).error);
	assert.ok(customRequest({ path: "lobby/<script>" }).error);
});

test("no action ever asks for impostor flags", () => {
	const s = sampleState();
	for (const a of ACTIONS) {
		for (const fn of [a.request, a.commit, a.touch].filter(Boolean)) {
			const r = fn(settingsOf(a, {}), s, 21);
			if (r && !r.error) {
				assert.ok(!("imp" in r.query), a.uuid);
				assert.ok(!r.path.includes("imp"), a.uuid);
			}
		}
	}
});

test("Lobby key view: number, phase, tally, crew v impostors and the sabotage timer", () => {
	const v = view("lobby", { lobby: "3" }, sampleState(), { status: "ok", receivedAt: 1000, now: 6000 });
	assert.equal(v.type, "lobby");
	assert.equal(v.no, 3);
	assert.equal(v.tally, "pgm");
	assert.equal(v.phase, "ingame");
	assert.equal(v.crew, 4);
	assert.equal(v.imps, 1);
	assert.deepEqual(v.sabotage, { system: "Reactor", left: 15 });
	assert.equal(typeof v.flash, "boolean");
	assert.notEqual(view("lobby", { lobby: "3" }, sampleState(), { now: 0 }).flash, view("lobby", { lobby: "3" }, sampleState(), { now: 500 }).flash, "the timer flashes");
	assert.equal(view("lobby", { lobby: "4" }).online, false);
	assert.equal(view("lobby", { lobby: "7" }).missing, true);
	const step = view("lobby", { lobby: "next" });
	assert.equal(step.type, "lobbyStep");
	assert.deepEqual(step.target, { no: 5, name: "Lobby 5" }, "next from Program (3)");
});

test("Player cam key view: the player, a ring when followed, ARMED, dead and empty slots", () => {
	const ann = view("cam", { target: "lobby", lobbyNo: "3", who: "slot", slot: "1" });
	assert.equal(ann.player.name, "Ann");
	assert.equal(ann.player.hex, "#c51111");
	assert.equal(ann.ring, "on", "followed, but the camera isn't on stream");
	const s = sampleState();
	s.program.layout = "cams";
	assert.equal(view("cam", { target: "pgm", who: "slot", slot: "1" }, s).ring, "pgm");
	const bea = view("cam", { target: "lobby", lobbyNo: "3", who: "colour", colour: "blue" });
	assert.equal(bea.player.dead, true);
	assert.equal(bea.ring, null);
	assert.equal(view("cam", { target: "lobby", lobbyNo: "5", who: "slot", slot: "2" }).armed, true);
	assert.equal(view("cam", { target: "lobby", lobbyNo: "5", who: "slot", slot: "1" }).armed, false);
	assert.equal(view("cam", { target: "lobby", lobbyNo: "3", who: "slot", slot: "9" }).player, null);
	assert.equal(view("cam", { target: "lobby", lobbyNo: "4", who: "slot", slot: "1" }).noCam, true);
	const next = view("cam", { target: "lobby", lobbyNo: "5", who: "next", mode: "arm" });
	assert.equal(next.type, "camStep");
	assert.equal(next.current.name, "Fox");
	assert.equal(next.mode, "arm");
});

test("Toggle, Take, Layout and Replay keys show the real state", () => {
	assert.equal(view("toggle", { which: "hold" }).on, true);
	assert.equal(view("toggle", { which: "mute" }).on, false);
	assert.equal(view("toggle", { which: "alerts" }).offText, "PAUSED");
	assert.equal(view("toggle", { which: "mute" }, null).on, null);
	const take = view("take", {});
	assert.equal(take.lit, true);
	assert.match(take.sub, /L1 L5/);
	assert.equal(view("take", {}, { ...sampleState(), preview: { layout: "full", slots: [] } }).dim, true);
	assert.equal(view("layout", { layout: "full" }).lit, true);
	assert.equal(view("layout", { layout: "full" }).accent, "red");
	assert.equal(view("layout", { layout: "2up" }).accent, "green");
	assert.equal(view("layout", { layout: "grid" }).lit, false);
	assert.equal(view("replay", { op: "last" }).sub, "Red kills Blue");
	assert.equal(view("replay", { op: "live" }).lit, false);
	assert.equal(view("replay", { op: "slow" }).word, "100%");
});

test("the status reaches every view", () => {
	for (const a of ACTIONS) assert.equal(view(a.uuid.slice(P.length), {}, sampleState(), { status: "offline", receivedAt: 0, now: 0 }).status, "offline", a.uuid);
});

test("Preview dial: turning steps through live lobbies from Preview; push takes", () => {
	const d = BY_UUID[P + "dial.preview"];
	const s = sampleState();
	assert.equal(d.rotate({}, s, null, 1), 3);
	assert.equal(d.rotate({}, s, 3, 1), 5);
	assert.equal(d.rotate({}, s, 5, 2), 3);
	assert.deepEqual(d.commit({}, s, 5), { path: "pvw/lobby/5", query: {} });
	assert.deepEqual(d.request(settingsOf(d, { as: "2up" }), s), { path: "take", query: { as: "2up" } });
	assert.deepEqual(d.touch({}, s), { path: "pvw/clear", query: {} });
	assert.equal(d.view(settingsOf(d, {}), s, {}, 5).lobby.no, 5);
	assert.equal(d.view(settingsOf(d, {}), s, {}, 5).pending, true);
	assert.equal(d.view(settingsOf(d, {}), s, {}, null).lobby.no, 1);
});

test("Player dial: turning arms the next living player; push cuts to them", () => {
	const d = BY_UUID[P + "dial.player"];
	const s = sampleState();
	const lobby3 = { target: "lobby", lobbyNo: "3" };
	assert.equal(d.rotate(lobby3, s, null, 1), 23, "from Ann (the pick) past dead Bea");
	assert.deepEqual(d.commit(lobby3, s, 23), { path: "cam/3/slot3", query: { arm: "1" } });
	assert.deepEqual(d.request(lobby3, s, 23), { path: "cam/3/slot3", query: {} });
	assert.deepEqual(d.request({ target: "pgm" }, s, null), { path: "cam/cut", query: {} });
	assert.ok(d.commit(lobby3, s, 99).error);
	assert.deepEqual(d.touch({}, s), { path: "wide", query: {} });
	assert.equal(d.view(lobby3, s, {}, 23).player.name, "Cy <&>");
});

test("Replay dial: slow turns step frames, fast turns jump seconds", () => {
	assert.equal(scrubStep(0), null);
	assert.deepEqual(scrubStep(1), { op: "frameforward", consumed: 1 });
	assert.deepEqual(scrubStep(-2), { op: "frameback", consumed: -1 });
	assert.deepEqual(scrubStep(5), { op: "forward", consumed: 5 });
	assert.deepEqual(scrubStep(-3), { op: "back", consumed: -3 });
	const d = BY_UUID[P + "dial.replay"];
	assert.deepEqual(d.request({}, sampleState()), { path: "replay/toggle", query: {} });
	assert.deepEqual(d.touch({}, sampleState()), { path: "replay/slow", query: {} });
});
