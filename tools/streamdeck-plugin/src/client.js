// Talking to Red Alert: the /deck/... presses, and the one long-poll loop on /deck/state that every
// key is drawn from. Red Alert answers one request per connection and only listens on this PC, so each
// request is its own short connection to 127.0.0.1. The deck key goes in the query (as Red Alert's own
// links carry it) and is never written to the log.

import http from "node:http";

export const DEFAULT_HOST = "127.0.0.1";
export const DEFAULT_PORT = 8768;

/** Red Alert holds /deck/state for up to 25 s; wait a little longer than that before giving up. */
const STATE_TIMEOUT = 35_000;
const PRESS_TIMEOUT = 10_000;
/** A state with nothing new that came back faster than this wasn't a held long poll. */
const QUICK_ANSWER = 1000;
const MAX_BODY = 2 * 1024 * 1024;

/** The connection from the global settings, with the defaults filled in and junk thrown out. */
export function connectionOf(settings) {
	const host = String(settings?.host ?? "").trim() || DEFAULT_HOST;
	const port = Number.parseInt(String(settings?.port ?? ""), 10);
	return {
		host: host.toLowerCase() === "localhost" ? DEFAULT_HOST : host,
		port: Number.isInteger(port) && port > 0 && port < 65536 ? port : DEFAULT_PORT,
		key: String(settings?.key ?? "").trim(),
	};
}

/** The path and query of a /deck/... request (the part after the host). */
export function deckPath(path, query, key) {
	const clean = String(path).replace(/^\/+/, "").split("/").map(encodeURIComponent).join("/");
	const params = new URLSearchParams();
	params.set("key", key ?? "");
	for (const [k, v] of Object.entries(query ?? {})) if (v !== undefined && v !== null && v !== "" && k !== "key" && k !== "imp") params.set(k, String(v));
	return `/deck/${clean}?${params.toString()}`;
}

/** A request's path for the log: the key (and everything else in the query) left out. */
export function forLog(path) {
	return String(path).split("?")[0];
}

/**
 * Whether Red Alert did what a request asked. A 2xx is done (202: queued, or armed for when play
 * resumes), except that Red Alert from before this plugin answered 200 with ok:false in the body when
 * nothing happened, so that counts as refused. A press Red Alert dropped as a repeat of the same press
 * within 300 ms isn't a failure either: the first one went through.
 */
export function succeeded(status, body) {
	if (status < 200 || status >= 300) return false;
	if (body?.ok !== false) return true;
	return typeof body.message === "string" && /^ignored repeat/i.test(body.message.trim());
}

/**
 * One request to Red Alert. Resolves (never rejects) with the status (0 when nothing answered), the
 * JSON body when there is one, and a message to show or log.
 */
export function request(conn, method, path, query = {}, o = {}) {
	return new Promise((resolve) => {
		const timeoutMs = o.timeout ?? PRESS_TIMEOUT;
		let settled = false;
		const done = (result) => {
			if (settled) return;
			settled = true;
			clearTimeout(timer);
			resolve(result);
		};
		let req;
		const timer = setTimeout(() => {
			req?.destroy(new Error("timeout"));
			done({ status: 0, ok: false, body: null, message: `Red Alert didn't answer within ${Math.round(timeoutMs / 1000)} s.`, timedOut: true });
		}, timeoutMs);
		try {
			req = http.request(
				{
					host: conn.host,
					port: conn.port,
					method,
					path: deckPath(path, query, conn.key),
					agent: false,
					signal: o.signal,
					headers: method === "POST" ? { "Content-Length": "0", Connection: "close" } : { Connection: "close" },
				},
				(res) => {
					const chunks = [];
					let size = 0;
					res.on("data", (chunk) => {
						size += chunk.length;
						if (size > MAX_BODY) res.destroy(new Error("too big"));
						else chunks.push(chunk);
					});
					res.on("error", () => done({ status: 0, ok: false, body: null, message: "The answer from Red Alert was cut off." }));
					res.on("end", () => {
						let body = null;
						try {
							body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
						} catch {
							body = null;
						}
						const status = res.statusCode ?? 0;
						const message = typeof body?.message === "string" ? body.message : `Red Alert answered ${status}.`;
						done({ status, ok: succeeded(status, body), body, message });
					});
				},
			);
			req.on("error", (e) => {
				const aborted = e?.name === "AbortError" || o.signal?.aborted;
				done({ status: 0, ok: false, body: null, aborted, message: aborted ? "Stopped." : `Nothing answered on ${conn.host}:${conn.port}. Is Red Alert open?` });
			});
			req.end();
		} catch (e) {
			done({ status: 0, ok: false, body: null, message: `Couldn't reach ${conn.host}:${conn.port}: ${e?.message ?? e}` });
		}
	});
}

/** A press: POST /deck/<path>. */
export function press(conn, path, query = {}, o = {}) {
	return request(conn, "POST", path, query, o);
}

/**
 * What the Property Inspector's Test button says about a connection: one immediate /deck/state read,
 * worded for whoever is setting up the Stream Deck.
 */
