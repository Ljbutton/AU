# Among Us Tournament Tracker

A **host-only** Among Us mod for running tournaments. Only the host installs it; players
use the normal game. It:

* **Tracks stats** for every game: kills, deaths, ejections, meetings, body reports, votes
  (correct, wrong, skipped, missed), tasks, sabotages, disconnects, wins and losses by team.
* **Keeps a tournament leaderboard** with configurable points, saved between sessions.
* **Posts to Discord** through a webhook: a live status message for the lobby, a report after
  each game (player table, impostors, points breakdown, MVP, full timeline) and the updated
  leaderboard. You can also turn on a live play-by-play.
* **Automutes Discord voice**: alive players are muted (and deafened) during tasks, everyone
  alive can talk in meetings, and dead players talk among themselves during tasks, with a
  short delay at each change. It can also mute spectators and link players by name
  automatically. It works like AutoMuteUs, but the host's game drives it directly, so no
  capture app is needed.

**Platforms:** the host plays the Windows PC version from Steam or Epic Games (the Xbox app /
Game Pass version can't be modded). Everyone else can join from any platform: PC, phone,
Switch, Xbox or PlayStation.

The host's client runs the game, so it sees every kill, vote and role. That is why only the
host needs the mod.

---

## Install (host only)

**One click:** download **`Install-TheButton.bat`** from the
[latest release](https://github.com/Ljbutton/AU/releases/latest) and double-click it. If
Windows shows "Windows protected your PC", click **More info → Run anyway**.

It installs **The Button**, the Tournament Tracker app (for your Windows user, no admin
needed), with Start menu and desktop shortcuts, and opens it. Everything is done from the app; nothing
is typed in the game chat, so there's nothing for Among Us's anti-cheat to trip on.

1. **Home:** the app finds Among Us (Steam or Epic, any drive; or paste the folder) and
   installs the mod with the mod loader it needs (BepInEx 6 build 735, 32-bit). It offers
   updates when a new release is out, and repairs an install that has the wrong loader.
2. **Settings:** paste the setup code the organiser gave you. The page then shows the
   tournament and the locked lobby settings (point values stay with the organiser), and
   switches between dark (the default) and light.
3. **Start Among Us** and host a lobby. The first start takes a few minutes while BepInEx
   sets itself up (a black console window appears). The app connects on its own.

While you play, the app's pages run the lobby:

* **Lobby:** lobby code, map and round; start the next round; void or unvoid a game;
  link players to Discord; post standings; the settings lock, referee ghost slot, stream
  overlay and lead-lobby switches.
* **Automute:** in the style of the AutoMuteUs capture window: the game's state, every
  player as a crewmate in their colour with their Discord name and whether they're muted,
  and buttons for automute on/off, referee mode, spectator muting and "unmute everyone".
* **Replays:** every recorded game (and any downloaded from Discord); watch one in the app.

The app talks to the mod over a private connection on your computer only (port 8766, with a
random key the mod writes in its data folder).

<details><summary>Installing by hand instead</summary>

1. Download `TournamentTracker-Full.zip` from the latest release and extract everything
   into the Among Us folder (the one with `Among Us.exe`). It contains BepInEx and the mod.
2. Or, if you already have **BepInEx 6 bleeding-edge build 735 (IL2CPP, the win-x86 build: Among Us is 32-bit)**, only
   `TournamentTracker-*.zip` is needed: it puts `BepInEx/plugins/TournamentTracker.dll` in
   place.

To uninstall, delete `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini` and
`.doorstop_version` from the Among Us folder.
</details>

`BUILT-AGAINST.txt` in the ZIP names the Among Us version the DLL was compiled for. After
Among Us updates, rebuild against the new version (see *Building*).

## Running a tournament (organiser)

The organiser sets up Discord once and hands each host a **setup code**. Hosts never touch
Discord developer settings or the config file.

### 1. Discord channels

* **Preliminary channel(s):** preliminary reports land here. Make a webhook for it.
* **Results channel:** tournament game reports and standings for players. Make a webhook.
* **Private results channel:** staff only. The hosts' mods post each game's data here,
  referees type point adjustments here, and `!resetleaderboard` here starts standings over.

### 2. Bots (tournament hosts only)

Create one bot per tournament lobby that runs at the same time, at
<https://discord.com/developers/applications> (New Application → Bot → Reset Token). For
each: turn on **Message Content Intent** (Bot → Privileged Gateway Intents), and invite it
with this link (put in the application ID):
`https://discord.com/oauth2/authorize?client_id=APP_ID&scope=bot&permissions=12700736`
(view channels, send messages, embed links, attach files, read history, add reactions,
mute and deafen members). Preliminary hosts don't need a bot.

### 3. Setup codes

Open `docs/setup-codes.html` (the setup code generator) in a browser and fill it in:

* **Preliminary code:** the tournament name, the preliminary server's name and the
  preliminary channel's webhook. Make one per preliminary server (the server name is what
  server standings use). Safe to hand out.
* **Tournament host code:** the tournament name, the results channel webhook, your server
  ID, one bot's token, the private results channel and the preliminary channels. It
  contains the bot token, so send it privately.

Both carry the point values, so every host scores the same, and the game settings (below).
Tournament host codes also carry the games per round and, for one host only (you), the
**lead lobby** tick that makes that host's mod answer your results-channel commands. Codes
are only encoded, not encrypted: anyone holding one can read what's in it.

* **Settings lock** (on by default in the generator): while a preliminary or tournament code
  is in use, the host's lobby is kept on the tournament's settings (impostors, cooldowns,
  vision, kill distance, tasks, special roles off…). Anything changed in the lobby is put
  back and the host is told. A game that still starts on the wrong settings says so in its
  report (and to the referees). For a casual game the host types `!lock off` (until they
  restart Among Us); without a code nothing is ever touched.
* **Fair impostor rotation** (off unless ticked): within a round nobody is impostor a
  second time until everyone has been once, still drawn at random. The mod swaps the
  roles the game handed out, and announces it when a round starts.

### 4. The combined preliminary leaderboard

Preliminary hosts have no bot, so a scheduled GitHub job builds one leaderboard per
preliminary, across all its lobbies, every 10 minutes. In the GitHub repository: Settings →
Secrets and variables → Actions → add the secret `DISCORD_BOT_TOKEN` (one of your bots,
invited to the server with the preliminary channels) and the variable `PRELIM_CHANNEL_IDS`
(comma separated). Optionally set `PRELIM_LEADERBOARD_CHANNEL` to post every leaderboard
in one channel. Scheduled runs only happen on the repository's default branch, so merge
this branch into it first. Actions → *Preliminary leaderboards* → Run workflow updates it
right away.

### Modes

| | Preliminary code | Tournament host code |
| --- | --- | --- |
| After each game | Report plus the game's data in your preliminary channel; a summary in the host's chat | Report in the results channel; the lobby's round standings |
| Leaderboard | The scheduled job's combined board per preliminary | Per lobby per round, with the cut line, plus a running total |
| Automute | Only if the host adds their own bot (most should use AutoMuteUs) | Yes, with your bot |
| Live status, referee tools | No | Yes |

### During the tournament

Hosts do all of this from the Tournament Tracker app's Lobby page (the `!` commands below
are what its buttons run); referees and the organiser use the private results channel.

