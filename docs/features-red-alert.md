# Red Alert: features

**Red Alert** is the Windows app for whoever runs the stream: the caster or producer. It runs on
the streaming PC next to OBS, receives every lobby whose host has turned on **Send my game to the
caster** in The Button, ranks what is happening, switches OBS, and runs the replays, montages,
on-stream graphics, lobby voice, sponsors and Twitch. Hosts, referees and the organiser don't need
it. This page describes **Red Alert 0.3.19** (released with The Button v0.1.67). For setup steps,
see the [README](../README.md#red-alert-the-broadcasters-app).

> **Red Alert shows who the impostors are.** Kill cards name the killer, the player cams mark
> impostors, the host view and the game replay viewer show them. Keep Red Alert's window on the
> caster's PC only: never capture it in OBS, never share it on a call, never let a player see the
> screen. The Stream Deck plugin is the exception: its keys never show impostors.

## At a glance

* **A vMix-style Live desk on one screen:** a status bar, Preview, Take and Program, a live tile
  per lobby, and the suggestions, the player cams and the graphics queue below.
* **Switching OBS for you:** full screen, 2-up, quad, grid and player cameras, with a cut, swoosh
  or fade, plus breaks, Hold, Panic and Undo.
* **Replays and montages:** a clip of every kill and big moment, "Replay that" on any lobby, slow
  motion, a second angle from the player camera, and montages built after each game and round.
* **On-stream graphics:** one transparent page with labels, impostor tags, sabotage countdowns,
  standings, a stats ticker, player cards and off-screen alerts.
* **Lobby voice and health:** each lobby's Discord voice and game sound on stream, following the
  picture, the lobby in Preview in your headphones, and every lobby shown green, yellow or red with
  automatic switching away from a drop.
* **Audience and sponsors:** Twitch predictions, polls, !sus votes and channel points; sponsor
  placements with read reminders and a proof-of-delivery report.
* **A Stream Deck plugin** with live keys (red and green tally, phase, player names), dials for
  the Stream Deck +, and plain links for Companion or decks without the plugin.
* **Team watch:** a Watch page for the production team on their own PCs on the network, behind a
  watch code, with **Show this** suggestions that land on the Live desk.
* **Safe to rehearse:** a simulation of four fake lobbies, a dry-run checklist and a one-click
  support bundle.

## Install, unlock and updates

* **Installer** — `Install-RedAlert.bat` from the [Red Alert releases](https://github.com/Ljbutton/AU/releases?q=broadcast-v) (tags `broadcast-v…`); installs for this Windows user (no admin) to `%LOCALAPPDATA%\Programs\RedAlert` with Start menu and desktop shortcuts, and can sit next to The Button. It works from any folder, even one with an apostrophe in its name.
* **One copy at a time** — starting Red Alert again brings the open window to the front.
* **Window** — opens maximised the first time, then where and as big as you left it; keeps running at full speed when minimised or covered (by an OBS projector, say), so host commands and camera picks still go out.
* **Page crash** — if the window's page crashes or stops responding (for 10 s), Red Alert loads it again (with a new browser if WebView2's own closed), at most 3 times a minute, and writes it in `red-alert.log` ("Window: the page's renderer crashed…"); past that the window says "Red Alert's page stopped working 4 times in a minute, so it isn't being loaded again." and asks you to close and open Red Alert (your settings are kept). While the page is gone or just back, lobbies without data aren't treated as down (see [Lobby health and drops](#lobby-health-and-drops)).
* **Administration code lock** — Red Alert opens locked; paste the tournament's administration code (the organiser's, as in The Button's Organiser tab) to unlock. Settings → Administration code has **Change** and **Lock**.
* **Check a host's setup code** — Settings → Administration code: paste a host's code to see whether their live data goes to the channel Red Alert reads, on the same server, with a bot Red Alert can read.
* **First start** — on a PC that had The Button's caster tools, Red Alert copies their settings once (OBS, priority rules, roster, graphics, sponsors, alerts, health, Twitch, kept games, ffmpeg, the administration code) and never overwrites its own.
* **Formerly TT Broadcast** — installing Red Alert replaces TT Broadcast, and its settings come over the first time.
* **Updates** — Settings → Updates says when a new Red Alert is out (it looks every 30 minutes, or **Check now**); **Update** downloads it and **Restart Red Alert** starts it. It never updates by itself, so nothing changes mid-stream. The download is checked against the size and SHA-256 its release lists: one that doesn't match is refused and nothing changes ("The download didn't match the release (it may be incomplete). Nothing was changed; try again later.").
* **Log** — `red-alert.log` in `%LOCALAPPDATA%\RedAlert` records what Red Alert did and what went wrong (an error is logged and Red Alert carries on); past 1 MB it moves to `red-alert.log.1`.
* **Settings files** — everything is in `%LOCALAPPDATA%\RedAlert` (`settings.json`, `obs.json`, `caster-priority.json`, `lobby-numbers.json`, `roster.csv`, `broadcast.json`, `sponsors.json`, `health.json`, `twitch.json`, `lobbies-cache.json`…); edited files are re-read while Red Alert runs.
* **Back after a restart** — Hold, auto grid, the next-round countdown and the standings' scope are kept (`settings.json`; a countdown or timed hold that ran out meanwhile isn't brought back). The lobbies and their links last read come back at once, marked **CACHED** on their tiles until the results channel confirms them (`lobbies-cache.json`, without players or impostors; only for the same results channel and lobbies seen in the last 18 hours; deleted on **Lock**; never in a support bundle).
* **Saved safely** — every file is written whole to a side file first and only then takes the old one's place, so a crash, a power cut or a full disk part-way through leaves the old file as it was. A file that can't be read (half-written, or edited wrongly by hand) is kept as `<name>.corrupt-<time>` before anything is saved over it, and `red-alert.log` says so; a hand-edited `broadcast.json` with a mistake is kept that way before a switch saves the last good settings.

## The side menu

One page per job. On the Live desk the menu shrinks to a narrow rail of short names. A dot beside
an item shows what needs a look; a number shows a count.

| Page | Rail | What's on it | Badge |
|---|---|---|---|
| **Live desk** | LIVE | Everything for the show on one screen | New suggestions from your team since you last looked; a red dot for a Discord problem |
| **Lobby health** | HLTH | Each lobby's health, interrupted games, kept game replays | Green, yellow or red dot |
| **Montages** | MONT | Montages, Moments and the Archive | Montages ready |
| **Graphics** | GFX | What the on-stream page shows, and the theme | |
| **Standings** | TABLE | Standings tables and storyline notes | |
| **Sponsors** | SPON | Placements, breaks and the report | |
| **Twitch** | TWCH | Predictions, polls, !sus, Chat Detective, channel points | Dot while connected |
| **OBS** | OBS | Connection, TT scenes, swoosh, setup check, simulation | Dot: connected or lost |
| **Lobby voice** | VOX | Each lobby's voice and game sound on stream | Dot on a problem |
| **Players** | NAMES | Real names from `roster.csv`, matching by hand | |
| **Settings** | SET | Caster overlay, administration code, player camera, Team watch, Stream Deck, updates, help | Dot when an update is out |

