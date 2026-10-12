import assert from "node:assert/strict";
import http from "node:http";
import { test } from "node:test";

import { StatePoller, backoff, connectionOf, deckPath, findRedAlert, forLog, probe, request, succeeded } from "../src/client.js";
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

/** A poller driven by scripted answers, with a clock that only moves when told to. /deck/hello (looking for Red Alert on its other ports) is answered by `o.hello(port)`, nothing by default. */
function scriptedPoller(answers, o = {}) {
	const asked = [];
	const hellos = [];
	const states = [];
	const statuses = [];
	const waits = [];
	const ports = [];
	let clock = 0;
	let stopAt = null;
	const poller = new StatePoller({
		now: () => clock,
		request: async (conn, method, path, query) => {
			if (path === "hello") {
				hellos.push({ method, port: conn.port, key: conn.key });
				return o.hello?.(conn.port) ?? { status: 0, ok: false, message: "Nothing answered" };
			}
			asked.push({ method, path, query: { ...query }, port: conn.port, key: conn.key });
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
		onPort: (port) => ports.push(port),
	});
	stopAt = o.stopAt ?? null;
	return { poller, asked, hellos, states, statuses, waits, ports };
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

const hello = (port) => ({ status: 200, ok: true, body: { ok: true, message: `Red Alert 0.1.0 on port ${port}.` }, message: "" });

test("Red Alert on another of its ports is found by asking who answers, without the key", async () => {
	const asked = [];
	const answers = { 8768: { status: 404, ok: false, body: { error: "not found" } }, 8769: { status: 0, ok: false }, 8770: hello(8770) };
	const ask = async (conn, method, path) => {
		asked.push({ port: conn.port, key: conn.key, method, path });
		return answers[conn.port] ?? { status: 0, ok: false };
	};
	assert.equal(await findRedAlert({ host: "127.0.0.1", port: 8768, key: "sekret" }, { request: ask, skip: 8768 }), 8770);
	assert.deepEqual(asked.map((a) => a.port), [8769, 8770], "the set port skipped, then in order until one answers");
	assert.ok(asked.every((a) => a.key === "" && a.method === "GET" && a.path === "hello"), "the key never goes to a port before it said it's Red Alert");
	// A foreign service that answers 200 isn't taken for Red Alert.
	answers[8770] = { status: 200, ok: true, body: { ok: true, message: "Hello from something else" } };
	assert.equal(await findRedAlert({ host: "127.0.0.1", port: 8768, key: "k" }, { request: ask, skip: 8768 }), null);
	// A port set by hand outside Red Alert's own ones: nothing is looked for.
	asked.length = 0;
	assert.equal(await findRedAlert({ host: "127.0.0.1", port: 9000, key: "k" }, { request: ask }), null);
	assert.equal(asked.length, 0);
	// Set to 8769 and Red Alert back on 8768 after a restart: found there.
	answers[8768] = hello(8768);
	assert.equal(await findRedAlert({ host: "127.0.0.1", port: 8769, key: "k" }, { request: ask, skip: 8769 }), 8768);
});

test("the state loop follows Red Alert to the port it moved to, and presses go there too", async () => {
	const s = scriptedPoller(
		[
			{ res: { status: 0, ok: false, message: "Nothing answered" } },
			{ res: state(7) },
			{ res: state(8) },
		],
		{ hello: (port) => (port === 8770 ? hello(8770) : undefined) },
	);
	s.poller.setConnection({ host: "127.0.0.1", port: 8768, key: "k" });
	s.poller.start();
	for (let i = 0; i < 4; i++) await new Promise((resolve) => setImmediate(resolve));
	assert.deepEqual(s.asked.map((a) => [a.port, a.key]), [[8768, "k"], [8770, "k"], [8770, "k"], [8770, "k"]]);
	assert.deepEqual(s.hellos.map((h) => [h.port, h.key]), [[8769, ""], [8770, ""]]);
	assert.deepEqual(s.ports, [8770]);
	assert.deepEqual(s.statuses, ["ok"], "never said offline: it was found straight away");
	assert.deepEqual(s.waits, [], "no backoff before asking the port it was found on");
	assert.deepEqual(s.states.map((x) => x.rev), [7, 8]);
	assert.deepEqual(s.poller.target, { host: "127.0.0.1", port: 8770, key: "k" });
	// A new connection set by hand starts from its own port again.
	s.poller.stop();
	s.poller.setConnection({ host: "127.0.0.1", port: 8769, key: "k" });
	assert.equal(s.poller.target.port, 8769);
});

test("not found on any of Red Alert's ports, the loop says offline and backs off as before", async () => {
	const s = scriptedPoller([{ res: { status: 0, ok: false, message: "Nothing answered" } }, { res: { status: 0, ok: false, message: "Nothing answered" } }]);
	s.poller.setConnection({ host: "127.0.0.1", port: 8768, key: "k" });
	s.poller.start();
	for (let i = 0; i < 4; i++) await new Promise((resolve) => setImmediate(resolve));
	assert.deepEqual(s.statuses, ["offline"]);
	assert.deepEqual(s.hellos.map((h) => h.port), [8769, 8770, 8771, 8769, 8770, 8771]);
	assert.ok(s.asked.every((a) => a.port === 8768));
	assert.equal(s.poller.target.port, 8768);
});

test("Test in the settings panel finds Red Alert on another of its ports", async () => {
	const ask = async (conn, method, path) => {
		if (conn.port === 8768) return { status: 0, ok: false, message: "Nothing answered" };
		if (path === "hello") return conn.port === 8769 ? hello(8769) : { status: 0, ok: false };
		assert.equal(conn.key, "sekret");
		return { status: 200, ok: true, body: sampleState(), message: "" };
	};
	const found = await probe({ host: "127.0.0.1", port: 8768, key: "sekret" }, { request: ask });
	assert.equal(found.ok, true);
	assert.match(found.message, /on port 8769/);
});
