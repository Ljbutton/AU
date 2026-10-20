# The mod and Discord: features

The Tournament Tracker is the Among Us mod behind The Button: a BepInEx plugin that only the lobby's
host installs, on the Windows PC version of Among Us (Steam or Epic Games), with the shared game
logic compiled into the same DLL. The host's game sees every kill, vote and role, so the mod tracks
and scores each game there, keeps the tournament's rounds and standings, runs automute, and does
everything the tournament does in Discord (reports, standings, the live lobby message, linking, the
private results channel) through The Button open on the same PC. Players use the normal game on any
platform. Hosts run it; organisers, referees and casters use what it posts and sends. This page
describes the mod as of v0.1.64 (released with Red Alert 0.3.16). For setup steps, see the
[README](../README.md).

## At a glance

* **Host only:** one mod per lobby, on the host's PC; players install nothing and link their Discord with `/link`.
* **Tracks and scores every game** with the tournament's point sheet, and keeps per-player totals and a file per game.
* **Tournament and preliminary modes** from a setup code: rounds, a cut line, tiebreaks, round summaries and standings across every lobby.
* **Posts to Discord** through webhooks (reports, leaderboards, the live lobby message) and a bot (automute, `/link`, the private results channel, where it also reads the referees' and organiser's changes The Button posts).
* **Holds each game for the host to check** in The Button's Referee tab before Discord sees it.
* **Referee ghost slot (experimental):** the host referees as an unseen ghost with the whole map, every crewmate's vision, the impostors marked, a mini chat and mini vitals.
* **Feeds the caster's Red Alert** with live events, a player camera and the game's replay, and records every game for replays in Freeplay and in a web viewer.
* **Safe mode on Among Us update day** turns off everything that sends messages to the game's servers until the mod is updated.

## What each feature needs