* **Rounds:** the host types `!r1`, `!r2`, `!r3`… in the lobby before each round's first game,
  for as many rounds as you need. Points restart each round, and a running total across all
  rounds is kept alongside. After every game the lobby's standings for the round are
  posted with a line under the top players who move on (5 by default). `!leaderboard` also
  shows every lobby in the round together.
* **Referee adjustments:** in the private results channel, type
  `!adjust LJ red -2 meta call` (the lobby name, the player's colour or name, the points,
  the reason). You can type it during the game; it lands on the game that lobby was playing
  at that moment. For a specific game use its name: `!adjust LJ-3 red -2 …`. The lobby's
  bot reacts ✅ when it's applied, or ❓ if the player couldn't be found. It shows in the
  points breakdown as "Referee: meta call −2".
* **Restarted games:** if a game has to stop and restart, the host types `!void [reason]`
  during it (or straight after it, for the last game), or a referee types
  `!void LJ-3 reason` in the results channel (`!void LJ` means the game that lobby is
  playing). A void game is kept for the record and its report says VOID, but it scores
  nothing and doesn't count toward the round, so the replacement game is the one that counts.
  Only `!unvoid` (in-game, or `!unvoid LJ-3` in the channel) brings it back: once the
  lobby's bot has seen a void it reacts ✅ and reposts the game with it built in, so deleting
  the `!void` message changes nothing. Preliminary channels work the same way (the scheduled
  job applies them), so the organiser's bot needs Add Reactions and Attach Files there.
