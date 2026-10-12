import assert from "node:assert/strict";
import http from "node:http";
import { test } from "node:test";

import { StatePoller, backoff, connectionOf, deckPath, forLog, probe, request, succeeded } from "../src/client.js";
import { sampleState } from "./fixtures.js";

test("the connection falls back to Red Alert's defaults", () => {
	assert.deepEqual(connectionOf({}), { host: "127.0.0.1", port: 8768, key: "" });
	assert.deepEqual(connectionOf({ host: " localhost ", port: "9000", key: " k1 " }), { host: "127.0.0.1", port: 9000, key: "k1" });
	assert.equal(connectionOf({ port: "70000" }).port, 8768);
	assert.equal(connectionOf({ port: "abc" }).port, 8768);
	assert.equal(connectionOf(null).host, "127.0.0.1");
});

test("a deck path encodes each part once, carries the key and never the impostor flag", () => {
	assert.equal(deckPath("lobby/3", { t: "cut" }, "K+/="), "/deck/lobby/3?key=K%2B%2F%3D&t=cut");
	assert.equal(deckPath("/lobby/Lobby 1", {}, "k"), "/deck/lobby/Lobby%201?key=k");
	assert.equal(deckPath("state", { since: 4, imp: 1, key: "other" }, "k"), "/deck/state?key=k&since=4");
	assert.equal(deckPath("take", { as: "", t: null }, "k"), "/deck/take?key=k");
	assert.equal(forLog("/deck/lobby/3?key=SECRET"), "/deck/lobby/3");
});

test("2xx is done, except Red Alert's old 200 with ok:false; a dropped repeat is not a failure", () => {
	assert.equal(succeeded(200, { ok: true }), true);
	assert.equal(succeeded(202, { ok: true, message: "Armed" }), true);
	assert.equal(succeeded(200, null), true);
	assert.equal(succeeded(200, { ok: false, message: "No lobby 3." }), false);
	assert.equal(succeeded(200, { ok: false, message: "ignored repeat" }), true);
	for (const status of [0, 401, 404, 409, 423, 503]) assert.equal(succeeded(status, { ok: true }), false, String(status));
});

test("the backoff grows to 5 s", () => {
	assert.deepEqual([1, 2, 3, 4, 5, 9].map(backoff), [500, 1000, 2000, 4000, 5000, 5000]);
});

/** A stand-in Red Alert on a free port: `answer(req)` gives [status, body]. */
async function fakeRedAlert(answer) {
	const seen = [];
	const server = http.createServer((req, res) => {
		seen.push({ method: req.method, url: req.url });
		const [status, body] = answer(req);
		res.writeHead(status, { "Content-Type": "application/json" });
		res.end(typeof body === "string" ? body : JSON.stringify(body));
	});
	await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
	return { conn: { host: "127.0.0.1", port: server.address().port, key: "sekret" }, seen, close: () => new Promise((resolve) => server.close(resolve)) };
}

test("a press is a POST with the key, and the answer's status and message come back", async () => {
	const ra = await fakeRedAlert((req) => (req.url.startsWith("/deck/cam/") ? [409, { ok: false, message: "Camera not available." }] : [200, { ok: true, message: "Lobby 3 is on stream." }]));
	try {
		const ok = await request(ra.conn, "POST", "lobby/3", { t: "swoosh" });
		assert.deepEqual([ok.status, ok.ok, ok.message], [200, true, "Lobby 3 is on stream."]);
		const refused = await request(ra.conn, "POST", "cam/pgm/red");
		assert.deepEqual([refused.status, refused.ok, refused.message], [409, false, "Camera not available."]);
		assert.deepEqual(ra.seen[0], { method: "POST", url: "/deck/lobby/3?key=sekret&t=swoosh" });
	} finally {
		await ra.close();
	}
});

test("a request to nothing, or that isn't answered in time, resolves with status 0", async () => {
	const ra = await fakeRedAlert(() => [200, {}]);
	const port = ra.conn.port;
	await ra.close();
	const res = await request({ host: "127.0.0.1", port, key: "k" }, "POST", "panic");
	assert.equal(res.status, 0);
	assert.equal(res.ok, false);
	assert.match(res.message, /Is Red Alert open/);
	const silent = http.createServer(() => {});
	await new Promise((resolve) => silent.listen(0, "127.0.0.1", resolve));
	try {
		const late = await request({ host: "127.0.0.1", port: silent.address().port, key: "k" }, "GET", "state", {}, { timeout: 100 });
		assert.equal(late.status, 0);
		assert.equal(late.timedOut, true);
	} finally {
		silent.closeAllConnections?.();
		await new Promise((resolve) => silent.close(resolve));
	}
});