* **Help behind ?** — each card's explanation opens from its small **?**.
* **No silent buttons** — anything that goes wrong says why, in a toast.

## Live desk: the status bar

* **On air box** — what OBS is showing: **PROGRAM** (red) with the layout and lobbies, **BREAK** (amber), **REPLAY**, **OFF AIR**, **EMPTY** (a layout with no lobby in it) or **SWITCHING**; with who switched ("by hand" from the desk or the Stream Deck, "switched by itself", "switched by itself (a lobby dropped)" or "switched in OBS") and the scene ("scene TT Full"); the whole line is also its tooltip.
* **Follows OBS** — a switch made in OBS by hand shows here; a switch OBS doesn't make within about 2.5 s turns into a red "OBS didn't switch" error.
* **Stream and recording** — STREAMING with its time, RECONNECTING, NOT STREAMING; REC with its time, REC PAUSED, NOT RECORDING.
* **AUDIO pill** — whose lobby voice the stream hears, with a meter; red and flashing while every lobby is muted; click to mute or unmute all.
* **OBS pill** — OFF, OK, CPU % or dropped frames %; click for the OBS page.
* **LOBBIES pill** — how many lobbies are fine, degraded and down; click for Lobby health.
* **REPLAYS pill** — how many replays are ready to play.
* **AUTO / HOLD / PANIC pill** — which automatic switching is on, or that switching is held (and what it held back); click to hold, release, or undo a panic.
* **Shot timer** — how long the current shot has been on; starts again on every switch.
* **Warnings rail** — the worst warning, with "+N" for more; click for all of them, each with its **Fix**, **Undo** or a link: OBS not connected, OBS didn't switch, an auto switch (**Undo / keep**), a lobby on stream that's down, a lobby on stream missing in OBS (**Fix** rebuilds it), the stream stopped or reconnecting, not recording, dropped frames, OBS overloaded, low disk space, Desktop Audio on (**Mute it**), replays that won't save, a held-back switch (**Release hold**), a Discord problem, the Stream Deck port, the window paused.
* **? keys** — lists the Live desk's keys and mouse actions.

## Live desk: lobby tiles

* **A tile per lobby** — a small live, silent picture of every lobby sending its game, in lobby-number order; pictures pause a minute after you leave the Live desk (each is another stream from the host) and come back when you return.
* **Stable lobby number** — big number on each tile (the same number keys **1–9** and the Stream Deck use).
* **Tally** — red border and **PGM** while on stream (PGM with a camera mark on a player camera), green and **PVW** while in Preview, with the pick order when several are in Preview.
* **Phase** — IN GAME, MEETING, GAME OVER, LOBBY, MENU or OFFLINE, or **CACHED** in place of OFFLINE for a lobby kept from before a restart that the results channel hasn't confirmed yet.
* **Alive counts and tasks** — crew v impostors alive ("5c v 2i") and the task bar %, while in play.
* **Sabotage timer** — the sabotage and its seconds left, highlighted when critical (reactor, O2).
* **Excitement** — MUST SHOW, VERY HIGH, HIGH or MEDIUM badge, and a glow for Must show and Very high; a lobby's place never moves because of it.
* **Health dot** — yellow or red when the lobby is degraded or down.
* **Camera badge** — a camera mark when its player camera is ready, **ARMED** when a camera pick waits for play to resume.
* **Replay badge** — ▶ when a replay of that lobby is ready; click to play it.
* **Voice meter** — a thin bar of what OBS's voice source for that lobby puts out (dim when it isn't the one heard).
* **Mouse** — click: into Preview; Ctrl+click (or Shift+click): add to Preview; double-click: on stream now; hover buttons **PVW**, **+PVW**, **CUT** and a camera button (its player camera on stream).
* **Right-click menu** — Preview, Add to Preview, On stream now, **Listen** (keep its voice up), its host view, and a button per player to put their player card on stream.

## Live desk: Preview, Take and Program