* **Round progress and ties:** standings say which game of the round they're after
  ("after game 2 of 3"), the all-lobbies view shows how far each lobby has got, and a host
  starting a 4th game in a round is warned. When a lobby has played all its games, a tie
  across the cut line is broken by impostor wins, then vote %, then task % (over the
  round). The players it moves above the line get +0.25 on their total; it isn't shown in
  any public breakdown, but the referees get a note in the results channel saying who,
  why and by how much. A tie that's level on all three is left to a referee (use `!adjust`).
* **Disconnects:** when a player leaves mid-game the host is told, and pointed to `!void`
  if it's before the first meeting; the referees get a note. A player who leaves keeps
  the points they'd earned and takes their team's loss (and the sabotage penalty if they
  left alive), but doesn't share a win.
* **Next round's lobbies** (from the private results channel, any device; answered by the
  lead lobby's mod):
  * `!lobbies 3` puts everyone who moved on from round 2 into lobbies of 10, snake-seeded
    by their round-2 points (1st, 4th, 5th, 8th… in lobby A when there are 2 lobbies), and
    lists the best of the rest as alternates. 10 or fewer make the Final.
  * `!move Soggy B`, `!swap Soggy Fred`, `!drop Fred` (shows the next alternates),
    `!add Millie B` (an alternate or anyone), `!host B LJ` adjust it; each change reposts the list.
  * `!start 3 in 10` switches every lobby's mod to round 3 (after the current game if one
    is running) and pings each lobby's linked players in the tournament channel with their
    lobby and host, then again when it starts. `!start 3` on its own says it's starting now.
  * `!lead` typed in-game by a host makes their mod the one that answers from then on.
  This needs the bot's Message Content Intent (already needed for the results channel).
* **Server standings:** `!servers` (and each new round) posts servers ranked by their
  players' total points, with each server's furthest player. A player's server is the one
  they played the most preliminaries in. Unofficial.

### Stream overlay

`!overlay on` (host) starts an overlay for OBS on the host's computer: add a **Browser
source** with `http://localhost:8765/` (size 360×900). It shows the lobby, round and game
number, the players, the round standings with the cut line, and the latest meetings and
ejections. It only shows what the players in the game already know (a death appears once a
meeting reveals it). `http://localhost:8765/?full=1` adds roles, kills and task bars: only
for a stream on a delay, since anyone watching live could see who the impostors are. Add
`&show=players` (or `standings`, `feed`, comma separated) to show some panels only.
`!overlay off` stops it; the choice is remembered.

### Replays

Every game is recorded: each player's position about ten times a second (and whether
they're dead or in a vent), the map's walls, rooms and vents read from the game, and the
game's events. The file (`tt-replay-LJ-3-….json.gz`, about 0.5 MB) is saved with the game
and posted with it: in the results channel for tournaments, in the organiser's channel for
preliminaries. Open it in `docs/replay-viewer.html`: play, pause, scrub, 0.5–8× speed, jump
to any kill or meeting, follow a player, show bodies, trails, vents, roles and ghosts.
`RecordReplays = false` in the config turns recording off.

**Watching in Among Us, on the real map.** Anyone with the mod can open **Freeplay** on the
replay's map and press **F8**: it lists the newest replays (saved with your own games, or
downloaded from Discord into your Downloads folder); press 1–9 to pick one. The players
appear with their own colour, hat, skin, visor and name, bodies lie where the kills were
until the next meeting, and the camera is yours:

| Key | |
| --- | --- |
| Space | Play / pause |
| ← / → | Back / forward 5 seconds |
| ↑ / ↓ | Faster / slower (0.5× to 8×) |
| 1–9, 0 | Follow that player (Tab: the next one) |
| F, then W A S D | Free camera |
| Mouse wheel | Zoom in and out, down to the whole map |
| R / G | Show roles (impostor names in red) / show ghosts |
| F8 | Leave the replay |