test("Test in the settings panel explains what's wrong", async () => {
	assert.match((await probe({ host: "127.0.0.1", port: 1, key: "" })).message, /Paste the key/);
	const answers = { good: [200, sampleState()], bad: [401, { ok: false, message: "Wrong key" }], old: [200, { ok: false, message: "Unknown Stream Deck action." }], locked: [423, { ok: false, message: "Locked" }] };
	let which = "good";
	const ra = await fakeRedAlert(() => answers[which]);
	try {
		const good = await probe(ra.conn);
		assert.equal(good.ok, true);
		assert.match(good.message, /4 lobbies, OBS connected/);
		which = "bad";
		assert.match((await probe(ra.conn)).message, /key is wrong/);
		which = "old";
		assert.match((await probe(ra.conn)).message, /update Red Alert/);
		which = "locked";
		assert.match((await probe(ra.conn)).message, /locked/);
		assert.ok(ra.seen.every((r) => r.method === "GET" && r.url.startsWith("/deck/state?key=sekret") && !r.url.includes("imp")));
	} finally {
		await ra.close();
	}
});

/** A poller driven by scripted answers, with a clock that only moves when told to. */
function scriptedPoller(answers, o = {}) {
	const asked = [];
	const states = [];
	const statuses = [];
	const waits = [];
	let clock = 0;
	let stopAt = null;
	const poller = new StatePoller({
		now: () => clock,
		request: async (conn, method, path, query) => {
			asked.push({ method, path, query: { ...query } });
			const next = answers.shift();
			if (!next || asked.length === stopAt) poller.stop();
			clock += next?.took ?? 0;
			return next?.res ?? { status: 0, ok: false, message: "done" };
		},
		wait: async (ms) => {
			waits.push(ms);
		},
		onState: (state, at) => states.push({ rev: state.rev, at }),
		onStatus: (status) => statuses.push(status),
	});
	stopAt = o.stopAt ?? null;
	return { poller, asked, states, statuses, waits };
}

const state = (rev) => ({ status: 200, ok: true, body: { ...sampleState(), rev }, message: "" });

test("the one state loop asks for anything newer than what it has, and never for impostor flags", async () => {
	const s = scriptedPoller([{ res: state(1) }, { res: state(2), took: 4000 }, { res: state(2), took: 25_000 }, { res: state(2), took: 200 }, { res: state(3) }]);
	s.poller.setConnection({ host: "127.0.0.1", port: 8768, key: "k" });
	s.poller.start();
	await new Promise((resolve) => setImmediate(resolve));
	await new Promise((resolve) => setImmediate(resolve));
	assert.deepEqual(s.asked.map((a) => a.query), [{}, { since: 1 }, { since: 2 }, { since: 2 }, { since: 2 }, { since: 3 }]);
	assert.ok(s.asked.every((a) => a.method === "GET" && a.path === "state" && !("imp" in a.query)));
	assert.deepEqual(s.states.map((x) => x.rev), [1, 2, 3], "each revision handed on once");
	assert.deepEqual(s.statuses, ["ok"]);
	assert.deepEqual(s.waits, [800], "only the quick answer with nothing new waits (the rest of a second)");
});

test("the state loop says why it can't read Red Alert, and backs off", async () => {
	const s = scriptedPoller([
		{ res: { status: 401, ok: false, message: "Wrong key" } },
		{ res: { status: 0, ok: false, message: "Nothing answered" } },
		{ res: { status: 0, ok: false, message: "Nothing answered" } },
		{ res: { status: 200, ok: false, body: { ok: false, message: "Unknown Stream Deck action." }, message: "Unknown" } },
		{ res: { status: 0, ok: false, timedOut: true, message: "late" } },
		{ res: state(5) },
	]);
	s.poller.setConnection({ host: "127.0.0.1", port: 8768, key: "k" });
	s.poller.start();
	for (let i = 0; i < 4; i++) await new Promise((resolve) => setImmediate(resolve));
	assert.deepEqual(s.statuses, ["badkey", "offline", "old", "ok"]);
	assert.deepEqual(s.waits, [3000, 500, 1000, 10_000], "a long poll that ran out asks again at once");
	assert.deepEqual(s.states.map((x) => x.rev), [5]);
});

test("without a key the loop doesn't ask, and a new key starts it from scratch", async () => {
	const s = scriptedPoller([{ res: state(9) }]);
	s.poller.start();
	await new Promise((resolve) => setImmediate(resolve));
	assert.equal(s.asked.length, 0);
	assert.equal(s.poller.status, "nokey");
	s.poller.setConnection({ host: "127.0.0.1", port: 8768, key: "new" });
	await new Promise((resolve) => setImmediate(resolve));
	assert.deepEqual(s.asked[0].query, {});
	assert.deepEqual(s.states.map((x) => x.rev), [9]);
	s.poller.stop();
});
