// The keys on the Stream Deck: which are showing, what each one last drew, and what a press, a dial
// turn or a tap on the touch strip sends. It doesn't know the Stream Deck SDK (plugin.js hands it a
// small handle per key), so the tests drive it with fakes.

import { BY_UUID, scrubStep, settingsOf } from "./actions.js";
import { lobbies, players } from "./model.js";
import { dataUrl, render } from "./svg.js";

/** How long a dial rests before the lobby or player it landed on is sent. */
const COMMIT_DELAY = 250;
/** How long a dial's choice stays on its strip after it was sent, until Red Alert's state shows it. */
const HOLD_CHOICE = 800;
/** Red Alert ignores the same request twice within 300 ms, so scrub steps go at least this far apart. */
const SCRUB_GAP = 320;

export class Deck {
	/**
	 * @param {{ send: (path: string, query: object) => Promise<{ status: number, ok: boolean, message: string }>, log?: (line: string) => void, now?: () => number, timers?: object }} o
	 */
	constructor(o) {
		this.send = o.send;
		this.log = o.log ?? (() => {});
		this.now = o.now ?? Date.now;
		this.timers = o.timers ?? { setTimeout, clearTimeout, setInterval, clearInterval };
		this.commitDelay = o.commitDelay ?? COMMIT_DELAY;
		this.holdChoice = o.holdChoice ?? HOLD_CHOICE;
		this.scrubGap = o.scrubGap ?? SCRUB_GAP;
		this.keys = new Map();
		this.state = null;
		this.receivedAt = 0;
		this.status = "nokey";
		this.ticker = null;
	}

	/** A key (or dial) came into view: remember it and draw it from the state there is. */
	appear(id, handle, uuid, settings, isDial = false) {
		const def = BY_UUID[uuid];
		if (!def) return;
		this.disappear(id);
		const key = { id, handle, def, settings: settingsOf(def, settings), dial: Boolean(isDial || def.dial), busy: false, image: null, pending: null, commitTimer: null, committing: null, staged: null, clearTimer: null, scrub: 0, scrubbing: false };
		this.keys.set(id, key);
		this.draw(key);
		this.updateTicker();
	}

	disappear(id) {
		const key = this.keys.get(id);
		if (!key) return;
		this.timers.clearTimeout(key.commitTimer);
		this.timers.clearTimeout(key.clearTimer);
		this.keys.delete(id);
		this.updateTicker();
	}

	/** New settings from the Property Inspector: forget a half-made dial choice and redraw. */
	settings(id, settings) {
		const key = this.keys.get(id);
		if (!key) return;
		key.settings = settingsOf(key.def, settings);
		this.timers.clearTimeout(key.commitTimer);
		key.commitTimer = null;
		key.pending = null;
		key.staged = null;
		this.draw(key, true);
	}

	setState(state, at) {
		this.state = state;
		this.receivedAt = at ?? this.now();
		this.drawAll();
		this.updateTicker();
	}

	setStatus(status) {
		if (status === this.status) return;
		this.status = status;
		this.drawAll();
	}

	view(key) {
		const ctx = { status: this.status, receivedAt: this.receivedAt, now: this.now() };
		return key.def.view(key.settings, this.state, ctx, key.pending);
	}

	/** Draws a key, sending the image to Stream Deck only when it changed. */
	draw(key, force = false) {
		let markup;
		try {
			markup = render(this.view(key));
		} catch (e) {
			this.log(`Couldn't draw a key: ${e?.message ?? e}`);
			return;
		}
		if (!force && markup === key.image) return;
		key.image = markup;
		const url = dataUrl(markup);
		const sent = key.dial ? key.handle.setFeedback?.({ canvas: url }) : key.handle.setImage?.(url);
		Promise.resolve(sent).catch(() => {});
	}

	drawAll() {
		for (const key of this.keys.values()) this.draw(key);
	}

	/** While a sabotage counts down, keys are redrawn twice a second (the timer flashes). */
	updateTicker() {
		const want = this.keys.size > 0 && lobbies(this.state).some((l) => l?.sabotage);
		if (want && !this.ticker) this.ticker = this.timers.setInterval(() => this.drawAll(), 500);
		else if (!want && this.ticker) {
			this.timers.clearInterval(this.ticker);
			this.ticker = null;
		}
	}

	/** Sends one request for a key; a tick on success and the alert triangle on anything else. */
	async run(key, req, o = {}) {
		if (!req || req.error) {
			this.log(`Not sent: ${req?.error ?? "nothing to send"}`);
			show(key, "showAlert");
			return false;
		}
		const res = await this.send(req.path, req.query ?? {});
		this.log(`${req.path} -> ${res.status} ${res.message ?? ""}`.trim());
		if (!res.ok) show(key, "showAlert");
		else if (o.ok !== false && !key.dial) show(key, "showOk");
		return res.ok;
	}