If you're on the wrong map it says which Freeplay map to open. The characters glide rather
than play their walking animation. Replays recorded before this version have no outfits,
so everyone appears without cosmetics.

### Referee ghost slot (experimental)

For an 11-player lobby that plays like 10 while the host referees: the host types
`!refslot on` in the lobby and sets the lobby to 11 players. Only the host can be the ghost
referee. At the start of every game the host becomes a ghost: never an impostor, no tasks, not in stats, points or automute (they can
always talk). The host can zoom out with the **mouse wheel** or **+ / −** to see the
whole map (for refereeing and streaming). `!refslot off` turns it off; the choice is remembered.

## Discord setup (without a setup code)

### Stats (webhook, no bot needed)

Channel settings → Integrations → Webhooks → New Webhook → Copy URL. Paste the URL into
`StatsWebhookUrl`.

The same channel gets a **live status message**: the lobby code, map, phase, and each
player's colour, name and Discord link. It updates as people join, link and play, and moves
below each game report so it stays at the bottom. Deaths only appear once the game has
revealed them (at a meeting or the end), so it never gives away a kill. Give it its own
channel with `StatusWebhookUrl`, or turn it off with `LiveStatus = false`. `!refresh` posts a
fresh copy.

If you want a live feed of kills and meetings, make a second webhook in a
**staff-only** channel (the feed reveals the impostors) and use it for `LiveFeedWebhookUrl`.

### Automute (bot)

1. Create an application at <https://discord.com/developers/applications>. Under **Bot**,
   reset the token and copy it. The bot needs no privileged intents.
2. Invite the bot with the View Channels, Mute Members and Deafen Members permissions:
   `https://discord.com/oauth2/authorize?client_id=YOUR_APP_ID&scope=bot&permissions=12583936`
3. Turn on Developer Mode in Discord, right-click your server → **Copy Server ID**.
4. In the config, set `[AutoMute] Enabled = true`, `BotTokens = <token>` and `GuildId = <server id>`.

Discord rate-limits member edits for each bot. With 10–15 linked players, one bot can take a
few seconds to mute everyone. To make that faster, invite two or three bots and list every
token, comma separated. The work is spread across all of them.

**Keep the config file private.** Anyone with the bot token can control that bot.

The bot isn't hosted anywhere: the mod on the host's PC acts as the bot while Among Us is
open, so there's no server to run. The bot shows as offline when no host has the game open.

#### Timing

Voice switches a moment after the game does, so nobody gets cut off mid-word:

| Change | Default | Setting |
| --- | --- | --- |
| Game starts → alive players muted | 3 s (time to react to the role reveal) | `DelayGameStart` |
| Meeting ends → alive players muted again | 3 s | `DelayMeetingEnd` |
| Game ends → everyone unmuted | 3 s | `DelayGameEnd` |
| Meeting called → alive players unmuted | 0 s, so nobody misses the start of the discussion | `DelayMeetingStart` |

A death applies at once, and `!unmuteall` / **F9** never wait.

#### Referee mode

Type `!ref on` in the lobby to explain the rules: everyone in the voice channel (players and
spectators) is muted, but can still hear, except you and anyone in `RefereeUserIds`
(co-referees, casters). `!ref off` gives everyone their voice back. It only ever turns on
by hand, and it ends by itself when a game starts, so a forgotten toggle can't silence a
meeting. You need to be linked to Discord yourself to be the one talking. The live status
message shows when the referee is speaking.

#### Spectators

With `MuteSpectators = true` (or `!spectators on`), anyone in the game's voice channel who
isn't playing is muted while a game is running and unmuted in the lobby. The game's voice
channel is the one the host is in (once the host is linked), otherwise the one most linked
players are in; set `VoiceChannelId` to pin it. Put casters
and referees in `SpectatorExemptUserIds` so they're never muted. Off by default.

### Linking players to Discord

Automute only touches players linked to a Discord account. Links are keyed by friend code,
so a link survives name and colour changes, and they're saved in
`BepInEx/config/TournamentTracker/links.json`.

* Players type **`!link their_discord_username`** (or their numeric user ID, or
  `<@id>`) in the lobby chat.
