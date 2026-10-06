# The broadcast feed (protocol v1)

How a host's game talks to the broadcast app (TT Broadcast), and how the broadcast app talks back.
The definitions in code are `src/TournamentTracker.Core/Broadcast/FeedProtocol.cs`; the mod writes
the feed in `LobbyFeed.cs` / `SpectatorView.cs`; the broadcast app reads it in `CasterDesk`,
`LobbyBoard`, `Tracks` and `GameArchive`.

## The path

```
Among Us + mod ──(local control port, /feed)──▶ host's Button ──(send page, VDO.Ninja data channel)──▶ TT Broadcast
                ◀──────── "spec …" commands ──── /app/command ◀──────────── ttc / ttn / ttack ─────────
```

* The mod keeps the last 600 messages; the host's Button page polls them (`/app/sendfeed?since=N`)
  and sends them over VDO.Ninja with the screen share: `postMessage({sendData:{tt:[…]}, type:'pcs'})`.
  Only someone with the stream's password (the caster) receives them.
* TT Broadcast joins each host's stream with a hidden, data-only VDO.Ninja view (`&novideo&noaudio`),
  takes `tt` messages from it, and answers on the same link with `type:'rpcs'`.

## Rules

* **Every message has `v`** (this is version **1**). A message without `v` is version 0, from a mod
  older than the broadcast app: it is still used, and the lobby is marked **host needs update**.
* **Readers ignore fields and kinds they don't know.** New fields can be added without a new version;
  raise the version only when a field changes meaning or a reader has to know about something new.
* **Each message once, in order:** messages from the mod have `src` (this run of the mod) and `seq`
  (1, 2, 3…). The host's page keeps each one until the caster acks it, and sends unacked ones again
  (with `re: true`) after a drop. The caster drops repeats and waits for gaps (up to 30 s).
* **Late is not live:** a message more than `StaleSeconds` (8 s) old when it arrives counts for the
  stats (with its own time) but is never shown as happening now.
* Times: `t` is milliseconds since 1970 on the host's clock; the caster works out each host's clock
  offset from arrivals. `clock` is seconds into the game.

## Every message

| Field | Type | |
|---|---|---|
| `v` | int | protocol version (1) |
| `type` | string | `event`, `snap`, `track` (from the mod); `voice`, `health`, `skip` (from the host's page) |
| `kind` | string | events only: see below |
| `lobby` | string | the host's lobby label, else the lobby code |
| `round` | int | tournament round |
| `game` | string? | `"LJ-3"`; null outside a game |
| `t` | long | ms since 1970, host's clock |
| `clock` | number? | seconds into the game |
| `src` | string | mod only: this run of the mod (changes when Among Us restarts) |
| `seq` | long | mod only: message number within `src` |
| `re` | bool | set by the host's page when it sends a message again |

### Player objects

* **who** (in events): `{id, name, display, color, colorName, imp}`: `display` is the name from the
  Discord linking (or the in-game name), `color` the colour id (0 red … 17 coral), `imp` impostor.
* **roster entry** (`players` in `snap`, `roster` in `gameStart`): `{id, name, display, color, key,
  discord, imp, dead}`: `key` is the player's key (friend code style), `discord` their linked
  Discord id or null, `imp`/`dead` null outside a game.

The caster's tools show impostors; nothing here ever goes to players.

## Events (`type: "event"`)

| `kind` | Fields |
|---|---|
| `gameStart` | `map`, `players` [who], `roster` [roster entry], `crewAlive`, `impAlive` |
| `kill` | `killer`, `victim` (who), `room`, `crewAlive`, `impAlive`, `winning` (this kill ends it), `pos {x,y}` (world), `screen {x,y,onScreen}` (0–1 on the host's screen) |
| `meeting` | `caller`, `body` (who or null), `emergency` |
| `eject` | `votes` [{voter, target, skipped}] (player ids), `ejected` (who or null), `wasImpostor`, `skipped`, `tie`, `crewAlive`, `impAlive` |
| `tasks` | `pct` (task bar 0–100) |
| `gameEnd` | `winner` (`Impostors`/`Crewmates`/null), `reason`, `how` (`kills`, `vote`, `tasks`, `sabotage`, …), `abandoned`, `crewAlive`, `impAlive`, `impostors` [who] |
| `vent` | `player` (who), `action` (`enter`/`exit`), `room`, `pos {x,y}` |
| `sabotage` | `system`, `state` (`start`/`fixed`), `critical`, `timeLeft`, `fixing` (0–1), `by` (who, start only, when known) |
| `killReady` | `impostor` (who), `estimated` (the host can't see other players' timers) |
| `danger` | `state` (`start`/`end`), `impostor`, `crewmate` (who), `room`, `distance` (start only) |

## Snapshot (`type: "snap"`, once a second)

| Field | |
|---|---|
| `phase` | `menu`, `lobby`, `ingame`, `meeting`, `ended` |
| `map` | |
| `crewAlive`, `impAlive`, `alive`, `taskPct` | null outside a game |
| `sabotage` | `{system, critical, timeLeft, fixing}` or null (the most urgent one) |
| `killReady` | player ids whose kill is (estimated) ready |
| `danger` | an impostor is alone with a crewmate right now |
| `video` | the host is sending their game |
| `spec` | spectator view: `{lit, vision (off/focus/rings), report, eye, focus, focusing, on}` |
| `players` | [roster entry] |
| `ifEnded` | each possible ending's points for every player (WhatIf), for "points on the line" |

## Positions (`type: "track"`, once a second)

`samples`: `[{t, p: [[id, x, y, inVent], …]}, …]`: x and y in thousandths of the host's screen
(may be off screen), sampled 5 times a second.

## From the host's page

| `type` | Fields |
|---|---|
| `voice` | `on`, `sending`, `problem`, `discord`, `game` (capture states), `voiceDb`, `gameDb`, `mic` |
| `health` | `video` (`ok`/`lost`/`unknown`, from VDO.Ninja's stats), `queued` (messages waiting to send) |
| `skip` | `src`, `seq`: a snap or track the page dropped because it got too old while the link was down |

## Back to the host (`type: "rpcs"` on the same link)

| Key | Value | The host's page… |
|---|---|---|
| `ttc` | `"spec lit on"`… (only `^spec [a-z]+( [a-z0-9.]+)?$`) | passes it to the mod (`/app/command`) |
| `ttn` | `{playerKey: "Real Name"}` | gives the mod roster names for nameplates (`/app/names`) |
| `ttack` | `{src: lastSeq}` | stops sending those again |

Spectator commands: `spec lit|report|eye on|off`, `spec vision off|focus|rings`,
`spec focus auto|<player id>`, `spec dim 0.22`.