export async function probe(conn, o = {}) {
	if (!conn.key) return { ok: false, message: "Paste the key from Red Alert (Settings → Stream Deck) first." };
	const res = await request(conn, "GET", "state", {}, { timeout: o.timeout ?? 8000 });
	if (res.status === 200 && res.body && typeof res.body.rev === "number") {
		const lobbies = Array.isArray(res.body.lobbies) ? res.body.lobbies.length : 0;
		const obs = res.body.obs?.connected ? "OBS connected" : "OBS not connected";
		return { ok: true, message: `Connected to Red Alert: ${lobbies} ${lobbies === 1 ? "lobby" : "lobbies"}, ${obs}.` };
	}
	if (res.status === 401) return { ok: false, message: "Red Alert answered, but the key is wrong: copy it again from Red Alert (Settings → Stream Deck)." };
	if (res.status === 403) return { ok: false, message: "Red Alert only answers this PC: use the host 127.0.0.1." };
	if (res.status === 423) return { ok: false, message: "Red Alert is locked: open it and type the administration code." };
	// Red Alert from before the plugin answers /deck/state as an unknown action (404, or 200 with no rev).
	if (isOldRedAlert(res)) return { ok: false, message: "Red Alert answered but has no Stream Deck state yet: update Red Alert to the newest release." };
	if (res.status === 0) return { ok: false, message: res.timedOut ? res.message : `Nothing answered on ${conn.host}:${conn.port}. Is Red Alert open? Check the port in Red Alert (Settings → Stream Deck).` };
	return { ok: false, message: `Red Alert answered ${res.status}: ${res.message}` };
}

/** A /deck/state answer from a Red Alert too old to have it: 404, or a 200 that isn't a state. */
function isOldRedAlert(res) {
	return res.status === 404 || (res.status === 200 && typeof res.body?.rev !== "number");
}

/** How long to wait before trying again after a failure, growing to 5 s. */
export function backoff(failures) {
	return Math.min(5000, 500 * 2 ** Math.max(0, failures - 1));
}

/**
 * The one loop that reads /deck/state for every key: it asks for anything newer than the revision it
 * has, Red Alert answers as soon as something changes (or after 25 s), and it asks again. It never
 * asks for impostor flags.
 */
export class StatePoller {
	/**
	 * @param {{ onState?: (state: object, at: number) => void, onStatus?: (status: string, message: string) => void, request?: typeof request, now?: () => number, wait?: (ms: number, signal: AbortSignal) => Promise<void> }} o
	 */
	constructor(o = {}) {
		this.onState = o.onState ?? (() => {});
		this.onStatus = o.onStatus ?? (() => {});
		this.request = o.request ?? request;
		this.now = o.now ?? Date.now;
		this.wait = o.wait ?? sleep;
		this.conn = connectionOf({});
		this.rev = null;
		this.status = "nokey";
		this.running = false;
		this.generation = 0;
		this.controller = null;
	}

	/** New host, port or key: drop the request in flight and start again from scratch. */
	setConnection(conn) {
		const changed = conn.host !== this.conn.host || conn.port !== this.conn.port || conn.key !== this.conn.key;
		this.conn = conn;
		if (changed && this.running) this.restart();
		else if (!conn.key) this.setStatus("nokey", "No key yet.");
	}

	start() {
		if (this.running) return;
		this.running = true;
		this.restart();
	}

	stop() {
		this.running = false;
		this.generation++;
		this.controller?.abort();
		this.controller = null;
	}

	restart() {
		this.generation++;
		this.controller?.abort();
		this.rev = null;
		const generation = this.generation;
		this.loop(generation).catch(() => {});
	}

	setStatus(status, message) {
		if (status === this.status) return;
		this.status = status;
		this.onStatus(status, message ?? "");
	}

	async loop(generation) {
		let failures = 0;
		while (this.running && generation === this.generation) {
			if (!this.conn.key) {
				this.setStatus("nokey", "No key yet.");
				return;
			}
			const controller = new AbortController();
			this.controller = controller;
			const query = this.rev === null ? {} : { since: this.rev };
			const asked = this.now();
			const res = await this.request(this.conn, "GET", "state", query, { timeout: STATE_TIMEOUT, signal: controller.signal });
			if (!this.running || generation !== this.generation) return;
			let pause = 0;
			if (res.status === 200 && typeof res.body?.rev === "number") {
				const fresh = res.body.rev !== this.rev;
				failures = 0;
				this.rev = res.body.rev;
				this.setStatus("ok", "");
				if (fresh) this.onState(res.body, this.now());
				// Nothing new: after a full 25 s wait ask again at once, but if Red Alert answered at
				// once (it didn't hold the request) wait out the rest of a second rather than spin.
				else pause = Math.max(0, QUICK_ANSWER - (this.now() - asked));
			} else if (res.status === 401) {
				this.setStatus("badkey", res.message);
				pause = 3000;
			} else if (isOldRedAlert(res)) {
				this.setStatus("old", res.message);
				pause = 10_000;
			} else if (res.status === 423) {
				this.setStatus("locked", res.message);
				pause = 3000;
			} else if (res.status === 0 && res.timedOut) {
				// A long poll that ran out: ask again straight away, nothing is wrong.
				failures = 0;
			} else {
				failures++;
				this.setStatus("offline", res.message);
				pause = backoff(failures);
			}
			if (pause > 0) await this.wait(pause, controller.signal);
		}
	}
}

function sleep(ms, signal) {
	return new Promise((resolve) => {
		const timer = setTimeout(resolve, ms);
		signal?.addEventListener?.("abort", () => {
			clearTimeout(timer);
			resolve();
		}, { once: true });
	});
}