* **Preview** (green) — click a lobby, press **1–9**, or press a lobby key on the Stream Deck; Ctrl+click adds more (two side by side, three or four in the quad, five or more in the grid). Preview is shared with the Stream Deck.
* **Take** (or **Enter**) — puts Preview on stream, in **Program** (red); the button shows the layout it will take as. A replay in Preview plays ("OBS isn't connected: replays play in OBS." without OBS, and it stays in Preview). **Esc** or **Clear** empties Preview.
* **Layouts** — **Full**, **2-Up**, **Quad**, **Grid** and **Cams** beside Take set Preview's layout; with nothing in Preview they take what's on stream (Grid: every active lobby) into that layout. Lobbies past a layout's slots are left out and named.
* **Best replay** (or **N**) — the best recent replay into Preview: its still, REPLAY and what happened; **Take** plays it, pressed again it offers the next one down (see [Replays](#replays)).
* **HEAR** (Preview's header) — Hear Preview: the lobby in Preview in your headphones, never on stream; "🎧 LJ" in green while it plays, amber while it's held back, **HEAR: OFF** when off (see [Lobby voice](#lobby-voice)).
* **Cut at once** — **Shift+1–9** or a double-click puts a lobby on stream without Preview.
* **Cut, Swoosh or Fade** — the choice beside Take is how every switch from the desk goes on (Take, cuts, camera cuts); remembered on this PC. With none picked, as set on the OBS page.
* **Back** — undoes the last switch (or a panic, or an auto switch).
* **Program picture** — OBS's own output, about twice a second, while OBS is connected; otherwise a drawing of the layout. Click a slot's name to put that lobby in Preview.
* **Rearrange** — on a 2-up or quad, pick which lobby goes in which slot; the result goes into Preview for Take.
* **Grid** — every active lobby at once (1 full, 2 side by side, up to 4×4, more five wide); empty tiles show a sponsor or the logo; a lobby that drops keeps its tile as RECONNECTING for 45 s, then the rest close up. Nothing else changes the grid by itself: a lobby joining or going to its menu waits until you change it, and a grid you picked keeps its lobbies.
* **Without OBS** — the desk still switches Red Alert's own pages (the overlay and video pages) and says OBS didn't.

## Player cams

* **Player camera** — a second picture from each host's game that follows one player up close, 960×540 at up to 30 frames a second while on stream; it darkens what that player can't see, as on their own screen. The host never sees it and can't change it.
* **Player cams card** (Live desk) — a tab per lobby in a game (the lobby in Preview, else the one on stream, by default), a chip per player with their crewmate head and real name (dimmed when dead, a knife mark on impostors), and a green underline on who the camera follows now.
* **Follow a player** — click a player: that lobby's camera goes on stream following them; **Shift+click** only changes who it follows. **◀ Prev**, **Next ▶**, **Cut to cam** and **Wide** (back to the whole map) under the chips.
* **Auto** — the camera picks by itself: an impostor closing in on a crewmate alone, else whoever is busy; a pick it made is kept at least 4 s, and your pick is kept for 4 s after they die so the kill is seen.
* **Armed in meetings** — a pick while the lobby is in a meeting, in the lobby or between games is **ARMED**: it goes on stream by itself when play resumes; pressed again, it's taken back.
* **Armed waits** — an armed camera waits out a replay, Be right back, intermission, a sponsor break and a hold; a switch you make by hand to another lobby or a break cancels it (a toast says so).
* **Several cameras** — put lobbies in Preview, press **Cams** and **Take**: one camera full screen with its whole map small in the corner, two side by side, three or four in a quad.
* **Back to the map by itself** — at a meeting or when the game ends, the stream goes back to the whole map (unless switching is held).
* **Camera sound** — while a lobby's camera is on stream, its game sound is what the followed player would hear (room sounds from where they stand, and their footsteps), and every kill is still heard; otherwise the referee's whole-map sound.
* **Straight cuts** — switching to a camera, back, and between players is a cut, not a swoosh (unless you pick another transition).
* **Other lobbies** — one line each for lobbies whose camera is ready or armed, with who it follows and ON AIR.
* **On or off** — Settings → **Player cameras** (on by default); each host sends about one more video stream while it's on.

## Automation, Hold and Panic

* **Auto grid** — fills Preview, never switches: when no lobby is mid-game (all in meetings or between games) it puts the grid into Preview ("All lobbies quiet: Grid is in Preview. Take to show it."), and when a game starts while its grid is on stream, the way back (what was on before, else the top lobby) goes into Preview ("A game started: … Take to go back."). Nothing switches until **Take** (Enter, TAKE, or the Stream Deck's Take). It fills only an empty Preview or replaces its own suggestion, never a Preview you picked; it takes its suggestion out once it no longer applies (a game started before the grid was taken, auto grid off, Hold, a break on stream); and one you cleared or replaced isn't made again until things change.
* **Between rounds** — when every lobby has been out of a game for 45 s, the desk offers intermission ("BETWEEN ROUNDS", **Intermission** or **Not now**); it never switches by itself. There's no auto intermission any more: you put intermission on and take it off (auto switch still puts it on when every lobby drops between games).
* **Auto switch** (Switch away) — when a lobby on stream goes down, switch away from it once (to the next lobby worth showing, else the grid); a card says what it did with **Undo / keep**. A lobby you put on yourself while it's down stays on.
* **Hold** (**H**, the Hold button or the AUTO pill) — nothing switches by itself until released (a camera going back to the map, auto switch, armed cameras, a sponsor break ending, a montage going back to intermission, the grid closing up after a drop), and auto grid puts nothing in Preview ("Held back: Auto grid"). What it held back shows as a toast and a warning; your own switches still go through. Kept across restarts (a timed hold until the same time).
* **Panic** (**P** twice within 3 s, or **Panic** then **Sure? Panic**) — fades to Be right back, mutes every lobby voice, clears the graphics, pauses off-screen alerts and holds switching.
* **Undo panic** — while a panic is on, **Undo panic**, **Back** or the Stream Deck's Undo puts back what was on, the voice, the alerts and your own hold. Put right by hand instead, the panic is over and later changes are yours.
* **Panic goes on at once** — nothing holds Panic's switch back on its way to the stream (OBS's own "stop streaming" is in OBS).

## Breaks and intermission

* **Starting soon** — the **TT Starting Soon** scene before the show: a countdown to the start (in minutes, "10", or a time, "19:30"), the tournament's name and logo, a line of your own (up to 120 characters), and the sponsors listed for `startingsoon` (else the split-screen break's) taking turns every 10 s. Goes on at once; at zero it says "Starting now" and stays. While it's on nothing switches by itself (auto grid, armed cameras and the graphics queue wait) and lobby voice is silent: **Take** a lobby to start the show.
* **Starting soon card** (Live desk) — two rows: **Starts** ("10" or "19:30") with **Set** and the time left ("at 19:30 · 4:59 to go", "Starting now" or "No countdown"), then **Line**, your line, with **Set** (Enter works too).
* **BRB** (Be right back) — the **TT Be Right Back** scene ("Technical difficulties", standings and storylines taking turns); goes on at once.
* **Intermission** (**I**) — the **TT Intermission** scene between rounds: a countdown to the next round, the standings, storyline notes taking turns, Chat Detectives, and the montage up next.
* **Intermission card** (Live desk) — **Next round** in minutes ("20") or a time ("19:30") with **Set**, **Play montage** for the one up next, and **Back to the game**; says "LJ IS LIVE" when a lobby starts during it (it doesn't go back by itself).
* **Sponsor break** — split screen, the lobby on the left and the sponsor on the right with a countdown; when its time is up it goes full screen on its lobby. The button shows **End break Ns** while it's on.
* **A sponsor break can't be interrupted** — a lobby you pick meanwhile (a cut, Take, a Stream Deck key, a player camera) goes into the break's picture and the break carries on ("Sponsor break on (18 s left): LJ is in the break's picture now."). Replays, montages, BRB, Intermission, Starting soon and another break are refused until **End break** ("A sponsor break is on: 18 s left. End break first."). Panic still works at once. If the break's lobby drops, auto switch puts a healthy lobby in the break (with none, the break stays on with RECONNECTING). Switching scenes in OBS itself still ends it.
* **Breaks apart from layouts** — breaks sit in their own row in the action panel, in amber, so one isn't clicked by mistake.
* **The action panel** — the Replays row and the Breaks rows stay at the top; prompts, sponsor reads and the break's card go below them (the break that's on has its card first) and scroll when they don't all fit: while there's more below, the bottom fades out with a **More below** button that scrolls down, gone once you're at the end.

## Replays

* **Clips by themselves** — every kill, winning kill, ejection, meeting, witnessed kill and game end saves a clip (8 s before to 2 s after) from OBS's buffer of each lobby's last 30 s.
* **Save replay** — any play in the **Plays** drawer can save its clip while OBS still has it; the play then shows **▶ REPLAY**, "Saving replay…" or why it failed, with retry.
* **Replay that** — saves the last 8 s of the lobby on stream and plays them as soon as they're saved, no card needed (not if you've switched or held since).
* **Best replay** (**N**, the desk's **▶ Best** button, the Stream Deck) — Red Alert picks the highest rated replay from the last 3 minutes that hasn't been played (card tier, then a play viewers missed, then points, then the newest; "Replay that" clips and montages are never picked) and puts it in Preview; **Take** plays it. Pressed again, the next one down. Nothing worth it: "No replay worth showing yet." It switches nothing, so Hold doesn't stop it.
* **Play last** — plays the newest ready replay again; the six newest also sit as buttons beside it, as many as fit whole (a long title ends in "…", the full one is its tooltip).
* **TT Replay scene** — the clip as a Media Source, cropped and zoomed (up to 2.5×) to keep the killer and victim in frame, following them as the camera moves, with a slow push-in and a REPLAY tag.
* **Replay controls** — take over the action panel while a replay plays: play/pause, ±1 s, frame step, a scrub bar with the play marked, restart, zoom, pan, **Follow**, **Back to live**; each has a key.
* **Slow motion** — 100 / 50 / 25 %, muted while slowed.
* **Second angle** — replays saved while a player camera was on have its close-up too: the **Cam / Map** button (key **P**) switches angle at the same moment; a kill the camera followed opens on the close-up.
* **Player card in replays** — the key player's card goes up by itself during a replay.
* **Source Record** — replays use the free Source Record plugin for OBS; if its buffer on a lobby isn't running, the save says so, Red Alert starts it again and the next save works.
* **Clean-up** — clips go to Videos\TT Replays; old ones are cleared (past 30 GB, older than 7 days, or with under 15 GB free), never ones a montage or the archive uses.

## Montages and the archive

* **Automatic montages** — when a game ends, about 45 s of its kills, ejections and ending; when a round ends, about 2 minutes: "every kill", then the top plays counting down. Built with ffmpeg in the background.
* **How they look** — each moment cut to a few seconds and cropped like replays, a lower third such as "Jake → Maria, Electrical", quick wipes, and an optional "Presented by" sponsor card first.
* **Montages card** — BUILDING, MONTAGE READY, FAILED, PLAYED or ON STREAM, with **Play** (in the replay scene), **Preview** (on this PC) and **Discard**; **Last game** and **This round** build one now; **Get ffmpeg** downloads it the first time (Windows).
* **Moments** — every saved clip with a still: click to pick, double-click to preview, drag to reorder, give a title, **Build montage**; clips already used are marked USED and can go in new montages.
* **Archive** — every montage that has played (title, kind, game or round, when made and played, its clips): **Replay**, **Rebuild** from its clips, **Open** the video, **Folder**, or **Delete** for good (a second click confirms; its clips stay).
* **Up next in intermission** — the queued montage shows on the intermission screen and on the desk's intermission card.

## Suggestions, talking points and plays

* **Suggestions** (the Live desk's first column) — the desk's own items at the top, then the team's suggestions from the [Watch page](#team-watch), newest first. Empty, it says "No suggestions right now. Your team sends them from the Watch page." (or, with Team watch off, how to turn it on).
* **Desk items** — lobby down, lobby back (into Preview), game interrupted (**Count**, **Void**, **Replay**, **Watch the game**), a game to replay (after **Replay** on an interrupted one), and chat's pick from Twitch (into Preview), each with its buttons.
* **A team suggestion** — TEAM, the lobby, IN PREVIEW once it's there, who sent it (and anyone who sent the same since) and how long ago; the reason ("Kill coming", "Cam on Ann") and their few words. **Into Preview** puts its lobby, or its camera following that player, in Preview ("Preview: LJ (Sam: Kill coming). Take puts it on."); **Cut** puts it on stream now; **×** dismisses it.
* **Clearing** — a suggestion clears when its lobby goes on stream (a camera suggestion: when its camera does), after 2 minutes, or when dismissed, and moves under **Earlier** with why ("went on stream", "timed out", "dismissed"). Suggestions are kept in memory only: a restart clears them.
* **Plays drawer** — **Plays** at the bottom of the third column ("N now · M earlier"): the plays going on now, highest first, with the lobby, the tier and the play in words ("Purple killed Lime in Electrical"), how it's on stream ("LIVE (quad, slot 2)"), **Save replay** / **▶ REPLAY**, **Preview** and **Card**; then plays that have calmed down, with **Watch again** (plays its replay if saved, else puts the lobby back up).
* **Tiers** — Must show (an ejection, the game ending, the winning kill), Very high (an impostor with kill ready next to a lone crewmate, reactor or O2 under 15 s, a witnessed kill), High (a meeting, one kill from an impostor win, task bar at 85 %+, three or fewer alive, someone seen venting), Medium (a kill, a sabotage, close counts, a skipped vote). They set each lobby's excitement badge, Best replay's pick and key **C**.
* **Ranking rules** — every weight and limit is in `caster-priority.json` (re-read when saved).
* **Talking points** — two storyline notes at a time about the lobbies on stream, **Next** for more, **Used** to hide one.

## Team watch

* **The Watch page** — every lobby at once for the production team (a producer, a second caster, a spotter), in a browser on their own PC or tablet on the same network as the caster's PC. They suggest what to show; they can't switch the stream.
* **It shows impostors** — the players with impostors marked IMP, like the Live desk. Give the link and code only to the production team, and keep their screens off stream and away from players.
* **Off by default** — Settings → **Team watch** switches it on ("Team watch on: your team opens http://IP:8780/ and types the code.") and off ("Team watch off: nobody can watch, and everyone was signed out."). It's kept across a restart.
* **Firewall** — the card always says: "The first time you turn it on, Windows Firewall asks whether Red Alert may use the network: allow it on private networks only." Declined, nobody can connect.
* **The card** — the Watch page's link for each network this PC is on (with **Copy**), the code with **New code**, who's watching (name, address, "watching now" or last seen, **Sign out**), and where the pictures come from.
* **Signing in** — a name (up to 24 characters, so the caster knows who's suggesting) and the watch code. A wrong code: "That code isn't right. It's in Red Alert: Settings → Team watch." Too many wrong codes: "Too many wrong codes. Wait a minute, then try again." Signed out by the caster, a new code or Team watch off: "Sign in again: the watch code changed, or Team watch was turned off."
* **The header** — the tournament and round, the **ON STREAM** and **PREVIEW** pills (what's on Program and what the caster has in Preview), who you are and **Sign out**.
* **A card per lobby** — in lobby-number order, laid out to fit the window: the picture with its red or green tally, number and name; MUST SHOW, VERY HIGH or HIGH; DOWN or Offline; a sabotage countdown; the phase, map and health; crew v impostors; the task bar; the players (dead crossed out, impostors marked IMP); its last 8 events with times; the top reasons for its rating; what the team has suggested for it; and **Show this**.
* **Show this** — a reason (Kill coming, Clutch, Meeting, Sabotage, Funny), or the player camera on a living player, and a few words if wanted (up to 60 characters), with what's "Already sent for this lobby". "Sent: LJ, Kill coming." The same suggestion from another person adds their name to it. A lobby already on stream can only be suggested for its camera ("LJ is on stream already."). One every 3 s and 8 a minute per person ("Slow down: one suggestion every few seconds.").
* **On the desk** — each one shows under [Suggestions](#suggestions-talking-points-and-plays) with **Into Preview**, **Cut** and **×**; the side menu's Live desk number counts new ones. The Stream Deck's `suggest/next` (and the Lobby key's **Newest team suggestion**) puts the newest one not yet in Preview into Preview.
* **The pictures** — with OBS connected, one small picture of each TT Lobby source from OBS, taken in one loop for everyone and only while someone has the page open: up to 5 a second per lobby and 30 a second in all (fewer each with many lobbies), up to 16 lobbies, so the hosts upload nothing more however many watch. Without OBS each card shows the host's own video, small and muted ("OBS isn't connected: video straight from the host"), which costs each host more upload per person watching. In simulation the stand-in pictures show.
* **The code** — 8 letters and numbers without 0, O, 1, I or L, shown in Settings and kept across a restart. **New code** signs everyone out, and so does switching Team watch off; off, the port isn't open at all.
* **Signing in again** — sign-ins and suggestions are kept in memory only: after Red Alert restarts, the team signs in again.
* **Limits** — 12 people signed in at once (room is made from someone gone for over 2 minutes, else "12 people are watching already. Ask the caster to sign someone out."); 5 wrong codes a minute from one address and 40 from everyone, and while that's reached even the right code waits; 30 live suggestions and 20 under Earlier.
* **What stays private** — nothing but the sign-in page answers without signing in; every other request needs the sign-in's token, and changes need it in a header (so another web page can't send one). The code is never written in `red-alert.log` (only "Team watch is on (port N)", who signed in from where, and "a wrong code from" an address), it's taken out of support bundles, and the bundle leaves out the team's suggestions (their own words could name an impostor). versions.txt says "Team watch: on, port N, X signed in."
* **Plain HTTP** — the code and the pages travel unencrypted on the network, so use it on a network you trust (the venue's or your own, not public Wi-Fi), and don't forward its port on the router.

## On-stream graphics

* **One graphics page** — a transparent 1920×1080 page (`http://localhost:8767/broadcast`) that Red Alert puts on top of every TT scene as **TT Broadcast**; each graphic lands on the right lobby in every layout, and graphics slide and fade when the layout changes.
* **Theme** — **Edit theme** opens `broadcast.json`: tournament name, colours, fonts (a Google Fonts family or the built-in ones) and logo.
* **Lobby labels** — the host's Twitch channel with Twitch's logo (hosts set it in The Button), else the lobby's name.
* **Impostor tags** — the impostors on each feed, dead ones crossed out. On by default, and live: they show on stream as it happens.
* **Sabotage countdown** — a big reactor or O2 countdown with fixing progress; lights and comms as a small icon.
* **Grid tiles** — each grid tile's label, status line and impostor tag, a border that pulses at high priority, and the logo or a sponsor in empty tiles; they stop above the stats ticker while it shows.
* **Top 3 by lobby** — top left, each playing lobby's top three for its own round, a different lobby every 8 s. It steps aside wherever it would cover a picture (the quad, a grid of two or more lobbies, the sponsor break), and while a big graphic, intermission, Be right back or a replay is up; over full screen, 2-up, a one-lobby grid and a player cam it shows as before.
* **Stats ticker** — along the bottom, leader boards taking turns five names at a time: tournament points, sharpest voters, most kills, impostor win %, task machines, survivors (top 10 of the counted games).
* **Hide room code** — a "Room code hidden" box over the room code while a lobby on stream is in its lobby or menu (off by default: hosts use streamer mode).
* **Moments** — **Off-screen alerts**, **After-game table** (arrows for who moved) and **Player cards** come up by themselves.
* **Info** — **Standings**, **Points on the line** (what each ending does to the lobby's table, near a game's end) and **Storyline note** (a lower third) are up while switched on.
* **Player cards** — a lower third with the player's real name, rank, points and today's impostor and crewmate records and kills; from **Card** buttons on cards, tiles and the Players page, or key **C** for the top play's player.
* **Dropped lobby** — its picture is covered and its graphics stay at their last state, greyed.
* **Twitch on stream** — CHAT THINKS bars during a meeting vote, prediction and poll results, Chat Detective shoutouts.

## Graphics queue and after-game prompts

* **One big graphic at a time** — an after-game table, a player card, a storyline note or the standings, each for 8 s (change it on the Live desk, 3–60 s), then the next.
* **Waits for its lobby** — a graphic about a lobby goes on that lobby: full screen, inside its tile when there's room, or in intermission; otherwise it waits and the next one goes first.
* **Graphics queue card** — what's on and how long it has left, what's next (and what's waiting for which lobby), **Skip** and **Clear**.
* **After a game** — the desk asks "LJ finished · Show the table?": **Show**, **Skip** or **Save for intermission** (saved tables play when intermission comes up); unanswered, it skips after a minute.
* **Old way** — Graphics → After a game → **Put the table up by itself** queues every table without asking.

## Off-screen alerts

* **Banners** — a banner for each kill, win, body report and emergency button in a lobby that isn't on screen.
* **Pace** — about 4 s each, one at a time, the rest queued (for up to 30 s); a second play in the same lobby joins its banner (×2).
* **Switches** — Graphics → Off-screen alerts: **Kills**, **Wins**, **Reports**, **Buttons** and **Pause all** (key **B**), with how many are waiting.

## Standings and storylines

* **Standings page** — **Lobby on stream** (the lobby full screen, or slot 1, in its own round), **Round · every lobby** or **Tournament**, and **Standings on stream**.
* **The tournament's own scoring** — the same tables as The Button and Discord, with rank, points, wins and losses, and advancing players marked.
* **Rounds per lobby** — lobbies can be in different rounds: each lobby's table and each round's totals count only that round's games, and a lobby's last round's final table stays up until its first game of the new round.
* **Storyline notes** — talking points from the day's games: impostor and crewmate records, win and loss streaks, milestones, first blood, most kills, fastest wins, rivalries, who keeps getting voted out, gaps in the standings; players on stream first.
* **Note actions** — **On stream** (a lower third, through the graphics queue), **Pin**, **Used**, **×**; the top 8 shown, **Show all** for the rest.

## Sponsors

* **sponsors.json** — name, logo, tagline, video and placements for each sponsor (**Edit sponsors** opens it; changes show within seconds).
* **Placements** — `killcam` ("Kill Cam presented by" on kill replays), `replay` (every other replay), `multiview` (a badge while 4 or more lobbies are on screen), `montage` (an opening card), `standings` (under the table), `grid` (empty tiles), `break` (the split-screen break), `startingsoon` (the Starting soon screen; without one, the `break` sponsors).
* **Sponsor break** — from the Sponsors page or the Live desk; lasts the sponsor's `breakSeconds` (30 s by default).
* **Read reminders** — give a sponsor a `readScript` and `readEveryMinutes`, `readEveryGames` or `readEveryRounds`; when one is due the Live desk shows the script to you only, with **Done** (logged) and **Snooze 5 min**.
* **Proof of delivery** — every appearance is logged (placement, time on screen, lobby, moment; "Starting soon screen" for that screen); **Export report** saves a CSV and a summary in "Sponsor reports"; each sponsor's line shows times shown, minutes and reads done.
* **Simulation is never logged** — fake games never count as appearances.

## Twitch

* **Sign-in** — paste your Twitch application's Client ID (client type Public), **Sign in**, and enter the code at twitch.tv/activate; the sign-in stays on this PC only.
* **Game predictions** — "LJ: Impostors or Crewmates?" opens when a game starts in the featured lobby (yours to pick, else the one on stream), locks after 90 s, resolves at the end, and refunds if the game is interrupted; **Lock**, **Cancel** or resolve by hand.
* **Round predictions** — **First to finish** (which lobby ends first) and **More wins** (impostor or crew wins this round).
* **Meeting votes** — in a meeting on stream, a "Who's the impostor?" poll with five or fewer alive, else a **!sus name** chat vote (one per viewer, latest counts); the stream shows CHAT THINKS, then what chat thought and who went.
* **Chat Detective** — each !sus vote for a real impostor scores; the leaderboard shows in intermission, and at each round's end the top three get a shoutout in chat and on stream.
* **Which lobby next?** — a poll in a calm moment (by hand, or by itself with Auto); chat's pick becomes a desk card with a button that puts that lobby in Preview.
* **Round MVP** — a poll of the round's top five when intermission starts.
* **Channel points** — Red Alert makes "Request a replay" and "Shoutout a player"; requests wait for **Approve** (pick the clip) or **Deny** (points back); shoutouts must name someone on the roster, and a word filter turns bad ones away.
* **Nothing gives impostors away** — options are shuffled, players listed by real name only, nothing from the caster's own view is used, and whether chat was right is said only once the game shows it.
* **Features and Auto** — each feature on or off, and whether it starts by itself; settings for stream delay (every action waits this many seconds to match what viewers see; 0 by default), prediction lock time, meeting vote mode and length, and saying at the ejection whether chat was right.
* **Log** — what Red Alert did on Twitch, with times.
* **Test mode** — a fake Affiliate channel with a pretend audience, so everything can be tried with nothing sent; **Twitch off** stops everything.

## OBS

* **Connect** — host, port (4455) and password from OBS's WebSocket server settings; the password is saved; **Disconnect**.
* **TT scenes built for you** — **TT Full**, **TT 2-up**, **TT Quad**, **TT Grid**, **TT Player Cam**, **TT Sponsor Break**, **TT Intermission**, **TT Be Right Back**, **TT Starting Soon** and **TT Replay**, with one source per lobby that stays connected (so switching is instant), the graphics page on top, a **TT Voice** source per lobby and one **TT Cue** (Hear Preview, monitor only). Your other scenes are never touched.
* **Rebuild TT scenes** — makes any missing scene or source again (the Live desk's **Fix** does the same).
* **Swoosh** — a stinger on every switch, made with ffmpeg in the tournament's colours with crewmates and the logo; add a Stinger transition called **TT Swoosh** in OBS and Red Alert sets its video; once per 1.5 s at most, never while scrubbing a replay; `swoosh.path` in `obs.json` uses your own video.
* **Setup check** — when OBS connects: scene collection and profile names (with how to rename), a leftover empty "Scene" (**Delete it**), Desktop Audio reaching the stream (**Mute Desktop Audio**), whether replays have the Source Record plugin, and which headphones OBS plays Hear Preview in (its monitoring device; warns when Disabled, notes Default); **Check again**.
* **No broadcast delay** — off in this version (the owner's call; it may come back later). Nothing is held back, whatever `obs.json` says (0.3.18 saved 3 s there): the lobbies' pictures, voice and cameras and the graphics reach the stream live. The OBS card has no delay setting, and asking Red Alert for one (`app/admin/obs` with `delay`) changes nothing and answers "The broadcast delay is off in this version of Red Alert." OBS's own stream delay isn't used either.
* **Recover missing sources** — when OBS keeps a removed source alive, Red Alert makes a fresh one under a new name ("TT Lobby LJ 2") and carries on; a source that can't be made is tried again after 30 s or on Rebuild, with no need to restart OBS.
* **Output status** — streaming and recording with their times, reconnecting, a stream that stopped, streaming without recording, dropped frames, OBS not keeping up drawing or encoding, and under 20 GB free disk (red under 5 GB).
* **Program picture** — OBS's output shown in the Live desk's Program box.

## Lobby voice

* **Each lobby's voice on stream** — the referee's Button sends the lobby's Discord voice and game sound (and their own mic only if they tick it) as its own stream; each lobby gets a **TT Voice** source in OBS.
* **Follows the picture** — the full-screen lobby, or slot 1, is heard; replays, montages and intermission are silent. It only goes to the stream (Hear Preview is the exception: your headphones only): nothing is played back to the players.
* **Listen** — keep one lobby's voice up whatever is on screen (Lobby voice page, a tile's menu, or the Stream Deck).
* **Hear Preview** — **HEAR** on Preview (and a Stream Deck toggle; on by default): the first lobby in Preview plays live in your headphones (OBS's monitoring device, Monitor Only, through the **TT Cue** source), never on stream. Silent on Mute all and Panic, with nothing in Preview, for auto grid's own suggestion, and while Desktop Audio is capturing ("Hear Preview waits while Desktop Audio is capturing: it could carry your headphones to the stream. Mute Desktop Audio."). Hosts on the old way (Share system audio) have no voice stream to hear.
* **Mute all** — **Mute all** on the page, key **U**, the AUDIO pill, or a global key (**Ctrl+Shift+M** by default, changeable) that works from any window; the desk shows it in red while on.
* **Meters** — per lobby: what OBS's voice source puts out (in dB), and the referee's Discord, Among Us and "Sent" levels as their page reports them.
* **Per-lobby volume and delay** — volume from −30 to +12 dB, and a delay up to 5000 ms to line the voice up with the picture.
* **Ducking** — **Duck under** your mic: an OBS compressor keyed to it makes lobby voice quieter while you talk; the likely mic is picked the first time.
* **Status chips** — NO VOICE YET, OFFLINE, PROBLEM, SENDING or NOT SENDING, HEARD, MIC.
* **On stream** — a switch to take lobby voice off the stream altogether.

## Lobby health and drops

* **Green, yellow, red** — each lobby with its problem in plain words: yellow for no data for 3 s, sound silent for 2 minutes while sending voice, or data 2 s later than usual; red for no data for 10 s, video lost for 3 s, or data more than 8 s late. Thresholds in `health.json` (**Thresholds**).
* **Lag** — a healthy lobby shows its data lag in ms.
* **Host needs update** — a host whose mod is older than Red Alert still shows, marked.
* **Auto switch** — **Auto switch: ON/OFF**, the same setting as the desk's Switch away.
* **Every lobby down** — the Be right back screen mid-game and intermission between games (with **BRB screen** off, always intermission); back to a lobby when one returns.
* **Nothing lost** — the referee's page keeps every message until Red Alert confirms it and resends after a drop; data links reconnect by themselves (5 s, then longer, up to a minute); old data counts for stats but never shows as live.
* **Desk page reload** — the desk page carries every lobby's data link: while it's gone (crashed, hung, or nothing from it for 6 s) and for 20 s after it's back, lobbies without data aren't counted as down, auto switch waits, no LOBBY DOWN card is made and no game is marked interrupted; one still down after that is dealt with as usual.
* **Reconnect and Forget** — **Reconnect** reloads a lobby's data link now; **Forget** takes a lobby that has gone for good off the list.
* **Interrupted games** — a referee's game that crashes, restarts or abandons a game is INTERRUPTED and kept out of the standings on stream until you pick **Count**, **Void** (posts `!void <game> interrupted`) or **Replay** (void it, and a card to replay it).
* **Game replays kept by Red Alert** — every host's game replay arrives as it's played, so a game can be watched (**Watch**) to count, void or replay it even if the host's PC dropped before posting; the viewer shows impostors.

## Players and real names

* **Roster** — `roster.csv` (name, Discord ID, in-game names, friend codes, pronunciation); **Edit roster** opens it.
* **Matching** — each player is matched by the Discord account automute links them to, then friend code, then in-game name; the Players page says how each was matched.
* **Pick by hand** — choose a roster name, "Not on the roster", or **Automatic** again; kept across restarts.
* **Names everywhere** — real names with crewmate heads on cards, on stream and in montages; the Players page shows how to say each name; each referee's nameplates show the roster names (on their screen only).

## Host view

* **Per lobby** (a tile's right-click menu) — **Lit map** (the whole map lit, every player shown), **Vision** (off, every crewmate's real sight, or every player's sight outline; or one crewmate's), **Sees impostor** (an eye by a crewmate's name while an impostor is in their sight).
* **Keys** — **M**, **V** and **E** switch them for the lobby on stream.
* **Referee's screen only** — drawn only on the referee ghost's game (the picture sent to you), never on the players'.

## Caster overlay and video pages

* **Caster overlay** — `http://localhost:8767/` for OBS (360×720): players, round standings and the latest meetings and ejections of the lobby on stream; `?full=1` adds roles, kills and task bars.
* **Video page** — `/video` (1920×1080): the lobby on stream full frame with its game sound; every lobby stays connected so picking one switches at once; `?sound=0` for picture only.
* **Multiview page** — `/multiview` (1920×1080): every lobby at once with its phase and players alive; silent unless `?sound=1`.
* **Follow or pin** — the pages follow what's on stream; Settings → Caster overlay pins them to one lobby (PINNED, **Follow the stream** to undo), with each page's link to copy.

## Simulation and TEST MODE

* **Simulation** (OBS → Test) — four fake lobbies, SIM-1 to SIM-4, play games with stand-in video and voice in OBS (or stand-in clips made with ffmpeg without OBS), so everything can be tried without real games.
* **TEST MODE bar** — across every page while it runs, with **Stop simulation**.
* **Fault buttons** — in simulation each lobby's health row has Video, Audio, Data, All, Lag, Crash and Reconnect.
* **Clean stop** — stopping removes every trace (lobbies, cards, games and standings, clips, montages, OBS sources) without switching anything in OBS; simulated games never reach the sponsor log, Discord or a real Twitch channel.

## Settings: Stream Deck and lobby numbers

* **Pairing** — the port and the Stream Deck key, **Copy key**, **Copy state link**, a download link for the plugin and **How to set it up**.
* **Link builder** — pick what a key does (a lobby, Preview, Take, a layout, a player's camera by slot or colour, cut, into Preview or only follow, replays with Best replay, breaks with Starting soon, panic, undo, hold, automation, graphics, after-game table, voice, host view) and the transition; copy the link.
* **Every link** — the ready-made links grouped, and **Copy all links**.
* **New key** — makes a new key; old links stop working.
* **Stable lobby numbers** — each lobby gets a number when it first connects (the number at the end of its name if free, else the lowest free one) and keeps it across restarts for 18 hours after it was last seen; type a new one (1–99; a lobby that had it swaps), or **Renumber online lobbies 1…N** for a new day or round.
* **Port warning** — when 8768 was taken, Settings and the warnings rail say which port Red Alert is on and that copied links need copying again.

## Stream Deck plugin

* **RedAlert.streamDeckPlugin** — attached to each Red Alert release; double-click to install; needs the Stream Deck app 6.5 or newer on Windows 10 or 11; works with the 15-key, XL, Mini, Neo, Stream Deck + and the mobile app.
* **Live keys** — one connection for all keys; every key updates the moment anything changes, and after a press shows a tick or a warning triangle.
* **Select, then submit** — new Lobby and Player cam keys put their lobby or camera in Preview, and the **Take** key puts it on stream; keys set up with an older plugin keep cutting, but a key whose settings were never opened now selects into Preview.
* **Lobby key** — a lobby by number, or next, previous, Best replay or Newest team suggestion (into Preview: it shows SUGGESTED with how many are waiting and the next one by its lobby and reason, never the team's words); into Preview (the default), cut, or add to Preview; red border for Program, green for Preview, phase, crew v impostors, tasks and a flashing sabotage timer.
* **Player cam key** — the lobby on stream, in Preview, or a number; a player slot (1–15, fixed for the game), a colour, next, previous or auto; into Preview (the default), cut, or arm. Shows the crewmate in its colour and the name, dimmed when dead, a ring while the camera follows them (red on stream) and ARMED.
* **Other keys** — **Take** (as a layout, with a transition; reads TAKE with what's in Preview: lobbies and layout, or REPLAY L3), **Wide**, **Cut to cam**, **Layout** (lit red on stream, green in Preview), **Replay** (Best replay, play last, replay that, back to live and the transport, speed), **Toggle** (mute all, Hear Preview, hold, auto grid, auto switch, standings, alerts, shown as ON/OFF, MUTED, HELD, PAUSED), **Break** (be right back, Starting soon, intermission, sponsor, end the break), **Panic** (UNDO PANIC while on), **Undo**, **Custom path**.
* **Stream Deck + dials** — **Preview dial** (turn to pick, push to Take, tap to clear), **Player dial** (turn to pick who the camera follows, push to cut, tap for wide), **Replay dial** (turn to scrub, push to play or pause, tap for speed).
* **Key art** — every key is drawn from Red Alert's live state; when something is wrong a strip says SET UP, WRONG KEY, OFFLINE, UPDATE RED ALERT or RED ALERT LOCKED over a faded picture.
* **No impostor information** — the plugin never asks for impostor flags (Red Alert leaves them out unless a link adds `&imp=1`, which the plugin strips), so a Stream Deck on camera or at a LAN gives nothing away.
* **Finds Red Alert by itself** — set to any of 8768–8771, the plugin finds Red Alert on the others.
* **No double presses** — a key ignores presses while its last one is on its way, and Red Alert ignores the same press twice within 300 ms.
* **Decks without the plugin** — every action is a plain link (`http://127.0.0.1:8768/deck/<path>?key=…`) for the Stream Deck's **Website** action (tick **GET request in background**), Bitfocus Companion or anything that opens a URL; it answers with JSON and a status, and `/deck/img/…` gives ready-made 144×144 key pictures.
* **OBS needed for Stream Deck switches** — a switch from the Stream Deck is refused while OBS isn't connected (nothing moves), unlike the desk.

See [stream-deck.md](stream-deck.md) for every action, path and recommended layouts.

## Settings: help

* **Save support bundle** — one zip on the Desktop (`RedAlert-support-<date>-<time>.zip`) with Red Alert's log and the one before, its settings, OBS's newest log and crash report, the versions, the Live desk's warnings and what the desk, OBS, lobby voice and lobby health were doing; also on the lock screen. The kept lobbies (`lobbies-cache.json`) never go in, and their VDO.Ninja passwords are taken out of every file.
* **Nothing secret in it** — the administration code, bot tokens, webhooks, OBS and VDO.Ninja passwords, stream keys, the Stream Deck key, the watch code and the Twitch sign-in are taken out, so it can be sent as it is; the team's suggestions are left out too.
* **Dry-run checklist** — opens [dry-run.md](dry-run.md): a 30-minute rehearsal with the real hosts, OBS and the Stream Deck, with a failure drill.

## Keyboard shortcuts

Keys don't act while you type in a box. The Live desk keys work on the Live desk only; the rest
work on every page. **G** to **E** and the replay keys can be changed under **Keys** in the
replay controls; the defaults are shown.

| Key | Does | Where |
|---|---|---|
| **1**–**9** | That lobby into Preview | Live desk |
| **Shift**+**1**–**9** | That lobby on stream now | Live desk |
| **Enter** | Take: Preview on stream | Live desk |
| **Esc** | Clear Preview (or close what's open) | Live desk |
| **N** | Best replay: the best recent replay into Preview (again: the next one); Take plays it | Live desk |
| **H** | Hold on or off | Live desk |
| **P** **P** | Panic (pressed again: undo panic) | Live desk |
| **G** | Every lobby (grid) into Preview | Every page |
| **I** | Intermission on or off | Every page |
| **U** | Mute every lobby voice | Every page |
| **B** | Pause off-screen alerts | Every page |
| **C** | Player card for the top play's player | Every page |
| **M** / **V** / **E** | Host view of the lobby on stream: lit map / vision / sees impostor | Every page |
| **Space** | Replay: play or pause | While a replay is up |
| **←** / **→** | Replay: back or forward 1 s | While a replay is up |
| **,** / **.** | Replay: back or forward one frame | While a replay is up |
| **R** | Replay: restart | While a replay is up |
| **=** / **−** | Replay: zoom in or out | While a replay is up |
| **W** **A** **S** **D** | Replay: pan | While a replay is up |
| **F** | Replay: follow the players again | While a replay is up |
| **L** | Replay: back to live | While a replay is up |
| **P** | Replay: the other angle (player cam or whole map) | While a replay is up |
| **Ctrl**+**Shift**+**M** | Mute all lobby voice (changeable on the Lobby voice page) | Any window in Windows |

Mouse on the Live desk: click a lobby for Preview, Ctrl+click to add it, double-click to put it on
stream now, right-click for its host view and players.

## Ports

| Port | What | Used by |
|---|---|---|
| 8768 (8769, 8770 or 8771 when taken) | Red Alert's own screen and the Stream Deck links (`/deck/…`) | The Red Alert window, the Stream Deck plugin, Companion; this PC only |
| 8767 (any free port when taken; Settings → Caster overlay shows the links) | Caster pages for OBS: overlay, `/video`, `/multiview`, the `/broadcast` graphics page, the replay tag, simulation stand-ins | OBS Browser sources; this PC only |
| 8780 (the next free one up to 8789) | Red Alert's Watch page for the production team (Team watch), only while it's on | Team members' browsers on the same network, behind the watch code |
| 4455 | OBS's WebSocket server (set in OBS) | Red Alert connects to it |
| 8765, 8766 | The mod on each host's PC (not Red Alert) | The Button |

Red Alert's screen needs a key made each time it starts, and the Stream Deck links need the Stream
Deck key, so no website can drive it. The Watch page is the only one that listens beyond this PC,
and only while Team watch is on.

## Limits and requirements

* **Windows only** — Red Alert runs on Windows on the streaming PC; its screen and pages answer only that PC, except the Watch page while Team watch is on.
* **Keep it off stream** — the desk, the player cams list, the host view and the game replay viewer all show impostors.
* **Impostors on stream** — the game pictures come from the referee ghost's screen, which shows impostors' names in red, and impostor tags are on by default. Nothing is held back: there is no broadcast delay in this version and no OBS stream delay, so impostors show on stream as it happens.
* **The administration code** — Red Alert needs the tournament's administration code, whose bot reads the private results channel where hosts' games are listed.
* **Hosts must send their game** — a lobby appears only when its host turns on **Send my game to the caster** in The Button (VDO.Ninja); each host needs about 10 Mbps upload (4–6 for the picture, 2–3 more with the player camera on; The Button's **Check my connection** measures it), and the caster downloads every lobby at once, so a wired connection is best.
* **OBS 28 or newer** — with its WebSocket server on. Panic, replays and Stream Deck switches need OBS connected; the desk alone switches only Red Alert's own pages without it.
* **Source Record plugin** — needed for replays, clips and montages (OBS's own Replay Buffer isn't used).
* **ffmpeg** — needed for montages, the swoosh and simulation's stand-in clips (**Get ffmpeg** downloads it on Windows).
* **Twitch** — polls, predictions and channel points need an Affiliate or Partner channel; the !sus vote and Chat Detective work on any channel. Twitch allows one poll and one prediction at a time.
* **Lobby voice** — each referee needs Windows 10 2004 or later for separate voice and game sound (older Windows shares system audio instead, without separate levels).
* **Keys 1–9** — reach lobbies 1 to 9 only; the Stream Deck reaches lobbies up to 99.
* **Older hosts** — a host whose mod is older than Red Alert still shows, marked **host needs update**; newer features such as the player camera's sound need an up-to-date mod.
* **Updates by hand** — Red Alert never updates by itself; use Settings → Updates.

## See also

* [README: Red Alert (the broadcaster's app)](../README.md#red-alert-the-broadcasters-app): install and setup
* [README: Caster overlay and game video](../README.md#caster-overlay-and-game-video): how hosts send their game
* [README: Caster tab (RedZone broadcast)](../README.md#caster-tab-redzone-broadcast): OBS, replays, graphics, sponsors, lobby voice and Twitch setup in detail
* [stream-deck.md](stream-deck.md): the Stream Deck plugin, every action and link
* [dry-run.md](dry-run.md): the 30-minute rehearsal before an event
* [broadcast-protocol.md](broadcast-protocol.md): what each host's game sends to Red Alert
* [test-list.md](test-list.md): what to check in each release
