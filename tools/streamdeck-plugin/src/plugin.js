// Red Alert for Stream Deck: the plugin's entry point. It registers every action, reads the
// connection (host, port, key) from the plugin's global settings, runs the one /deck/state loop that
// all keys are drawn from, and passes presses, dial turns and Property Inspector messages on.

import streamDeck, { SingletonAction } from "@elgato/streamdeck";

import { ACTIONS } from "./actions.js";
import { StatePoller, connectionOf, press, probe } from "./client.js";
import { Deck, summary } from "./deck.js";

const log = streamDeck.logger.createScope("Red Alert");

let conn = connectionOf({});

const deck = new Deck({
	// Only the action's path is logged, never the query (the key lives there).
	send: (path, query) => press(conn, path, query),
	log: (line) => log.info(line),
});

const poller = new StatePoller({
	onState: (state, at) => deck.setState(state, at),
	onStatus: (status, message) => {
		deck.setStatus(status);
		if (status !== "ok") log.info(`Red Alert: ${status}${message ? ` (${message})` : ""}`);
	},
});

/** Stops reading Red Alert's state a little after the last Red Alert key went out of view. */
let idleTimer = null;
function visibleChanged() {
	clearTimeout(idleTimer);
	if (deck.keys.size > 0) poller.start();
	else idleTimer = setTimeout(() => poller.stop(), 10_000);
}

function useConnection(settings) {
	conn = connectionOf(settings);
	poller.setConnection(conn);
	if (!conn.key) deck.setStatus("nokey");
}

/** What a key needs from the SDK, wrapped so the Deck never has to know which kind of key it is. */
function handleFor(action) {
	const isKey = typeof action.isKey === "function" && action.isKey();
	return {
		setImage: (url) => (isKey ? action.setImage(url) : undefined),
		setFeedback: (payload) => (!isKey && typeof action.setFeedback === "function" ? action.setFeedback(payload) : undefined),
		showOk: () => (isKey ? action.showOk() : undefined),
		showAlert: () => action.showAlert(),
	};
}

/** The Property Inspector's requests: test a connection, use a new one, or list the lobbies. */
async function fromInspector(payload) {
	const reply = (message) => streamDeck.ui.current?.sendToPropertyInspector(message).catch(() => {});
	switch (payload?.event) {
		case "test": {
			const result = await probe(connectionOf(payload));
			await reply({ event: "testResult", ...result });
			break;
		}
		case "connection":
			useConnection(payload);
			break;
		case "hello":
			await reply({ event: "summary", ...summary(deck.state, deck.status) });
			break;
		default:
			break;
	}
}

/** One class for every action: what each one does lives in actions.js, keyed by its manifest UUID. */
class RedAlertAction extends SingletonAction {
	constructor(uuid) {
		super();
		this.manifestId = uuid;
	}

	onWillAppear(ev) {
		deck.appear(ev.action.id, handleFor(ev.action), this.manifestId, ev.payload.settings, ev.payload.controller === "Encoder");
		visibleChanged();
	}

	onWillDisappear(ev) {
		deck.disappear(ev.action.id);
		visibleChanged();
	}

	onDidReceiveSettings(ev) {
		deck.settings(ev.action.id, ev.payload.settings);
	}

	onKeyDown(ev) {
		return deck.keyDown(ev.action.id);
	}

	onDialDown(ev) {
		return deck.keyDown(ev.action.id);
	}

	onDialRotate(ev) {
		deck.dialRotate(ev.action.id, ev.payload.ticks);
	}

	onTouchTap(ev) {
		return deck.touchTap(ev.action.id);
	}

	onSendToPlugin(ev) {
		return fromInspector(ev.payload);
	}

	/** The inspector opened: give it the lobbies and players straight away (it asks again too). */
	onPropertyInspectorDidAppear() {
		return fromInspector({ event: "hello" });
	}
}

for (const action of ACTIONS) streamDeck.actions.registerAction(new RedAlertAction(action.uuid));

streamDeck.settings.onDidReceiveGlobalSettings((ev) => useConnection(ev.settings));

streamDeck
	.connect()
	.then(() => streamDeck.settings.getGlobalSettings())
	.then((settings) => useConnection(settings))
	.catch((e) => log.error(`Couldn't start: ${e?.message ?? e}`));