* The host can link anyone: **`!link red coolbean`** or **`!link CoolBean 1234…`**.
* **Automatically:** in the lobby, a player whose in-game name matches exactly one person in
  the voice channel (their server nickname, display name or username, ignoring capitals,
  spaces and symbols) is linked for them, and the lobby chat says so. A wrong match is fixed
  with `!unlink`, and that player won't be auto-linked again that session. Turn it off with
  `AutoLinkByName = false`.
* Or fill in `links.json` before the event (see `links.example.json`). A player's key is their
  friend code in lower case, e.g. `coolbean#1234`.

Unlinked players are never muted, and spectators only when `MuteSpectators` is on.

## Several lobbies at once

Each lobby needs its own host running the mod. Everything else is automatic:

* **Labels.** Each lobby is named after its host's in-game name, so games are numbered per
  host (`Game LJ-3`, `Game MAL-3`) with a unique ID that includes the start time
  (`LJ-3-20261003-192144`). All hosts keep the same `TournamentName`. Set `LobbyLabel` to use
  something else, e.g. "Bracket 1".
* **Voice.** Each host's mod follows the voice channel that host is sitting in, so give each
  lobby its own voice channel.
* **Bots.** Give each host their own bot: Discord rate-limits each bot separately, so two
  lobbies sharing one bot mute more slowly.

### Combined leaderboard

To have one leaderboard across every lobby, make a **private** text channel (e.g.
`#tournament-data`) that each host's bot can see, send messages and attach files in, and put
its channel ID in every host's `ResultsChannelId`. After each game, the host's mod posts
the game's record there as a small file, then reads every host's files back and posts the
combined leaderboard. Each host's own `!stats` and `!leaderboard` show the combined numbers.

In the Developer Portal, turn on **Message Content Intent** (Bot → Privileged Gateway
Intents) for every host's bot. Without it a bot can't read the files other bots posted, and
the log warns about it.

**To reset it**, type `!resetleaderboard` in that channel yourself, from any device, or use
`!resetleaderboard` in the game as a host. Only games posted after the newest reset
message count. The older game files stay in the channel, so deleting the reset message
brings the old standings back.

## Chat commands (off by default)