| Needs | Features |
| --- | --- |
| The mod only | Tracking, scoring, game files, rounds, the Submit hold, replays, the stream overlay, the referee ghost slot and its screen, the settings lock, impostor rotation, safe mode, the F10 profiler |
| The Button open on the same PC | Anything in Discord, and every switch (the mod takes no commands in the game chat) |
| A webhook | Game reports, leaderboards, the live lobby message (plain), preliminary reports with their data file and count, the public results channel, round-start messages |
| A bot (in the setup code, or The Button's Settings → **Your Discord**) | Automute, referee mode, spectator muting, who's talking, `/link`, `/unlink`, `/new`, the colour menu, auto-link, linking from Home, the voice-recording notice |
| A bot and the private results channel | Round standings with the cut line, every lobby's standings, round summaries, notes for referees, the referees' and organiser's changes from The Button (points, voids, rounds, a reset), the Live data message, **Send my game to the caster** (and the player camera), server standings |
| The bot's Message Content Intent | Reading other hosts' game files, and the changes The Button posts in the results channel |

## Game tracking and stats

* **Tracked from the host's game** — kills (killer, victim, time, first blood), meetings (who called it, the body or the emergency button), every vote (for a player, a skip or missed), ejections (and whether it was an impostor), tasks (long tasks counted apart), sabotages (who, which system), disconnects (when, alive or dead) and how the game ended.
* **Totals per player, per tournament** — games, wins and losses, impostor and crewmate records, kills, first bloods, deaths, first deaths, times ejected, survivals, correct and wrong votes, skips, missed votes, meetings called, bodies reported, tasks, sabotages, killers caught, disconnects and points. A new tournament name (or tournament ID in the code) starts a fresh leaderboard and keeps the old one.
* **Game names** — each lobby is named after its host's in-game name (or `LobbyLabel`), so games read `Game LJ-3`, numbered per host, with a unique ID that includes the start time (`LJ-3-20261003-192144`).
* **Players kept by friend code** — totals and Discord links survive name and colour changes.
* **Timeline** — each game keeps one (the start with its impostors, kills, meetings, ejections with the vote count, a crewmate finishing all their tasks, sabotages, disconnects, the end) for reports, the overlay and replays.
* **Abandoned and void games** — a game the host leaves before it ends is saved as `-abandoned` and not counted; a void game is saved as `-void`, kept for the record, and scores nothing.
* **Wrong settings flagged** — with the tournament's settings in the setup code, a game that starts on other settings is marked, the host is told, and its report says so.
* **A player leaves mid-game** — the host is told, and pointed to Void when it's before the first meeting; in a tournament with a results channel the referees get a note. The player keeps the points they'd earned and takes their team's loss, but doesn't share a win.
* **Referee left out** — the referee ghost is in no stats, points or reports.
* **Files on the host's PC** — under `BepInEx/config/TournamentTracker/`: `stats-<tournament>.json`, one JSON file per game under `games/<tournament>/`, the replays, `links.json`, the round and the switches (`state-<tournament>.json`) and the games waiting for Submit, so nothing is lost when Among Us restarts.
* **Saved safely** — every file is written whole to a side file first and only then takes the old one's place, so a crash, a power cut or a full disk part-way through leaves the old file as it was; a voided game's new file is written before its old name goes. A file that can't be read (half-written, edited wrongly) is kept as `<name>.corrupt-<time>` before anything is saved over it, and the log says so; The Button's Home shows it too. The Button also keeps a backup of the games, links, waiting list, round and totals outside the Among Us folder (see [features-the-button.md](features-the-button.md#safe-saving-and-the-backup)).
* **Self-check at start** — each game hook goes in on its own; one that doesn't match a new game version is skipped and named in the log, and the rest of the mod keeps working ("Self-check: every hook is in place." when all are).

## Scoring

The full sheet, with examples, is in the README's [Scoring](../README.md#scoring) section.

* **One point sheet for every host** — the values come from the setup code (or the config's `[Scoring]` section without one); halves are allowed, penalties are negative, 0 switches a rule off.
* **Impostors** — +1 a kill, +1 first blood, +2 for each vote on a crewmate who got ejected; +5 sabotage win, +4 vote or kill win; −3 lost to tasks, −2 lost to a vote; −2 for the first impostor voted out, −1 for a later one.
* **Crewmates, during the game** — +1 for finishing every task, +2 for each vote on an impostor who got ejected, +1 for calling the meeting that ejected one; −2 for each vote on a crewmate who got ejected; −0.25 if killed; −1 if voted out by the crew.
* **Voting and task percentages** — voting bonus up to +1 (the share of meetings, while alive, where they voted for someone), correct-voting bonus up to +2 (the share of their votes on an impostor) and task bonus up to +3 (the share of their task effort, a long task counting as two short ones). Below 25% the correct-voting and task bonuses slide down to −2 at 0%.
* **Crewmate results** — +5 task win, +3 vote win; −5 alive when the crew loses to sabotage, −2 any other loss; alive at a loss costs another −0.25 for every round the game lasted (meetings + 1), up to −4.
* **First crewmate killed** — ends the game on 90% of the average of their crew teammates, whatever they scored ("Died first: 90% of crew average").
* **Disconnects** — a player who leaves keeps what they earned, takes their team's loss (and the sabotage penalty if they left alive) and doesn't share a win; a win because the other team disconnected scores nothing unless `DisconnectWin` is set.
* **Exact points** — nothing is rounded; screens and Discord show up to two decimals.
* **Points breakdown** — each player's points carry the rule behind every one, in reports, The Button's Referee tab and round summaries, so referees can check them against the sheet.
* **Points on the line** — while a game runs, what each player would score if it ended now by an impostor win, a crew vote win or a task win, sent to the caster.

## Modes, rounds and standings

* **Settings file (no setup code)** — everything comes from `com.ljbutton.tournamenttracker.cfg`: a report and the leaderboard after each game through `StatsWebhookUrl`, the live lobby message, and automute when a bot is set.
* **Preliminary code** — each report with its data file, the lobby's leaderboard and its count go to the organiser's private reports channel; the host's own public channel (Settings → **Your Discord**) gets the game's results only; the host's chat gets a short summary. No round counting or cut line, and no live lobby message unless the host has a bot and a live lobby channel. Optional: automute with a bot, and the Red Alert channel so the lobby shows live for the caster.
* **Tournament host code** — the report goes to the results channel, then the lobby's round standings with the cut line; the live lobby message, rounds and referee tools, and with a bot and the private results channel, everything in the results channel below.
* **Administration code** — not for the mod: a game ignores it (it unlocks The Button's Organiser tab and Red Alert).
* **Several lobbies at once** — each lobby has its own host with the mod and its own bot; games are numbered per host, each lobby follows its host's voice channel, and every game ends up in one shared record in the private results channel.
* **Rounds** — **Next round** or **Set** (Home → Round) sets the round, between games only; points start from zero each round and a running total is kept; the round is saved per tournament.
* **Games per round** — from the setup code (3 by default). The host is warned when a game starts with no round set ("counts as round 0") or past the round's count, and told when the lobby's round is done.
* **Cut line** — the lobby's round standings put a line under the players who move on ("top 5 move on" by default, set in the code), ranked by round points, then wins, then kills, with each player's running total; the title says "after game 2 of 3" or "final (game 3 of 3)".
* **Every lobby's round** — **Post → Leaderboard** in a tournament with the private results channel posts this lobby's round standings, then every lobby's round together, with how many games each lobby has played (marked when done).
* **Cut-line tiebreak** — once a lobby has played all its games in the round, a tie across the cut line is broken by impostor wins, then vote %, then task %. Those it moves above the line get +0.25 on their total, shown in no public breakdown; the referees get a note in the private results channel saying who, why and by how much, or that a referee must decide when they're level on all three.
* **Round summary** — after a lobby's last game of the round, its bot posts "round N scores" in the private results channel: every player's round points (and total), wins and losses, and where the points came from added up over the round ("Kill +3 · Task win +5…", a referee's change as its own item), with the round's most kills and best votes. It doesn't say who moves on.
* **Server standings** — servers ranked by their players' total tournament points, with each server's furthest player; a player's server is the preliminary server they played the most games in. Posted with **Post → Server standings** and by itself when a new round starts. Needs a tournament code with the preliminary channels; unofficial.

## Discord: results and standings

* **Game report** (webhook) — three parts: the result with a player table (colour, name, role, kills, tasks, votes that ejected an impostor and votes that ejected a crewmate, points, result), the map, length, impostors with their kills, meetings and MVP; the **Points** breakdown for every player; and the **Timeline** (a long game keeps its start and end). A void game's title says VOID; a game started on the wrong settings says so at the top.
* **Leaderboard** (webhook) — the top 15 (`LeaderboardSize`) with points, wins and losses, kills, vote % and task %, after each counted game in settings-file mode (`PostLeaderboardAfterEachGame`) or with **Post → Leaderboard**. `LeaderboardMinGames` leaves out players with fewer games; `LeaderboardMentions` shows linked players as @mentions without pinging them.
* **Shared record** (bot, private results channel) — each host's mod posts every finished game there as a small data file (`tt-game-….json`) and reads every host's files back; round standings, every lobby's round, round summaries, server standings and the Organiser tab are built from it, and outside a tournament it gives one combined leaderboard across hosts.
* **Starting the record over** — **Reset…** (Settings → Tournament → Combined leaderboard) on a host's Button, or **Start over…** on the Organiser tab: only games posted after the newest reset message count, and deleting that message brings the old standings back. A `!resetleaderboard` typed by a person counts for nothing.
* **Preliminary reports** (webhook) — the organiser's private reports channel gets each report with its data file, the lobby's leaderboard and the lobby's **count**: of all the points scored there, the share that went to impostors and to crew, with the totals, points per player per game, wins and games. The count is posted again after every game so it stays at the bottom.
* **Public results** (webhook, preliminaries) — the host's public channel gets who won and how, each player's role, result and total, and the timeline; no points breakdown, MVP, votes, settings warnings, lobby code or data file.
* **Combined preliminary leaderboard** — not the mod: a scheduled GitHub job (every 10 minutes, or run by hand) reads every preliminary game file in the organiser's private channels with the organiser's bot and keeps one leaderboard per preliminary, plus the all-games count (every lobby together, then each lobby on its own line). A host's void or points change in the Referee tab posts the game's data file again, and the job goes by the newest copy; a `!void`, `!unvoid` or `!adjust` typed in those channels counts for nothing (only a bot's counts).
* **Live lobby message** — one message per lobby, edited in place: the lobby code, server (NA, EU, Asia or a custom one), map, round, phase (Lobby open, Game in progress, Meeting, Game over), each player's colour, name and Discord @mention (or "not linked"), the referee ghost on its own line, spectators in voice, "Referee speaking" during referee mode, and whether automute is on with how many are linked. A death shows (crossed out) only once the game has revealed it. It moves below each game report, says "The host closed the lobby" when the host leaves, and is tried again (5, 10, 20, 40 s, then every minute) when Discord fails.
* **Who posts the live message** — a webhook (`StatusWebhookUrl`, else the stats channel), or the bot, which adds the colour menu and crewmate-head emojis; `LiveStatus = false` turns it off and **Post → Lobby message** posts a fresh copy.
* **Live data message** (bot, private results channel) — "Live data · LJ", updated a few seconds after anything changes and re-sent every minute: round, phase, map, lobby code, players (deaths as known and as they really are, impostors and task counts during play), the round standings, the latest events, and the private video link while the host sends their game. The Button's Organiser tab and Red Alert read it. It shows the impostors live, so the channel must be staff only. `PublishLive = false` turns it off; Home says when it can't post and why.
* **Notes for referees** (bot, tournaments) — the private results channel gets a line for a disconnect, a game started on the wrong settings, a cut-line tiebreak, a void or unvoid, and a referee's change to a game already posted.
* **Round start** (webhook) — **Start round** on The Button's Organiser tab (round 3, starts in 10 min) has the lead lobby post "Round 3 starts in 10 minutes!" in the results channel with every lobby, its host and its players (linked players @mentioned), then "starting now" when the time comes; each lobby also posts where to go: its voice channel, lobby code and map.
* **Voice-recording notice** (bot) — when the host starts sending lobby voice to the caster, the bot posts "voice in this channel may be recorded and played on the tournament broadcast" in the lobby voice channel's chat, at most once every 30 minutes per channel.

## Referees and the organiser

* **Hold each game for Submit** — on by default (only the setup code turns it off): every finished game waits in The Button's **Referee** tab under **Waiting for you**, and nothing goes to Discord until **Submit points** (or **Submit all**) sends its report, standings and round summary. Waiting games survive a restart, and a game voided before it was sent never reaches Discord.
* **Referee changes in The Button** — **+** and **−** next to a player add or take away any amount with up to 3 significant figures, shown as "Referee change" in their breakdown ("Referee: <reason>" when made with a reason); on a game already posted, Discord is told and the standings are posted again.
* **Void and unvoid** — **Void this game** (Referee tab, with an optional reason) voids the running game or the last one; **Void** and **Unvoid** next to any game on this PC. A void game is kept but scores nothing and doesn't count toward the round, so the replacement game is the one that counts; Discord is told when it was already posted.
* **Reset all points** — **Reset points…**, then typing RESET, moves every game on this PC into a dated "reset" folder (never deleted) and starts the totals and round counts from zero; Discord isn't touched.
* **Changes come only from The Button** — in the private results channel the mods act only on what a bot posted: The Button's Referee tab (**Results channel** card) and Organiser tab (**Rounds**) post as the administration bot, and Red Alert's void of an interrupted game too. The same command typed by a person in Discord does nothing, and nobody types one: The Button builds each change from its fields.
* **Points changes** (Referee tab → **Results channel**, administration code) — posted as `!adjust LJ red -2 meta call` (lobby, colour or name, points, reason), it lands on the game that lobby was playing when it was posted, or on a numbered game (`!adjust LJ-3 …`); it shows as "Referee: meta call −2" ("Referee change" without a reason). The lobby's bot reacts with a tick, or a question mark when the player wasn't found, the next time it posts its standings.
* **Voids and unvoids** (Referee tab → **Results channel**) — posted as `!void LJ-3 reason`, or `!void LJ` for the game that lobby is playing; the lobby's bot reposts the game with the void built in and ticks the message, so deleting the void message later changes nothing. Only an unvoid brings a game back.
* **Next round's lobbies** (Organiser tab → **Rounds**, carried out by the lead lobby) — **Build its lobbies** for round 3 puts everyone who moved on from round 2 into lobbies of up to 10, snake-seeded by their round-2 points (one lobby becomes the "Final"), with the best of the rest as alternates; **Move there**, **Add there**, **Drop**, **Swap** and **Set host** change it, and each change reposts the list.
* **Starting a round** (Organiser tab → **Rounds** → **Start round**, a second click to be sure) — switches every tournament lobby's mod to round 3, after the current game when one is running (the host's chat: "Round 3 was started by the organiser: it begins after this game."); the lead lobby pings the players with their lobby and host.
* **Lead lobby** — one host's mod carries out the round changes, so nothing is posted twice: the setup code's lead tick, or **Take over** (Settings → Tournament → Lead lobby), which tells the other lobbies ("LJ carries out the round changes from The Button's Organiser tab now.").

## Linking players to Discord

* **`/link`** (bot) — a player types `/link` with their in-game name or colour; only they see the answer. A colour works from the lobby's voice channel, a name from anywhere; with several lobbies, the lobby that has that player (or that voice channel) answers, and if none can, the player is told why.
* **Linking someone else** — referees (anyone with Mute Members or Administrator in the server) and the linked host add `user:`, e.g. `/link player:Red user:@Soggy`.
* **`/unlink`** — removes the link (referees can add `user:`) and stops auto-link from linking that player again this session.
* **Colour menu** (bot) — the bot's live lobby message carries a "Select your in-game colour" menu with the colours in the lobby right now; picking one links whoever picked it. The crewmate heads are uploaded as the bot's own emojis the first time; `LinkMenu = false` turns the menu off.
* **`/new`** (bot) — the host types it in the text channel where the lobby's live message should go; the old message is removed and the choice is remembered. `/new code:QWERTY` works before the host is linked (and links them); referees can move any lobby's message with its code.
* **Auto-link by name** (bot) — in the lobby, a player whose in-game name matches exactly one person in the voice channel (server nickname, display name or username, ignoring capitals, spaces and symbols) is linked for them; `AutoLinkByName = false` turns it off.
* **Link from Home** (bot) — **Link** under a player in The Button, then their Discord username or nickname: an exact match wins, otherwise the only match; when several people match, they're listed.
* **The newest link wins** — linking a player who is already linked moves the link to the new person, and the answer says who it replaced.
* **Where links show** — links are saved in `links.json`; new links and auto-links show on The Button's Home, never in the game chat.
* **Slash commands appear by themselves** — `/link`, `/unlink` and `/new` are added to the server when the bot connects with the mod.

## Automute and voice

* **Automute** (bot) — during tasks alive players are muted and deafened and the dead talk among themselves; in meetings the living talk and the dead are muted; at the end everyone is unmuted. Each rule is a setting (`DeafenAliveDuringTasks`, `DeadCanTalkDuringTasks`, `MuteDeadDuringMeetings`).
* **Deaths kept secret** — a player killed during tasks keeps the living's voice state until the next meeting, so Discord never shows who died or when.
* **Timing** — muting for tasks never waits; voices open at a meeting straight away (`DelayMeetingStart`, 0 s) and 3 s after the game ends (`DelayGameEnd`). Muting always goes out before unmuting, so nobody who should be quiet is still talking while the bot works through the rest.
* **Several bots** — up to 3 bots share the work, each sending a few changes at once within Discord's limits; a player who left voice gets the current state as soon as they're back.
* **Automute switch** — **Automute** on Home switches it off, and it stays off after a restart until switched on, so AutoMuteUs can be used instead; linking, the live message and the results channel still work.
* **Unmute all** — **Unmute all** on Home (or **F9** in the game) gives everyone their voice back at once and turns automute off ("Everyone unmuted; automute is OFF. Press Automute on The Button's Home to turn it back on."); closing Among Us also unmutes everyone the bot muted.
* **Referee mode** — **Referee** on Home (with automute on) mutes everyone in the voice channel, players and spectators, without deafening them, except the host (once linked) and the referees (`RefereeUserIds`, or the code's), so the rules can be explained. It ends by itself when a game starts, and the live lobby message shows "Referee speaking".
* **Spectators** — **Spectators** on Home (or `MuteSpectators`) mutes anyone in the game's voice channel who isn't playing while a game runs, and unmutes them in the lobby; `SpectatorExemptUserIds` are never muted as spectators. Off by default.
* **The game's voice channel** — `VoiceChannelId` when set, else the channel the host is in (once linked), else the one most linked players are in.
* **Who's talking** (bot) — the first bot sits muted in the host's voice channel and follows them; on the host's screen (and so on stream) a talking player's meeting card is outlined in their colour, and in the lobby a speaker shows by their name. It only sees who is sending sound; it never records or decodes anything. `ShowTalking = false` turns it off.
* **Who automute leaves alone** — the referee ghost is never muted; anyone in the game's voice channel who isn't a linked player counts as a spectator, muted only with spectator muting on (during games) or in referee mode.

## Referee ghost slot (experimental)

* **Referee ghost slot** (host only; Home → Tools → **Referee ghost slot**) — for an 11-player lobby that plays like 10: at the start of every game the host becomes a ghost, never an impostor, with no tasks, and out of stats, points and automute (they can always talk). The choice is remembered.
* **Roles sent once** — while the game hands out roles the mod holds its messages, makes the referee a plain crewmate (a special role they drew goes to another plain crewmate) and sends each player's role once, as the game would; the referee's one task message carries no tasks.
* **Record kept dead** — the host marks the referee dead in the player record it keeps, again after the intro, before each meeting and every 5 seconds; no exile, kill or second role message is ever sent for them.
* **Out of sight** — as the game starts and after every meeting, the referee's ghost is moved off the edge of the map, where no player sees it, and isn't drawn on the host's screen. The host turns down any kill on the referee, and votes for the referee count as skips, so they can't be voted out.
* **What players see** — players with the mod never see the referee, and their meeting list leaves the card out; players without it see a crossed-out (dead) card in meetings when host authority is on, and the referee alive without it.
* **Host authority** (on by default; Home → Tools → **Host authority**) — the mod sets Innersloth's "+25" host-only mod flag as Among Us connects, so the host keeps every player's record and players without the mod see the referee as dead. It applies from the main menu, to the next lobby hosted, and Home says when the referee became a ghost in a lobby without it. Modded lobbies are kept out of public matchmaking; lobby codes work as usual.
* **Host-only message guard** — with host authority, the host's game turns down exile, kill, meeting, role, name, shield and shapeshift messages that come from another player's game; it's logged and shown on Home, and nobody is kicked.
* **Whole-map camera** — the referee's camera starts over the middle of the map, zoomed all the way out; **arrow keys** or **WASD** move it, **Home** brings it back, and the **mouse wheel** or **+ / −** zoom; a task or the map opens at normal size.
* **Mini chat** — in meetings, the newest chat lines show along the bottom of the referee's screen below the name plates, in each player's colour, cleared as each game starts; the chat button still opens the full chat.
* **Mini vitals** — during play, a small list down the left edge of the referee's screen shows every player in their colour, who is dead or gone, and a red IMP by each impostor.
* **Referee's task bar** — smaller, and always filled from the real task count, whatever the lobby's task bar setting.
* **Every kill heard** — the referee's game plays the kill sound for every kill (the game itself only plays it for the killer and the victim).
* **Spectator view** — drawn on the referee's game only: the whole map lit with every player shown (impostors in vents stay hidden); vision showing every living crewmate's real, wall-blocked sight (their vision setting and lights sabotage included) with the rest of the map dimmed, or one picked crewmate's, or an outline of every living player's sight in their colour; impostors' names in red, in the game and in meetings; a faint eye by a crewmate's name while an impostor is in their sight, flashing when they see a kill or a vent. Red Alert switches each part per lobby.
* **Task animations** — scans, asteroid shots and trash show on the referee's screen and the player camera even with visual tasks off for the players.
* **Roster names** — the referee's nameplates show the caster's roster names (on that screen only).
* **A clean screen** — no room name at the top, and the chat button stays.
* **Pets** — a dead player's pet stays visible to the living, so the referee unequips theirs.
* **Impostors on screen** — the referee's screen shows who the impostors are: it goes to the caster, never anywhere players can see it.

## Lobby settings and roles

* **Settings lock** (Home → Tools → **Settings lock**; on by default with a preliminary or tournament code that carries settings) — keeps the host's lobby on the tournament's settings: impostors, confirm ejects, emergency meetings and cooldown, anonymous votes, discussion and voting time, player speed, crewmate and impostor vision, kill cooldown and distance, visual tasks, common, long and short tasks, the task bar, ghosts doing tasks, and the roles.
* **Put back, said once** — anything changed in the lobby is put back within a second and the host is told once, in their own chat, until the next game; nothing is sent during the start countdown or as the game starts.
* **Casual games** — switching the lock off frees the settings until Among Us restarts; games are still checked against the tournament's settings and still count while the code is in.
* **Roles from the setup code** — each role (Engineer, Scientist, Guardian Angel, Noisemaker, Tracker, Detective, Judge, Spirit Guide, Shapeshifter, Phantom, Viper) at a number per game and a chance, every other role off, and each role's options held when the code fills them in.
* **One setting at a time** — a setting a new game version renamed is skipped (and logged); the rest are still held.
* **Impostor rotation** (setup code; ticked by default in the code maker) — each of the last counted game's impostors has a 2% chance (set in the code) of being impostor again, and everyone else shares the rest equally. A voided game doesn't count, the referee is never picked, and the host's chat says it's on when a round starts.
* **How rotation is applied** — the game still chooses the roles; the mod holds the role messages, gives the impostor roles to the picked players and sends each player's role once.

## Replays

* **Recording** — every game: each player's map position about ten times a second (and whether they're dead, in a vent or gone), the map's walls, rooms and vents read from the game, a top-down picture of the real map (taken once per map and game version), everyone's outfit, and the timeline. `RecordReplays = false` turns it off.
* **Kept on this PC** — `tt-replay-<game>.json.gz` (about 0.5 MB) next to the game's file; the newest 20 are kept; replays are never posted to Discord.
* **Sent to the caster** — while the host sends their game, the replay goes to Red Alert every 2 seconds, so it has a copy even if the host's PC drops out.
* **Replay theatre in Among Us** — anyone with the mod opens **Freeplay** on the replay's map and presses **F8** (Freeplay reminds them): the newest replays of games this PC hosted are listed and **1–9** picks one. The players appear on the real map with their colour, hat, skin, visor and name, and bodies lie where the kills were until the next meeting; on the wrong map it says which to open.
* **Web replay viewer** — [`docs/replay-viewer.html`](replay-viewer.html) opens a replay file in the browser over the real map picture: play, pause, ±5 s, 0.5–8× speed, click the timeline to jump to any kill or meeting, click a player to follow them, and switches for roles, ghosts, trails and wall outlines; **Watch a sample** shows how it works.

| Key | In the replay theatre |
| --- | --- |
| Space | Play / pause |
| ← / → | Back / forward 5 seconds |
| ↑ / ↓ | Faster / slower (0.5× to 8×) |
| 1–9, 0 | Follow that player (Tab: the next one) |
| F, then W A S D | Free camera |
| Mouse wheel | Zoom in and out, down to the whole map |
| R / G | Roles on or off (impostor names in red; on at the start) / ghosts |
| F8 | Leave the replay |

## Stream overlay

* **Stream overlay** (Home → Tools → **Stream overlay**, remembered) — an OBS Browser source at `http://localhost:8765/` (360 × 720, this PC only; `OverlayPort` changes the port): the tournament, round, lobby and game number, the phase, the players as crewmate heads (two columns past 8 players), the round standings with the cut line (the leaderboard outside a tournament), and the latest meetings and ejections.
* **Spoiler-free by default** — it only shows what the players already know: a death appears once a meeting reveals it.
* **`?full=1`** — adds roles, kills and task bars; only for a stream on a delay, since anyone watching live could see the impostors.
* **`?show=`** — `players`, `standings` or `feed` (comma separated) shows those panels only, e.g. the players on one side of the screen and the standings on the other; the standings and feed stay hidden until there's something in them.

## Caster feed to Red Alert

* **Send my game to the caster** (Home → Tools; needs a bot and the private results channel, from a tournament code or a preliminary code with the Red Alert channel) — opens a private VDO.Ninja page that shares the host's game at up to 1080p and 60 frames a second. Its random link and password are kept for the tournament and only written in the Live data message.
* **The live feed** — The Button's send page carries the mod's feed over the same private VDO.Ninja link, so only someone with the password (the caster's Red Alert) receives it; anything not acknowledged after a drop is sent again with its own time.
* **Events** — game start (map and roster), kills (killer, victim, room, map and screen position, whether it ended the game), meetings (caller, body or button), ejections (every vote, who went, impostor or not, ties), the task bar, sabotages (system, time left, fixing progress, who started it), vents (in and out, room), kill ready (estimated), danger (an impostor alone with a crewmate), a crewmate who saw a kill or a vent, and the game's end (winner, how, the impostors).
* **Every second** — a snapshot (phase, map, crew and impostors alive, the task bar, the most urgent sabotage, kill-ready impostors, danger, whether video is on, the spectator view's switches, the roster with roles, deaths and linked Discord accounts, and points on the line) and everyone's screen positions (sampled five times a second); the game's replay every 2 seconds.
* **Impostors go only to the caster** — the feed includes roles because the host's game knows them; it never goes to Discord or the game chat. Red Alert's screens show the impostors and stay off stream.
* **Versioned** — every message carries the protocol version (1); a host whose mod is older than Red Alert still shows, marked "host needs update". Field by field: [broadcast-protocol.md](broadcast-protocol.md).
* **What comes back** — Red Alert can only send `spec …` switches (the spectator view, the player camera and its sound) and its roster names; the mod answers those in its log, never in the host's chat or the stream's mini chat.
* **Player camera** — a second, off-screen camera on the host's game that follows one player up close: the caster's pick (kept about 4 s after they die, so the kill is seen), else an impostor closing in on a crewmate alone, else whoever is busy. 960 × 540 pictures, up to 30 a second while on stream and 3 while ready, drawn only while The Button's send page asks; it darkens what the followed player can't see, leaves out the host's overlays, and pauses in meetings. On by default (Red Alert switches it); a failure turns it off for the session while the main video carries on.
* **Player camera sound** — while the camera is on stream and the host is the referee ghost, the host's game sound is heard from where the followed player stands, with their own footsteps; with the whole map on stream it's the referee's again. Every kill is still heard.

## The Button's connection

* **Private connection** — the mod listens on `127.0.0.1`, port 8766 (`ControlPort`; any free port when that one is taken, `-1` turns it off), with a random key in `control.json` in its data folder; only programs on this PC with the key get in.
* **What The Button reads** — `GET /api/status` (lobby, players with their Discord link and voice state, round, automute, every switch, safe mode, the last disconnect, the Live data state, points and games for the Referee tab, notices for Home), `GET /api/feed?since=N` (the caster feed), `GET /api/pov?after=N` (the player camera's newest picture); it sends `POST /api/command` (runs a command and answers with what the mod said) and `POST /api/names` (the caster's roster names).
* **Notices on Home** — what the game says on its own (a player left, an extra game, settings put back, new links, messages turned down by the host-only guard, safe mode) shows at the top of Home for 20 minutes.
* **Discord through The Button** — every Discord request from the mod goes through The Button on the same PC (to Discord addresses only), and The Button keeps the bot connected. Without it, games are still tracked and saved (with the Submit hold, they wait in the Referee tab), but nothing reaches Discord; the host's chat says so when a game starts or The Button closes, and every voice state is sent again when it's back.
* **New setup without restarting Among Us** — a setup code or Your Discord saved in The Button restarts the tracker straight away in the lobby, or as soon as the current game ends.

| Command | What it does | In The Button |
| --- | --- | --- |
| `r3` or `round 3` | Sets the round (between games); `round` alone says which | Home → Round → **Next round**, **Set** |
| `void [reason]`, `unvoid` | Voids the running game or the last one; brings it back | Referee → **Void this game**, **Unvoid** |
| `voidgame <game> [reason]`, `unvoidgame <game>` | Voids or brings back any game on this PC | Referee → **Void** / **Unvoid** by a game |
| `verify <game>` or `verify all` (also `submit`) | Sends held games to Discord | Referee → **Submit points**, **Submit all** |
| `adjust <game> <player> <points> [reason]` | A referee change to one player in one game | Referee → **+** / **−** |
| `resetpoints` | Puts every game away and starts from zero | Referee → **Reset points…** |
| `automute on` / `off` | Automute on or off (kept after a restart) | Home → **Automute** |
| `unmuteall` | Everyone unmuted, automute off | Home → **Unmute all** |
| `ref on` / `off` | Referee mode | Home → **Referee** |
| `spectators on` / `off` | Spectator muting | Home → **Spectators** |
| `link <player> <Discord name>`, `unlink <player>` | Links or unlinks a player by their Discord name | Home → **Link** under a player |
| `lock on` / `off` | Settings lock | Home → Tools → **Settings lock** |
| `refslot on` / `off` | Referee ghost slot (between games) | Home → Tools → **Referee ghost slot** |
| `hostauth on` / `off` | Host authority, from the next lobby hosted | Home → Tools → **Host authority** |
| `overlay on` / `off` | Stream overlay | Home → Tools → **Stream overlay** |
| `feed on` / `off` | Send my game to the caster | Home → Tools → **Send my game to the caster** |
| `leaderboard` (or `lb`) | Posts the leaderboard, or the round standings in a tournament | Home → Tools → Post → **Leaderboard** |
| `servers` | Posts the server standings | Post → **Server standings** |
| `refresh` | Posts the live lobby message again | Post → **Lobby message** |
| `resetleaderboard` | Starts the shared record over | Settings → Tournament → **Reset…** |
| `lead` | This lobby carries out the round changes from the Organiser tab | Settings → Tournament → **Take over** |
| `safemode off` / `on` | Run normally on this game version / back to automatic | Home → safe mode card → **Run normally anyway**, **Back to automatic** |
| `setup reload`, `setup clear` | Uses the newly saved setup code, or none | Settings (saving or removing the code) |
| `voicenotice` | Posts the voice-recording notice | Sent by the send page when lobby voice starts |
| `spec …` | Spectator view and player camera switches | Sent by Red Alert |

## Safety and diagnostics

* **Safe mode** — when Among Us is newer than the game version this DLL was built for (`BUILT-AGAINST.txt`), or a hook of a feature that sends messages to the game's servers didn't go in, those features stay off: host authority (the +25 flag), the referee ghost slot (the referee plays as an ordinary player), the impostor rotation, the settings lock putting settings back (it only watches), and the host-only message guard. Tracking, stats, the overlays, the caster feed, Discord and automute keep working.
* **Safe mode card** — Home shows a red "Safe mode" card with the reason and the versions; **Run normally anyway** (a second click to confirm) runs everything on this game version, remembered for that version only, so the next Among Us update turns safe mode back on; **Back to automatic** undoes it. A change during a game applies from the next game.
* **Disconnect watch** — every time Among Us is disconnected (not when leaving on purpose), the mod logs the game's reason in plain English ("server anti-cheat", "kicked", "timed out"…), whether a game was running, the lobby code, whether it was hosting with host authority or in safe mode, and what the mod itself sent in the 10 seconds before. The last 20 are kept in `disconnects.json` for the support bundle, and the latest shows on Home.
* **F10 frame profiler** — **F10** in the game (or `FrameProfiler = true` under `[Debug]`) writes any frame where the mod takes more than 8 ms to `BepInEx/LogOutput.log`, at most once a second, broken down by part with the garbage collections since the last line, plus a summary every 10 seconds; nothing shows in the game.
* **Referee record log** — `RefereeRecordSync = true` under `[Debug]` logs each time the referee's player record is sent and whether it says dead.
* **Never breaks a match** — every hook catches its own errors; a part that fails is logged once and switched off, and the game carries on.

| Key (in Among Us) | When | What it does |
| --- | --- | --- |
| F8 | In Freeplay | Opens or leaves the replay theatre |
| F9 | Hosting, automute set up | Unmutes everyone and turns automute off |
| F10 | Any time | Frame profiler on or off |
| Arrow keys or WASD, Home | Referee ghost | Moves the camera; back to the middle |
| Mouse wheel, + / − | Referee ghost | Zooms out to the whole map and back |

## What the mod never does

* **Nothing is typed in the game** — every host action is a button in The Button and players link in Discord; the mod takes no commands from the chat. Its notes (round progress, a player who left, a game waiting for Submit) show in the host's own chat only; `PublicChat = true` sends the announcements to the whole lobby.
* **No spoilers in public** — the live lobby message, the default overlay and automute only show a death once the game has revealed it; impostors only reach private places (the caster feed, the Live data message, the overlay's `?full=1`, the referee's own screen).
* **No second role or task message, and no exile or kill for the referee** — the referee is record-only; a second role message or an exile outside a meeting is what gets a host kicked.
* **Other players' games are left alone** — everything drawn for the referee is on the host's game only (players with the mod just don't see the referee); with host authority on, modded lobbies leave public matchmaking.
* **Nobody is kicked** — messages the host-only guard turns down are only logged and shown on Home.
* **No Discord without The Button**, and only Discord addresses through it.
* **No voice recorded** — who's talking only sees who is sending sound.
* **No replays in Discord** — replays stay on the PC, and go to the caster's Red Alert only while the host sends their game.
* **No settings changed without a code** — the settings lock only acts with a preliminary or tournament code, and never during the start countdown.

## Limits and requirements

* **Windows, Steam or Epic, host only** — the Xbox app / Game Pass version can't be modded; players join from any platform. The mod loader is BepInEx 6 build 735 (the 64-bit build for Among Us since its 29 September 2026 update).
* **One host per lobby** — every lobby needs its own host with the mod; a player who has the mod but isn't hosting only gets the replay theatre and the hidden referee.
* **Built for one Among Us version** — a newer game turns on safe mode until the mod is updated; a game update that renames a method stops only the hook for that method.
* **Discord needs The Button open** on the host's PC; `/link` also needs the host in the Among Us lobby.
* **Bots** — automute, linking and the results channel need a bot in the setup code or Settings → Your Discord (up to 3); Discord rate-limits each bot, so muting a full lobby with one bot takes a few seconds. The bot needs the channel permissions in the README's [least-privilege table](../README.md#what-the-bots-need-least-privilege), and Message Content Intent for the results channel.
* **Automute** follows linked players only (anyone else in the game's voice channel counts as a spectator), and Discord only lets a server mute change while someone is in a voice channel.
* **Who's talking** needs the host linked and in voice, and the bot allowed to Connect there.
* **Round changes and points changes** — come only from The Button with the administration code (a command typed in the channel does nothing); rounds and lobbies work in tournament mode only and are carried out by the lead lobby; a points change's tick comes when that lobby next posts its standings. Any bot or webhook that can post in the private results channel could still post one, so keep it staff only. Every host needs this version's mod: an older one still acts on typed commands.
* **Submit hold** can only be switched off in the setup code; there's no config-file setting for it.
* **Settings lock** needs a preliminary or tournament code with settings; switched off, it's back on after Among Us restarts.
* **Impostor rotation** comes only from a setup code.
* **Referee ghost slot** is experimental: host only, set the lobby to 11 players, needs host authority for players without the mod to see the referee as dead, and is off in safe mode.
* **Host authority** applies from the main menu (the next lobby hosted), online games only, and keeps the lobby out of public matchmaking.
* **Replays** stay on this PC (the newest 20); the theatre needs Freeplay on the replay's map and lists only games this PC hosted; the characters glide rather than walk; replays recorded before outfits were saved show no cosmetics.
* **Local ports** — 8765 (stream overlay) and 8766 (The Button's connection) on this PC only.
* **Caster feed** needs a setup code with a bot and the private results channel, and about 10 Mbps upload (4–6 for the picture, 2–3 more for the player camera; The Button's **Check my connection** measures it).
* **Impostors are shown** on the referee's screen, in the caster feed, in the Live data message and on the overlay's `?full=1`: none of these may be anywhere players can see them during a game.
* **The combined preliminary leaderboard** runs on GitHub, not in the mod: it needs the `DISCORD_BOT_TOKEN` secret and the `PRELIM_CHANNEL_IDS` variable, and only runs on the repository's default branch.

## See also

* README: [Install (host only)](../README.md#install-host-only), [Setup codes](../README.md#3-setup-codes), [Modes](../README.md#modes), [During the tournament](../README.md#during-the-tournament), [The combined preliminary leaderboard and count](../README.md#4-the-combined-preliminary-leaderboard-and-count)
* README: [Scoring](../README.md#scoring), [What gets recorded](../README.md#what-gets-recorded), [Configuration](../README.md#configuration)
* README: [Discord setup](../README.md#discord-setup-without-a-setup-code), [Automute (bot)](../README.md#automute-bot), [Linking players to Discord](../README.md#linking-players-to-discord), [Several lobbies at once](../README.md#several-lobbies-at-once), [Nothing is typed in the game](../README.md#nothing-is-typed-in-the-game)
* README: [Referee ghost slot (experimental)](../README.md#referee-ghost-slot-experimental), [Replays](../README.md#replays), [Stream overlay](../README.md#stream-overlay), [Organiser tab](../README.md#organiser-tab-administration-code)
* README: [Red Alert (the broadcaster's app)](../README.md#red-alert-the-broadcasters-app), [Caster tab](../README.md#caster-tab-redzone-broadcast), [Building](../README.md#building)
* [broadcast-protocol.md](broadcast-protocol.md): the feed between a host's game and Red Alert, field by field
* [replay-viewer.html](replay-viewer.html): the web replay viewer
* [setup-codes.html](setup-codes.html): the setup-code maker
* [points-lab.html](points-lab.html): try point values and see their effect
* [dry-run.md](dry-run.md): the 30-minute rehearsal with every host and the caster