	/** A key press (or a dial push): ignored while that key's last request is still on its way. */
	async keyDown(id) {
		const key = this.keys.get(id);
		if (!key || key.busy) return;
		key.busy = true;
		try {
			if (key.commitTimer) {
				this.timers.clearTimeout(key.commitTimer);
				key.commitTimer = null;
				// Push takes Preview: a lobby still waiting to be staged goes in first. (The player dial's
				// push carries its choice itself.)
				if (key.def.flushFirst) this.stage(key);
			}
			// Take waits for the stage on its way (sent just now, or when the dial rested): Red Alert answers
			// each request on its own, so Take sent beside it could go first. If the stage was refused,
			// nothing is taken: what's in Preview isn't the lobby the dial landed on. The same when it was
			// refused before the push (the strip still shows that lobby, with the alert): no Take.
			if (key.def.flushFirst) {
				if (key.committing && !(await key.committing)) return;
				if (key.pending !== null && key.staged && key.staged.value === key.pending && !key.staged.ok) {
					this.log("Not sent: the dialled lobby wasn't staged, so Take would put the old Preview on.");
					show(key, "showAlert");
					return;
				}
			}
			const ok = await this.run(key, key.def.request(key.settings, this.state, key.pending));
			if (ok && key.pending !== null) this.clearLater(key);
		} finally {
			key.busy = false;
		}
	}

	/** A tap on a dial's touch strip. */
	async touchTap(id) {
		const key = this.keys.get(id);
		if (!key || key.busy || !key.def.touch) return;
		key.busy = true;
		try {
			await this.run(key, key.def.touch(key.settings, this.state));
		} finally {
			key.busy = false;
		}
	}

	/** A dial turn: the choice shows at once and is sent when the dial rests (the replay dial scrubs). */
	dialRotate(id, ticks) {
		const key = this.keys.get(id);
		if (!key || !Number.isFinite(ticks) || ticks === 0) return;
		if (key.def.scrub) {
			key.scrub += ticks;
			this.pumpScrub(key);
			return;
		}
		if (!key.def.rotate) return;
		const next = key.def.rotate(key.settings, this.state, key.pending, ticks);
		if (next === null || next === undefined) return;
		key.pending = next;
		key.staged = null;
		this.timers.clearTimeout(key.clearTimer);
		key.clearTimer = null;
		this.draw(key);
		this.timers.clearTimeout(key.commitTimer);
		key.commitTimer = this.timers.setTimeout(() => {
			key.commitTimer = null;
			this.stage(key);
		}, this.commitDelay);
	}

	/**
	 * Sends a dial's choice once any earlier one for that dial has been answered, so they reach Red Alert
	 * in turn. The answer (whether it was done) stays on the key as `committing` until it comes, for a
	 * push to wait on.
	 */
	stage(key) {
		const before = key.committing;
		const p = (before ? before.then(() => this.commit(key)) : this.commit(key))
			.catch(() => false)
			.then((ok) => {
				if (key.committing === p) key.committing = null;
				return ok;
			});
		key.committing = p;
		return p;
	}

	/**
	 * Sends a dial's choice (Preview lobby, or who the camera follows): whether it was done, true when there
	 * was none. The answer stays on the key (`staged`) while the strip shows that choice, for a push to see.
	 */
	async commit(key) {
		const value = key.pending;
		if (value === null || value === undefined || !key.def.commit) return true;
		const ok = await this.run(key, key.def.commit(key.settings, this.state, value), { ok: false });
		if (key.pending === value) {
			key.staged = { value, ok };
			this.clearLater(key);
		}
		return ok;
	}

	/** Lets Red Alert's state take over from the dial's own choice a moment after it was sent. */
	clearLater(key) {
		this.timers.clearTimeout(key.clearTimer);
		key.clearTimer = this.timers.setTimeout(() => {
			key.clearTimer = null;
			key.pending = null;
			key.staged = null;
			this.draw(key);
		}, this.holdChoice);
	}

	/** Sends the replay dial's gathered turns, one step at a time, at least SCRUB_GAP apart. */
	pumpScrub(key) {
		if (key.scrubbing) return;
		key.scrubbing = true;
		const step = async () => {
			const next = scrubStep(key.scrub);
			if (!next || !this.keys.has(key.id)) {
				key.scrubbing = false;
				return;
			}
			key.scrub -= next.consumed;
			const ok = await this.run(key, { path: `replay/${next.op}`, query: {} }, { ok: false });
			// Nothing to scrub (no replay on stream): drop the rest of the turn rather than alert on every notch.
			if (!ok) key.scrub = 0;
			this.timers.setTimeout(() => step().catch(() => (key.scrubbing = false)), this.scrubGap);
		};
		step().catch(() => (key.scrubbing = false));
	}
}

/** The key's tick or alert triangle (if Stream Deck can't show it, nothing more is done). */
const show = (key, what) => Promise.resolve(key.handle[what]?.()).catch(() => {});

/**
 * What the Property Inspector shows to help fill in a key: the lobbies by number and their players by
 * slot. Names and colours only: the state the plugin reads has no impostor flags in it, and none are
 * passed on.
 */
export function summary(state, status) {
	return {
		status,
		lobbies: lobbies(state)
			.filter((l) => Number.isFinite(l?.no))
			.sort((a, b) => a.no - b.no)
			.map((l) => ({
				no: l.no,
				name: String(l.name ?? ""),
				online: l.online !== false,
				players: players(l).map((p) => ({ slot: p.slot, colour: String(p.colour ?? ""), name: String(p.name ?? ""), dead: Boolean(p.dead) })),
			})),
	};
}