The app does all of this. Typing commands in the lobby chat is off unless you set
`ChatCommands = true` in the config (and `PublicChat = true` to send the mod's announcements
to everyone's chat). The commands, for reference; the app runs the same ones:

| Command | Who | What |
| --- | --- | --- |
| `!link <discord>` | anyone* | Link your Discord account |
| `!unlink` | anyone | Remove your link |
| `!stats [player]` | anyone | Show tournament totals in chat |
| `!help` | anyone | List commands |
| `!link <player> <discord>` | host | Link someone else (player = colour or name) |
| `!unlink <player>` | host | Unlink someone |
| `!links` | host | Who in the lobby is linked and who isn't |
| `!automute on\|off` | host | Pause or resume automute |
| `!unmuteall` | host | Emergency: unmute everyone and turn automute off (**F9** does the same) |
| `!ref on\|off` | host | Referee mode: mute everyone in voice except the referees, to explain the rules |
| `!spectators on\|off` | host | Mute people in voice who aren't playing, during games |
| `!refresh` | host | Post a fresh live status message at the bottom of the channel |
| `!leaderboard` | host | Post the leaderboard to Discord now (combined across lobbies if set up) |
| `!resetleaderboard` | host | Start the combined leaderboard over for every lobby |
| `!r1`, `!r2`… or `!round 3` | host | Start a tournament round (points restart; running total kept) |
| `!servers` | host | Post the server standings |
| `!lock on\|off` | host | Keep the lobby on the tournament's settings / free it for a casual game |
| `!lead` | host | This lobby's mod answers the results-channel commands |
| `!overlay on\|off` | host | Stream overlay for OBS at http://localhost:8765/ |
| `!void [reason]` / `!unvoid` | host | Throw out the current or last game (a restart) / bring it back |
| `!refslot on\|off` | host | Referee ghost slot: the host plays as a ghost referee (experimental) |
| `!setup` | host | Apply the setup code on the clipboard (`!setup clear` to stop using one) |
| `!resetstats confirm` | host | Archive the stats file and start a new leaderboard |

\* unless `AllowSelfLink = false`.

When the game closes, the mod unmutes everyone it muted before it exits.

## Configuration

`BepInEx/config/com.ljbutton.tournamenttracker.cfg`:

| Section | Setting | Default | |
| --- | --- | --- | --- |
| General | `TournamentName` | Among Us Tournament | A new name starts a new leaderboard. The old one is kept. |
| General | `LobbyLabel` | | Names this lobby (`Game LJ-3`); empty = the host's in-game name |
| General | `CommandPrefix` | `!` | |
| General | `AllowSelfLink` | true | |
| General | `RecordReplays` | true | Record a replay of every game |
| General | `OverlayPort` | 8765 | Stream overlay port (this computer only) |
| General | `GamesPerRound` | 3 | Tournament rounds; a setup code overrides it |
| General | `ChatCommands` | false | Accept commands typed in the lobby chat (the app is the normal way) |
| General | `PublicChat` | false | Send announcements to everyone's chat |
| General | `ControlPort` | 8766 | The app's private connection; -1 turns it off |
| Discord | `StatsWebhookUrl` | | Game reports and leaderboard |
| Discord | `LiveFeedWebhookUrl` | | Optional play-by-play |
| Discord | `PostLeaderboardAfterEachGame` | true | |
| Discord | `LeaderboardSize` | 15 | |
| Discord | `LeaderboardMinGames` | 1 | Games a player needs before they appear on the leaderboard |
| Discord | `LeaderboardMentions` | false | Show linked players as @mentions on the leaderboard (nobody is pinged) |
| Discord | `LiveStatus` | true | The live lobby status message |
| Discord | `ResultsChannelId` | | Shared channel for the combined leaderboard across hosts |
| Discord | `StatusWebhookUrl` | | Its own channel; empty = the stats channel |
| AutoMute | `Enabled` | false | |
| AutoMute | `BotTokens` | | Comma separated |
| AutoMute | `GuildId` | | |
| AutoMute | `DeafenAliveDuringTasks` | true | Alive players can't hear the dead |
| AutoMute | `DeadCanTalkDuringTasks` | true | |
| AutoMute | `MuteDeadDuringMeetings` | true | |
| AutoMute | `DelayGameStart` / `DelayMeetingEnd` / `DelayGameEnd` | 3 / 3 / 3 | Seconds; see *Timing* |
| AutoMute | `DelayMeetingStart` | 0 | Seconds |
| AutoMute | `MuteSpectators` | false | See *Spectators* |
| AutoMute | `VoiceChannelId` | | Empty = where most linked players are |
| AutoMute | `SpectatorExemptUserIds` | | Comma separated |
| AutoMute | `AutoLinkByName` | true | |
| AutoMute | `RefereeUserIds` | | Who else can talk in referee mode, comma separated |
| Scoring | *(see below)* | | Every point value on the tournament sheet |

## Scoring

The defaults follow the **Point Sheet Template** tab of the tournament spreadsheet
(`Tournament_Points.xlsx`). Each value is a line under `[Scoring]` in the config. Halves are
allowed, penalties are negative, and 0 switches a rule off.

| Impostor | Points | Config key |
| --- | --- | --- |
| Kill | +1 each | `Kill` |
| First blood (on top of the kill) | +1 | `FirstBlood` |
| Voted for a crewmate who got ejected | +2 each | `VotedCrewmateOut` |
| Win by sabotage | +5 | `ImpostorSabotageWin` |
| Win by vote | +4 | `ImpostorVoteWin` |
| Win by kills | +4 | `ImpostorKillWin` |
| Lose to tasks | −3 | `ImpostorTaskLoss` |
| Lose to vote | −2 | `ImpostorVoteLoss` |
| First impostor voted out | −2 | `VotedOutFirst` |
| Later impostor voted out | −1 | `VotedOutLast` |

| Crewmate | Points | Config key |
| --- | --- | --- |
| Finished every task | 0 (off; the task bonus covers it) | `CompletedTasks` |
| Voted for an impostor who got ejected | +2 each | `CorrectVoteOut` |
| Called the meeting where an impostor got ejected | +1 | `CaughtKiller` |
| Killed | 0 (off) | `GotKilled` |
| First player killed: ends on 90% of their crew teammates' average | see below | `DiedFirstShareOfCrewAverage` |
| Voted for a crewmate who got ejected | −2 each | `IncorrectVoteOut` |
| Reads bonus: +1 per vote on an impostor who stayed in, times the share of such votes that were right | up to +4 | `ReadVotePoints`, `ReadVoteBonus` |
| Task bonus: % of their task effort finished, a long task counting double | up to +3 | `TaskPercentBonus`, `LongTaskWeight` |
| Win by tasks | +5 | `CrewTaskWin` |
| Win by vote | +3 | `CrewVoteWin` |
| Alive when the team loses to sabotage | −5 | `CrewSabotageLossAlive` |
| Any other loss | −1 | `CrewOtherLoss` |

A crewmate's vote scores one of two ways. A vote that **ejected** someone is a vote out:
+2 on an impostor, −2 on a crewmate. Any other vote, for someone who stayed in, is a
**read**. Reads show who spotted the impostors early, so each read on an impostor earns +1,
up to +4, and that is then multiplied by the share of their reads that were right. Right at
four meetings earns +4; right at one earns +1; voting at everyone in 8 meetings and being
right in 4 earns only +2, so calling lots of meetings to vote doesn't pay. Skips and missed
votes don't count either way.

The **task bonus** scales with the share of task effort finished (up to +3), where a long
task counts as two short ones (`LongTaskWeight`). With 2 common, 3 long and 5 short tasks,
finishing everything but the long ones is 7 of 13 (54%, +1.5), not 7 of 10.

The **first crewmate killed** ends the game on 90% of the average of their crew teammates
(everyone else on the crew who didn't disconnect), whatever they scored themselves. Dying
first once is often bad luck and costs little; dying first every game keeps a player out of
the top half. The report shows it as "Died first: 90% of crew average".

Bonuses and the died-first score round to the nearest half point (`BonusRounding`). Win and loss points go to the whole team, dead or alive, but not to anyone
who disconnected. A game won because the other team disconnected isn't on the sheet, so it
scores nothing unless you set `DisconnectWin`.

Every game report on Discord includes a **Points** section listing each rule a player
scored on, so referees can check it against the sheet. In the player table, ✓ counts votes
that ejected an impostor and ✗ counts votes that ejected a crewmate.

## What gets recorded

Files go in `BepInEx/config/TournamentTracker/`:

* `stats-<tournament>.json` holds running totals per player: games, wins, losses, impostor and
  crew records, kills, deaths, first deaths, times ejected, survivals, correct and wrong votes,
  skips, missed votes, emergency meetings, bodies reported, tasks done, sabotages,
  disconnects and points.
* `games/<tournament>/game-0001-….json` holds the full record of each game: roles, every kill
  with its time, every meeting with the caller, the body and each vote, ejections, sabotages
  and the result.

A game the host leaves before it ends is saved as `-abandoned` and is not counted; a void
game is saved as `-void`.

## Building

```bash
dotnet test tests/TournamentTracker.Tests          # core logic, no game needed
dotnet build src/TournamentTracker.Plugin -c Release -p:GameLibsVersion=2025.x.y
```

The plugin builds against `AmongUs.GameLibs.Steam` from the BepInEx NuGet feed, with
`BepInEx.IL2CPP.MSBuild` generating the interop assemblies at build time. By default it
uses the newest game version (currently 2026.8.18); pass `GameLibsVersion` to match your
game. BepInEx is pinned to be.735, because newer builds use an Il2CppInterop version the
interop generator can't run. On GitHub, run the
**Build** workflow by hand to choose the version, or push a `v*` tag to publish a release ZIP.

Layout:

* `src/TournamentTracker.Core` has everything that doesn't touch the game: the stat tracker,
  scoring, leaderboard, Discord REST and webhooks, the automute planner and dispatcher, and
  chat commands. It is unit-tested.
* `src/TournamentTracker.Plugin` is the BepInEx plugin: Harmony hooks on the game, and a
  per-frame driver that reads the game phase for automute. It compiles the core in, so the
  build is a single DLL.

If a game update renames a method, only the hook for that method stops working. The rest of
the mod still loads, and the BepInEx log names the hook that failed.
