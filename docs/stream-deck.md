# Red Alert on a Stream Deck

Red Alert has its own Stream Deck plugin. Every key shows what is really happening, not just a
label: a lobby key lights red while that lobby is on stream (Program) and green while it waits in
Preview, shows the phase, crew v impostors and a flashing sabotage timer; a player camera key shows
the crewmate in its colour with the player's name, dimmed when dead, with a ring while the camera
follows them; toggles show whether they are on. Player cameras, Preview and Take, replays, breaks,
panic, graphics and lobby voice are all one press away.

The keys never show who the impostors are: the plugin never asks Red Alert for impostor flags, so a
Stream Deck that ends up on camera or in a photo at a LAN gives nothing away.

## What you need

* Red Alert open on the same PC (it only answers this PC), a version with the Stream Deck state
  (the keys say **UPDATE RED ALERT** if it's older).
* The Stream Deck app 6.5 or newer on Windows 10 or 11. Any Stream Deck works: 15-key (MK.2),
  XL, Mini, Neo, Stream Deck + (with dials) or the mobile app.

## Install

1. Download **RedAlert.streamDeckPlugin** from the newest Red Alert release (the
   `broadcast-v…` releases on GitHub, next to RedAlert.exe).
2. Double-click it. The Stream Deck app asks to install **Red Alert**; say yes. The actions appear
   in the action list under **Red Alert**.

To update, download the file from a newer release and double-click it again: your keys and their
settings stay. The plugin's version is the Red Alert version it came with.

## Pair it with Red Alert

1. In Red Alert open **Settings → Stream Deck**. It shows the **port** (8768 unless that was busy)
   and the Stream Deck **key**.
2. Drag any Red Alert action onto a key and click it. At the bottom of its settings panel open
   **Red Alert connection**, check the host (**127.0.0.1**) and port, paste the key and click
   **Test**. It says how many lobbies Red Alert has and whether OBS is connected, or what's wrong.

The connection is shared by every Red Alert key, so you set it once. **New key** in Red Alert makes
a new key; paste it again here.

While something is wrong every key shows a strip across the top, over a faded picture of the last
thing it knew:

| Strip | Meaning |
|---|---|
| SET UP | No key yet: pair it as above. |
| WRONG KEY | Red Alert answered but the key doesn't match. Copy it again. |
| OFFLINE | Nothing answered on that port. Is Red Alert open? Did the port change? |
| UPDATE RED ALERT | This Red Alert has no Stream Deck state: install the newest one. |
| RED ALERT LOCKED | Open Red Alert and type the administration code. |

## How the keys behave

* **One connection for all keys.** The plugin keeps one request open to Red Alert
  (`/deck/state`), and Red Alert answers the moment anything changes, so every key updates at once.
  It stops asking when no Red Alert key is on screen.
* **A tick or a triangle.** After a press the key shows a tick when Red Alert did it (or queued it)
  and the alert triangle when it couldn't, for example a camera that isn't sending or OBS not
  connected. The reason is in Red Alert's Stream Deck log and the plugin's log.
* **No double presses.** A key ignores presses while its last one is still on its way, and Red
  Alert ignores the same press twice within 300 ms.
* **Meetings arm cameras.** Picking a player while their lobby is in a meeting, in the lobby or
  between games doesn't put a frozen camera on stream: the pick is **ARMED** (amber badge) and goes
  live by itself when play resumes.
* **Lobby numbers stay put.** Lobby 3 is always the same lobby during an event: Red Alert keeps a
  list of lobby names and their numbers next to its desk settings, and uses the number at the end of
  a lobby's name when it's free.

## The actions

Every action has its settings in the panel beside the key. The path is what the key sends to Red
Alert (`/deck/<path>`), for reference and for Companion below.

| Action | Settings | What a press does | Path |
|---|---|---|---|
| **Lobby** | lobby 1, 2, 3… or next, previous, where the action is; **Press**: cut it to stream, put it in Preview, or add it to Preview; transition (cut or swoosh) | A lobby full screen now, or into Preview. Next and previous step through the live lobbies. Add builds a 2-up or quad in Preview, one lobby per press. | `lobby/3`, `lobby/next`, `lobby/action`, `pvw/lobby/3`, `pvw/add/3` |
| **Player cam** | **Lobby**: on stream, in Preview, or a number; **Player**: slot 1–15, a colour, next, previous, auto; **Press**: cut, or arm | Cut to that player's camera, or (arm) set who the camera follows without cutting. | `cam/pgm/slot2`, `cam/3/red`, `cam/pvw/next?arm=1` |
| **Take** | take as (the layout in Preview, or full, 2-up, quad, grid, cameras); transition | Preview goes on stream. | `take`, `take?as=quad&t=swoosh` |
| **Wide** | | Back to the whole map from a player camera. | `wide` |
| **Cut to cam** | lobby: on stream, in Preview, or a number | That lobby's camera on stream, still following whoever it follows. | `cam/cut`, `cam/3/slot2` |
| **Layout** | full, 2-up, quad, grid, cameras | That layout on stream. Lit red while on stream, green while in Preview. | `layout/quad` |
| **Replay** | play last, replay that, back to live, play/pause, back, forward, frame back, frame forward, restart, zoom in, zoom out, follow, angle, speed | **Replay that** saves the last seconds of the lobby on stream and plays them; **Play last** plays the newest ready clip; **Speed** steps 100 / 50 / 25%. | `replay/that`, `replay/last`, `replay/slow`… |
| **Toggle** | mute all lobby voice, hold, auto grid, auto intermission, auto switch, standings, alerts; **Press**: switch it, always on, always off | Shows ON / OFF (MUTED, HELD, PAUSED) from Red Alert's own state. **Hold** stops all automatic switching. | `voice/mute/toggle`, `hold/on`, `auto/grid/off`, `gfx/alerts/toggle`… |
| **Break** | be right back, intermission, sponsor, end the break | That break screen, or back from it. | `break/brb`, `break/off` |
| **Panic** | | One press: fade to Be right back and mute lobby voice. | `panic` |
| **Custom path** | path (with a list of common ones), key text | Any other Red Alert Stream Deck path: skip or clear graphics, the player card, after-game prompts, undo, listen to one lobby, the host's view modes, player cameras on/off. | `gfx/skip`, `undo`, `voice/listen/2`, `spec/vision`… |

**Player slots.** Slot 1 to 15 are a lobby's players, living and dead, in colour order. A slot keeps
its player for the whole game, so a row of slot keys is a fixed row of player cameras: each shows
the player's crewmate and name, goes dim when they die and gets a ring (red while on stream) when the
camera follows them. A colour key follows whoever wears that colour.

**Lobby: on stream / in Preview.** A camera key set to *the lobby on stream* always works on
whatever is on Program, so one row of slot keys covers every lobby. Set it to *the lobby in
Preview* to line up the next player before you Take, or to a number for a key that always belongs
to one lobby.

### Stream Deck + dials

| Dial | Turn | Push | Tap the strip |
|---|---|---|---|
| **Preview dial** | Pick the lobby in Preview (sent when the dial stops) | Take (as set in its panel) | Clear Preview |
| **Player dial** (on stream, in Preview, or a lobby number) | Pick who the camera follows, living players only (armed, no cut) | Cut to that camera | Back to the whole map |
| **Replay dial** | Scrub: slowly a frame at a time, quickly by seconds | Play / pause | Speed 100 / 50 / 25% |

## Recommended layouts

Stream Deck profiles are binary files, so the plugin doesn't bring any; these take a few minutes to
set up by dragging actions. Use the Stream Deck's own pages or folders for the second page.

**15 keys (Stream Deck MK.2), page 1: switching**

| | | | | |
|---|---|---|---|---|
| Lobby 1 (Preview) | Lobby 2 (Preview) | Lobby 3 (Preview) | Lobby 4 (Preview) | Take |
| Player cam: on stream, prev | Player cam: on stream, next | Cut to cam | Wide | Layout: quad |
| Replay that | Replay: back to live | Toggle: mute all | Toggle: hold | Panic |

**15 keys, page 2: player cameras** (the lobby on stream)

| | | | | |
|---|---|---|---|---|
| Slot 1 | Slot 2 | Slot 3 | Slot 4 | Slot 5 |
| Slot 6 | Slot 7 | Slot 8 | Slot 9 | Slot 10 |
| Player cam: auto | Wide | Cut to cam | Replay that | (page back) |

**32 keys (Stream Deck XL)**

| | | | | | | | |
|---|---|---|---|---|---|---|---|
| Lobby 1 | Lobby 2 | Lobby 3 | Lobby 4 | Lobby 5 | Lobby 6 | Lobby: action | Take |
| Slot 1 | Slot 2 | Slot 3 | Slot 4 | Slot 5 | Slot 6 | Slot 7 | Slot 8 |
| Slot 9 | Slot 10 | Slot 11 | Slot 12 | Player cam: auto | Cut to cam | Wide | Layout: cameras |
| Layout: full | Layout: quad | Layout: grid | Replay that | Replay: back to live | Toggle: mute all | Toggle: hold | Panic |

Lobby keys in Preview mode with a Take key give the vMix-style "line it up, then take it" flow;
set them to Cut if you'd rather switch at once. A second page can hold the breaks, standings,
alerts, auto switch, sponsor break and the replay transport.

**Stream Deck +** (8 keys, 4 dials)

| | | | |
|---|---|---|---|
| Lobby 1 (Preview) | Lobby 2 (Preview) | Lobby 3 (Preview) | Lobby 4 (Preview) |
| Cut to cam | Replay that | Toggle: mute all | Panic |

Dials, left to right: **Preview dial**, **Player dial** (the lobby on stream), **Player dial**
(the lobby in Preview), **Replay dial**.

## Without the plugin: Companion or the Website action

Every action is also a plain web link on this PC, for Bitfocus Companion (its Generic HTTP module),
the Stream Deck's own **Website** action (tick **GET request in background**) or anything else that
can open a URL:

```
http://127.0.0.1:8768/deck/<path>?key=<your key>
```

GET and POST both work. Red Alert answers with JSON, `{"ok":true,"message":"…"}`, and a status:
200 done, 202 queued or armed, 401 wrong key, 404 unknown action or lobby, 409 refused (for example
the camera isn't available), 423 locked, 503 OBS not connected. The same link twice within 300 ms is
ignored. Settings → Stream Deck in Red Alert lists ready-made links with your key in them.

| Path | Does |
|---|---|
| `lobby/<n>`, `lobby/next`, `lobby/prev`, `lobby/action`, `lobby/<name>` (add `?t=cut` or `?t=swoosh`) | A lobby full screen now |
| `pvw/lobby/<n>`, `pvw/add/<n>`, `pvw/clear` | Preview: one lobby, add a lobby, clear it |
| `take` (add `?as=` full, 2up, quad, grid or cams, and `&t=cut` or `&t=swoosh`) | Preview on stream |
| `layout/full`, `layout/2up`, `layout/quad`, `layout/grid`, `layout/cams` | A layout on stream |
| `cam/<lobby>/<who>` (add `?arm=1` to arm) | A player camera. `<lobby>` is a number, `pgm` or `pvw`; `<who>` is a colour, `slot1`…`slot15`, `next`, `prev` or `auto` |
| `cam/cut`, `wide` | The camera on stream; back to the whole map |
| `replay/last`, `replay/that`, `replay/live`, `replay/toggle`, `replay/back`, `replay/forward`, `replay/frameback`, `replay/frameforward`, `replay/restart`, `replay/zoomin`, `replay/zoomout`, `replay/follow`, `replay/angle`, `replay/slow` | Replays |
| `break/brb`, `break/intermission`, `break/sponsor`, `break/off`, `panic` | Breaks |
| `hold/on`, `hold/off`, `hold/toggle` | Hold: no automatic switching |
| `auto/grid/…`, `auto/intermission/…`, `auto/switch/…` (`on`, `off`, `toggle`), `undo` | Automatic switching |
| `gfx/standings/…`, `gfx/alerts/…` (`on`, `off`, `toggle`), `gfx/skip`, `gfx/clear`, `gfx/card` | Graphics |
| `prompt/show`, `prompt/skip` | After-game prompts |
| `voice/mute/on`, `voice/mute/off`, `voice/mute/toggle`, `voice/listen/<n>`, `voice/listen/off` | Lobby voice |
| `spec/lit`, `spec/vision`, `spec/eye` | The host's view modes |
| `mute`, `quad`, `grid`, `cam`, `cam/on`, `cam/off`, `cam/<who>` | Older links, still working |

For feedback on keys, `/deck/state?key=…` is the JSON the plugin reads (add `&since=<rev>` to wait
up to 25 s for a change), and `/deck/img/lobby/<n>.svg?key=…`, `/deck/img/cam/<n>/slot<k>.svg?key=…`
and `/deck/img/toggle/<name>.svg?key=…` are ready-made 144 × 144 key pictures for tools that can show
an image from a URL.

## Building the plugin

The plugin is in `tools/streamdeck-plugin` (Node.js, Elgato's `@elgato/streamdeck` SDK). It uses SDK
1.x, which runs on the Node 20 that Stream Deck 6.5 and newer bring with them; SDK 2 needs Node 24
and Stream Deck 7.1.

```
cd tools/streamdeck-plugin
npm ci
npm test            # the key pictures and what each key sends, without a Stream Deck
npm run build       # bin/plugin.js and the pictures, into com.ljbutton.redalert.sdPlugin
npm run pack        # dist/com.ljbutton.redalert.streamDeckPlugin
```

To try changes on a Stream Deck, `npx streamdeck link com.ljbutton.redalert.sdPlugin` once, then
`npm run build` and `npx streamdeck restart com.ljbutton.redalert`. The plugin's log is in the
plugin's `logs` folder (`%APPDATA%\Elgato\StreamDeck\Plugins\com.ljbutton.redalert.sdPlugin\logs`);
it has the path of each press and Red Alert's answer, never the key.

CI builds and tests it on every push, uploads RedAlert.streamDeckPlugin with the other files, and
attaches it to each `broadcast-v…` release.
