import assert from "node:assert/strict";
import { test } from "node:test";

import {
	camChoice,
	camOnProgram,
	findPlayer,
	fit,
	livingPlayers,
	normLayout,
	onlineLobbies,
	playerHex,
	previewLobby,
	programLobby,
	sabotageLeft,
	stepLobby,
	stepPlayer,
	targetLobby,
	toggleOn,
} from "../src/model.js";
import { sampleState } from "./fixtures.js";

test("Program and Preview lobbies come from the slots, else from the tally", () => {
	const s = sampleState();
	assert.equal(programLobby(s).no, 3);
	assert.equal(previewLobby(s).no, 1);
	s.program.slots = [];
	s.preview.slots = [null];
	assert.equal(programLobby(s).no, 3, "falls back to the red tally");
	assert.equal(previewLobby(s).no, 1, "falls back to the first green tally");
	assert.equal(programLobby(null), null);
});

test("a key's target is pgm, pvw or a lobby number", () => {
	const s = sampleState();
	assert.equal(targetLobby(s, "pgm").no, 3);
	assert.equal(targetLobby(s, "pvw").no, 1);
	assert.equal(targetLobby(s, 5).no, 5);
	assert.equal(targetLobby(s, "5").no, 5);
	assert.equal(targetLobby(s, 9), null);
});

test("next and previous lobby skip offline lobbies and wrap round", () => {
	const s = sampleState();
	assert.deepEqual(onlineLobbies(s).map((l) => l.no), [1, 3, 5]);
	assert.equal(stepLobby(s, 3, 1).no, 5);
	assert.equal(stepLobby(s, 5, 1).no, 1);
	assert.equal(stepLobby(s, 1, -1).no, 5);
	assert.equal(stepLobby(s, 1, 2).no, 5);
	assert.equal(stepLobby(s, 4, 1).no, 1, "from an offline lobby: the first");
	assert.equal(stepLobby(s, null, -1).no, 5, "from nothing, backwards: the last");
	assert.equal(stepLobby({ lobbies: [] }, 1, 1), null);
});

test("players are found by slot (with or without the word) and by colour", () => {
	const lobby = sampleState().lobbies[1];
	assert.equal(findPlayer(lobby, "slot1").name, "Ann");
	assert.equal(findPlayer(lobby, "2").name, "Bea");
	assert.equal(findPlayer(lobby, "SLOT3").name, "Cy <&>");
	assert.equal(findPlayer(lobby, "lime").name, "Cy <&>");
	assert.equal(findPlayer(lobby, "Black").name, "Dee");
	assert.equal(findPlayer(lobby, "slot9"), null);
	assert.equal(findPlayer(lobby, "purple"), null);
	assert.equal(findPlayer(null, "slot1"), null);
});

test("the player dial steps through the living only, in slot order, wrapping", () => {
	const lobby = sampleState().lobbies[1];
	assert.deepEqual(livingPlayers(lobby).map((p) => p.slot), [1, 3, 4]);
	assert.equal(stepPlayer(lobby, 21, 1).id, 23, "skips dead Bea");
	assert.equal(stepPlayer(lobby, 24, 1).id, 21);
	assert.equal(stepPlayer(lobby, 21, -1).id, 24);
	assert.equal(stepPlayer(lobby, 22, 1).id, 21, "from a dead player: the first living");
	assert.equal(stepPlayer({ players: [] }, null, 1), null);
});

test("the camera's choice is its pick first, else who it follows", () => {
	const s = sampleState();
	assert.equal(camChoice(s.lobbies[3]).name, "Fox");
	s.lobbies[3].cam.pick = null;
	assert.equal(camChoice(s.lobbies[3]).name, "Eve");
	assert.equal(camChoice(s.lobbies[0]), null);
});

test("a camera is on stream only in a cam layout with its lobby in it", () => {
	const s = sampleState();
	assert.equal(camOnProgram(s, s.lobbies[1]), false);
	s.program.layout = "cam";
	assert.equal(camOnProgram(s, s.lobbies[1]), true);
	s.program.layout = "cams";
	assert.equal(camOnProgram(s, s.lobbies[0]), false);
});

test("layout names fold to the five keys", () => {
	assert.equal(normLayout("4up"), "quad");
	assert.equal(normLayout("cam"), "cams");
	assert.equal(normLayout("2up"), "2up");
	assert.equal(normLayout(undefined), "none");
});

test("the sabotage timer counts down from when the state arrived", () => {
	const lobby = sampleState().lobbies[1];
	assert.equal(sabotageLeft(lobby, 1000, 1000), 20);
	assert.equal(sabotageLeft(lobby, 1000, 6000), 15);
	assert.equal(sabotageLeft(lobby, 1000, 99_000), 0);
	assert.equal(sabotageLeft(sampleState().lobbies[0], 0, 0), null);
});

test("toggles read the state, and say nothing when it doesn't", () => {
	const s = sampleState();
	assert.equal(toggleOn(s, "mute"), false);
	assert.equal(toggleOn(s, "cue"), true, "Hear Preview");
	assert.equal(toggleOn({ voice: { muteAll: false } }, "cue"), null, "an older Red Alert doesn't say");
	assert.equal(toggleOn(s, "hold"), true);
	assert.equal(toggleOn(s, "autogrid"), false);
	assert.equal(toggleOn(s, "autointermission"), true);
	assert.equal(toggleOn(s, "standings"), true);
	assert.equal(toggleOn(s, "alerts"), true, "alerts on = not paused");
	s.graphics.alertsPaused = true;
	assert.equal(toggleOn(s, "alerts"), false);
	assert.equal(toggleOn({}, "mute"), null);
	assert.equal(toggleOn(null, "hold"), null);
	assert.equal(toggleOn(s, "nonsense"), null);
});

test("player colours come from the colour number, else the name", () => {
	assert.equal(playerHex({ color: 0 }), "#c51111");
	assert.equal(playerHex({ colour: "lime" }), "#50ef39");
	assert.equal(playerHex({ colour: "nope" }), "#4a4a54");
	assert.equal(playerHex(null), "#4a4a54");
});

test("names are cut by whole characters, emoji included", () => {
	assert.equal(fit("Short", 10), "Short");
	assert.equal(fit("Supercalifragilistic", 10), "Supercali…");
	assert.equal(fit("🔥🔥🔥🔥", 3), "🔥🔥…");
	assert.equal(fit("bad\u0007name", 20), "badname");
	assert.equal(fit(null, 5), "");
});
