// A /deck/state document as Red Alert sends it (no impostor flags: the plugin never asks for them).

export function sampleState() {
	return {
		rev: 7,
		obs: { connected: true, streaming: true, recording: false, problem: null },
		program: { layout: "full", slots: ["Lobby 3"], confirmed: true, switching: null },
		preview: { layout: "2up", slots: ["Lobby 1", "Lobby 5"] },
		voice: { muteAll: false, heard: "Lobby 3", listen: null, cue: true, cueLobby: "Lobby 1" },
		replay: { onAir: false, playing: false, speed: 100, ready: 2, last: { id: "r9", lobby: "Lobby 2", title: "Red kills Blue" } },
		hold: true,
		auto: { grid: false, intermission: true, switch: false },
		graphics: { standings: true, alertsPaused: false, now: null, waiting: 0, prompts: 0 },
		lobbies: [
			{ no: 1, name: "Lobby 1", online: true, phase: "ingame", tier: "high", health: "green", tally: "pvw", crew: 7, imps: 2, tasks: 30, sabotage: null, cam: { on: true, sending: true, following: null, pick: null, armed: false }, players: [] },
			{
				no: 3, name: "Lobby 3", online: true, phase: "ingame", tier: "veryHigh", health: "yellow", tally: "pgm", crew: 4, imps: 1, tasks: 72.6,
				sabotage: { system: "Reactor", left: 20 },
				cam: { on: true, sending: true, following: 21, pick: 21, armed: false },
				players: [
					{ slot: 2, id: 22, colour: "blue", color: 1, name: "Bea", dead: true },
					{ slot: 1, id: 21, colour: "red", color: 0, name: "Ann", dead: false },
					{ slot: 3, id: 23, colour: "lime", color: 11, name: "Cy <&>", dead: false },
					{ slot: 4, id: 24, colour: "black", color: 6, name: "Dee", dead: false },
				],
			},
			{ no: 4, name: "Lobby 4", online: false, phase: "menu", tier: "low", health: "red", tally: "none", crew: 0, imps: 0, tasks: 0, sabotage: null, cam: { on: false, sending: false, following: null, pick: null, armed: false }, players: [] },
			{
				no: 5, name: "Lobby 5", online: true, phase: "meeting", tier: "low", health: "green", tally: "pvw", crew: 5, imps: 2, tasks: 10, sabotage: null,
				cam: { on: true, sending: true, following: 51, pick: 52, armed: true },
				players: [
					{ slot: 1, id: 51, colour: "pink", color: 3, name: "Eve", dead: false },
					{ slot: 2, id: 52, colour: "cyan", color: 10, name: "Fox", dead: false },
				],
			},
		],
	};
}
