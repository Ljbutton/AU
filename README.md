# The Button: Among Us tournaments

**The Button** is a Windows app plus a **host-only** Among Us mod (the Tournament Tracker)
for running tournaments. Only the host installs it; players use the normal game, and link
their Discord account with **/link** in the Discord server. It:

* **Tracks stats** for every game: kills, deaths, ejections, meetings, body reports, votes
  (correct, wrong, skipped, missed), tasks, sabotages, disconnects, wins and losses by team.
* **Keeps a tournament leaderboard** with configurable points, saved between sessions.
* **Posts to Discord** through a webhook: a live status message for the lobby, a report after
  each game (player table, impostors, points breakdown, MVP, full timeline) and the updated
  leaderboard.
* **Automutes Discord voice**: alive players are muted (and deafened) during tasks, everyone
  alive can talk in meetings, and dead players talk among themselves during tasks, with a
  short delay at each change. It can also mute spectators. Players link themselves with
  `/link` in Discord, or are linked automatically when their name matches. It works like AutoMuteUs, but the host's game drives it directly, so no
  capture app is needed.

**Platforms:** the host plays the Windows PC version from Steam or Epic Games (the Xbox app /
Game Pass version can't be modded). Everyone else can join from any platform: PC, phone,
Switch, Xbox or PlayStation.

The host's client runs the game, so it sees every kill, vote and role. That is why only the
host needs the mod.

---

## Install (host only)

The current release is **v0.1.20**, a beta.

**One click:** download **`Install-TheButton.bat`** from the
[latest release](https://github.com/Ljbutton/AU/releases/latest) and double-click it. If
Windows shows "Windows protected your PC", click **More info → Run anyway**.

It installs **The Button** (for your Windows user, no admin needed), with Start menu and desktop shortcuts, and opens it. Everything is done from the app; nothing
is typed in the game chat, so there's nothing for Among Us's anti-cheat to trip on.

1. **Settings:** the app finds Among Us (Steam or Epic, any drive; or pick the folder) and
   installs the mod with the mod loader it needs (BepInEx 6 build 735, 64-bit: Among Us is
   64-bit since its 29 September 2026 patch; a 32-bit loader left from before shows as
   "Mod needs repair" and The Button swaps it by itself once Among Us is closed). Paste the
   setup code the organiser gave you; the page then shows the tournament and the locked
   lobby settings (point values stay with the organiser).
2. **Start Among Us** and host a lobby. The first start takes a few minutes while BepInEx
   sets itself up (a black console window appears). The app connects on its own.

The top bar only shows the logo, plus a warning when something needs you: "No setup code
installed", "New version available", "Mod not installed", "Mod needs repair", "Among Us not
found", "Setup code damaged", "Discord bot refused" (the bot's token was reset: the host
needs a new code) or "Referee commands not heard" (the bot's Message Content Intent is off).
Click one to go where it's fixed.

**The Button updates itself.** When a new version is out it downloads it and uses it from
the next start (Settings → The Button: "Restart to update"). The mod in Among Us updates
itself the same way while the game is closed (Settings → Mod). Turn **Update automatically**
off in either place to update only when you choose.

While you play, the app's pages run the lobby:

* **Home:** a line at the top with the game's latest warnings (a player left and whether to
  void, an extra game, settings put back…; "N more" opens the rest, ✕ dismisses); a
  checklist of what the lobby needs (mod up to date, setup code, round set, Discord voice,
  everyone linked, the lobby message, sending video), red or amber when something's
  missing; and the whole lobby on one page, up to 15 players: code, map, round, each player
  as their crewmate with their Discord link and whether automute has them muted or deafened;
  the automute buttons (automute on/off, referee mode, spectator muting, unmute everyone);
  start the next round; void or unvoid a game; the settings lock, referee ghost slot and
  stream overlay switches; post the leaderboard or server standings, or repost the lobby
  message.
* **Points:** the point totals, for the host and the referees (players never see them in
  the game or through the bot). In a tournament: this lobby's round standings with the cut
  line and each player's running total, every lobby's round together (with a results
  channel), and from round 2 the running total across rounds. With a preliminary code: the
  lobby's leaderboard, to help decide who moves on.
* **Games:** every game this PC hosted, newest first: winner and how, top scorer, map,
  length, voided or not, and **Watch** for its replay. Replays downloaded from other lobbies
  are listed below. It reads the saved games, so it works with Among Us closed.
* **Settings:** the game folder and the mod; the setup code as one line (details, change or
  remove it); in a tournament with a results channel, the **lead lobby** and the combined
  leaderboard reset; The Button's updates; files under **Advanced**.

When a lobby plays its last game of the round, its bot posts the **round's scores** in the
private results channel (staff only, so lobbies without a bot don't post it): every player's
points for the round, in order, and under each one where the points came from, added up over
the round's games ("Kill +3 · Task win +5 · Lost -2…"; a referee's adjustment shows as its
own item). It doesn't say who moves on.

The app talks to the mod over a private connection on your computer only (port 8766, with a
random key the mod writes in its data folder).

<details><summary>Installing by hand instead</summary>

1. Download `TournamentTracker-Full-x64.zip` (64-bit Among Us, the game since its 29 September
   2026 update) or `TournamentTracker-Full-x86.zip` (32-bit Among Us, before it) from the latest
   release and extract everything into the Among Us folder (the one with `Among Us.exe`). It
   contains BepInEx and the mod. (`TournamentTracker-Full.zip` is the same as the x64 one, kept
   for older copies of The Button.)
2. Or, if you already have **BepInEx 6 bleeding-edge build 735 (IL2CPP, the win-x64 build for
   64-bit Among Us, win-x86 for 32-bit)**, only
   `TournamentTracker-Mod-*.zip` is needed: it puts `BepInEx/plugins/TournamentTracker.dll` in
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
`https://discord.com/oauth2/authorize?client_id=APP_ID&scope=bot+applications.commands&permissions=12700736`
(view channels, send messages, embed links, attach files, read history, add reactions,
mute and deafen members, and the `/link` and `/unlink` commands, which appear in the server
the first time a host opens Among Us with the code). Preliminary hosts don't need a bot,
but can have one for automute (see below).

### 3. Setup codes

Once your administration code is in, Settings → Administration → **Make setup codes** opens the setup code generator in your browser (it's also `docs/setup-codes.html` in this repository). Fill it in:

* **Preliminary code:** the tournament name, the preliminary server's name and the
  preliminary channel's webhook. Make one per preliminary server (the server name is what
  server standings use). Safe to hand out, unless you add automute:
  * **Automute (optional):** fill in your server ID and a bot's token (and, if you like, a
    webhook for a live lobby channel, which gets the message with the colour menu). The
    host's game then mutes Discord itself, and players link with `/link` or the menu. Leave
    it empty and the preliminary works exactly as before (players can use AutoMuteUs). A
    code with a bot token in it must be sent privately.
* **Tournament host code:** the tournament name, the results channel webhook, and
  optionally your server ID, up to 3 bot tokens, the private results channel and the
  preliminary channels. The bot is optional: without one, games are still tracked, scored
  and reported, and the host has the whole app, but there's no automute, no `/link`, `/new`
  or colour menu, no combined standings and no referee commands. 2 or 3 bots mute a full
  lobby faster (Discord limits each bot's speed; the work is shared). A code with a bot
  token in it must be sent privately.

* **Administration code** (for you, referees and casters): the tournament name, the private
  results channel and one bot token. Paste it into The Button's Settings → **Setup
  code** box like any code: The Button recognises it, and the **Administration** card and
  **Organiser** tab appear (nobody without the code sees either) (see *Organiser tab* below). It has a bot token in it, so send it privately.

Both carry the point values, so every host scores the same, and the game settings (below).
Tournament host codes also carry the games per round and, for one host only (you), the
**lead lobby** tick that makes that host's mod answer your results-channel commands. Codes
are only encoded, not encrypted: anyone holding one can read what's in it. The Button keeps
its own copy of the host's code, so reinstalling the mod or Among Us doesn't lose it: it's
put back by itself.

* **Settings lock** (on by default in the generator): while a preliminary or tournament code
  is in use, the host's lobby is kept on the tournament's settings (impostors, cooldowns,
  vision, kill distance, tasks, special roles off…). Anything changed in the lobby is put
  back and the host is told. A game that still starts on the wrong settings says so in its
  report (and to the referees). For a casual game the host turns **Settings lock** off on
  The Button's Home page (until they restart Among Us); without a code nothing is ever touched.
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
| Automute | Optional: add a bot to the code (otherwise players can use AutoMuteUs) | Yes, with your bot |
| Live status, referee tools | No | Yes |

### During the tournament

Hosts do all of this from The Button's Home page; referees and the organiser use the
private results channel.

* **Rounds:** the host presses **Next round** (or sets a number) on Home before each round's
  first game, for as many rounds as you need. Points restart each round, and a running total across all
  rounds is kept alongside. After every game the lobby's standings for the round are
  posted with a line under the top players who move on (5 by default). **Post** under
  Standings also shows every lobby in the round together.
* **Referee adjustments:** in the private results channel, type
  `!adjust LJ red -2 meta call` (the lobby name, the player's colour or name, the points,
  the reason). You can type it during the game; it lands on the game that lobby was playing
  at that moment. For a specific game use its name: `!adjust LJ-3 red -2 …`. The lobby's
  bot reacts ✅ when it's applied, or ❓ if the player couldn't be found. It shows in the
  points breakdown as "Referee: meta call −2".
* **Restarted games:** if a game has to stop and restart, the host presses **Void** on Home
  during it (or straight after it, for the last game), or a referee types
  `!void LJ-3 reason` in the results channel (`!void LJ` means the game that lobby is
  playing). A void game is kept for the record and its report says VOID, but it scores
  nothing and doesn't count toward the round, so the replacement game is the one that counts.
  Only **Unvoid** (on Home, or `!unvoid LJ-3` in the channel) brings it back: once the
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
* **Disconnects:** when a player leaves mid-game the host is told, and pointed to **Void**
  if it's before the first meeting; the referees get a note. A player who leaves keeps
  the points they'd earned and takes their team's loss (and the sabotage penalty if they
  left alive), but doesn't share a win.
* **Automute on or off:** the host can switch automute off on Home (the Automute button);
  it stays off, even after restarting, until they switch it on again. While it's off the bot
  never touches anyone's voice, so AutoMuteUs can be used instead; linking, the live
  message and the results channel still work.
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
    Each lobby's own game then posts where to go: "**Round 3 · LJ's lobby:** join voice
    #lobby-1 · lobby code `QWERTY` · Polus" (its voice channel is the one its host is in).
  * **Take over** (Lead lobby, on Home) makes that host's mod the one that answers from then on.
  This needs the bot's Message Content Intent (already needed for the results channel).
* **Server standings:** **Servers** on Home (and each new round) posts servers ranked by their
  players' total points, with each server's furthest player. A player's server is the one
  they played the most preliminaries in. Unofficial.

### Stream overlay

**Stream overlay** (Home → Tools) starts an overlay for OBS on the host's computer: add a
**Browser source** with `http://localhost:8765/` (size 380×720). With more than 8 players
the list goes into two columns, so a full 15-player lobby still fits. It shows the lobby,
round and game number, the players (as the same crewmate heads The Button uses), the round standings with the cut line, and the latest meetings and
ejections. It only shows what the players in the game already know (a death appears once a
meeting reveals it). `http://localhost:8765/?full=1` adds roles, kills and task bars: only
for a stream on a delay, since anyone watching live could see who the impostors are. Add
`&show=players` (or `standings`, `feed`, comma separated) to show some panels only, for
example the players on one side of the screen and the standings on the other. Standings
and the feed stay hidden until there's something in them. Turning the switch off stops it;
the choice is remembered.

### Organiser tab (administration code)

With an administration code pasted into Settings → Setup code, The Button gets an
**Organiser** tab, and it doesn't need Among Us on that computer. Every tournament host's
game with a bot keeps one "Live data" message in the private results channel, updated a few
seconds after anything changes; the Organiser tab reads them all:

* **Every lobby live:** phase, map, lobby code, who's alive or dead, and the round's games.
  Tick **show roles** to see impostors, kills and task bars too. A lobby that hasn't sent
  anything for a couple of minutes is shown as gone quiet.
* **Standings:** each lobby's round standings with the cut line, and all lobbies combined.
  Until the new round's first game, the last round's final standings stay up.
* **Awards:** candidates for this round or the whole tournament, the top three in each of
  top score, most kills, best impostor, sharpest voter, impostor hunter, task machine and
  survivor. **Copy for Discord** copies them; you pick the winners.
* **Jump to action** (or **N**): casts the lobby worth watching now (a meeting first, then
  whatever just happened, else the next lobby in turn). A lobby whose host has gone silent
  for a couple of minutes gets a warning at the top.
* **Referee** (on **Home**, for whoever has the administration code): adjust points, void or
  unvoid a game, or type any `!` command; it's posted in the results channel as the
  administration bot and the lead lobby carries it out. On a PC without Among Us, Home
  waits for the game as usual, with the referee tools below.
* **Caster overlay:** an OBS Browser source at `http://localhost:8767/` that shows whichever
  lobby you pick with **Cast** (or the number keys 1-9 while the tab is open), switching
  live: players, round standings and the latest meetings and ejections for that game.
  `http://localhost:8767/?full=1` adds roles, kills and task bars (for a stream on a delay
  only).
* **Game video, RedZone style:** each host turns on **Send my game to the caster** (Home →
  Tools). The Button opens a private VDO.Ninja page in their browser; they choose **Entire
  screen**, tick **Share system audio** (a window share has no sound on Windows) and leave
  the tab open. The sound is everything the host's PC plays, Discord included. The link is random, kept for the tournament, and only
  written in the private results channel. In your OBS:
  * `http://localhost:8767/video` (1920×1080): the lobby you're casting, full frame, with its
    game sound. Every lobby's picture stays connected in the background, so pressing Cast
    switches instantly; the sound follows a moment later and only the lobby on air is ever
    heard. Put the caster overlay on top of it. `?sound=0` for picture only.
  * `http://localhost:8767/multiview` (1920×1080): every lobby at once, with its name,
    phase and players alive, the one on air outlined, and a red mark on a lobby with a
    meeting or an ejection happening. Silent (the video page carries the sound);
    `?sound=1` plays the lobby on air if you use the multiview on its own.
  * The Organiser tab marks lobbies with something happening too (with **show roles**,
    kills and sabotages as well), so you know where to jump.
  * Each host needs about 3-6 Mbps upload spare, and you download every lobby at once, so
    use a wired connection.
  * Put a delay on your public stream (OBS → Settings → Advanced → Stream Delay, 2-3
    minutes) so players in other lobbies can't watch it for information.

The administration bot needs View Channel, Read Message History and Send Messages in the
private results channel, and its **Message Content Intent** turned on (Discord Developer
Portal → Bot). Hosts can turn the live data off with `PublishLive = false`.

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

For an 11-player lobby that plays like 10 while the host referees: the host turns on
**Referee ghost slot** (Home → Tools) and sets the lobby to 11 players. Only the host can be the ghost
referee. At the start of every game the host becomes a ghost: never an impostor, no tasks, not in stats, points or automute (they can
always talk). The host can zoom out with the **mouse wheel** or **+ / −** to see the
whole map (for refereeing and streaming). The same switch turns it off; the choice is remembered.

## Discord setup (without a setup code)

### Stats (webhook, no bot needed)

Channel settings → Integrations → Webhooks → New Webhook → Copy URL. Paste the URL into
`StatsWebhookUrl`.

The same channel gets a **live status message**: the lobby code, map, phase, and each
player's colour, name and Discord link. It updates as people join, link and play, and moves
below each game report so it stays at the bottom. Deaths only appear once the game has
revealed them (at a meeting or the end), so it never gives away a kill. Give it its own
channel with `StatusWebhookUrl`, or turn it off with `LiveStatus = false`. **Repost lobby
message** on The Button's Home posts a fresh copy.

### Automute (bot)

1. Create an application at <https://discord.com/developers/applications>. Under **Bot**,
   reset the token and copy it. The bot needs no privileged intents.
2. Invite the bot with the View Channels, Mute Members and Deafen Members permissions and
   its slash commands:
   `https://discord.com/oauth2/authorize?client_id=YOUR_APP_ID&scope=bot+applications.commands&permissions=12583936`
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

A death applies at once, and **Unmute all** (Home) / **F9** never wait.

#### Referee mode

Press **Referee** under Automute on Home to explain the rules: everyone in the voice channel (players and
spectators) is muted, but can still hear, except you and anyone in `RefereeUserIds`
(co-referees, casters). Pressing it again gives everyone their voice back. It only ever turns on
by hand, and it ends by itself when a game starts, so a forgotten toggle can't silence a
meeting. You need to be linked to Discord yourself to be the one talking. The live status
message shows when the referee is speaking.

#### Spectators

With **Spectators** on (Automute on Home, or `MuteSpectators = true`), anyone in the game's voice channel who
isn't playing is muted while a game is running and unmuted in the lobby. The game's voice
channel is the one the host is in (once the host is linked), otherwise the one most linked
players are in; set `VoiceChannelId` to pin it. Put casters
and referees in `SpectatorExemptUserIds` so they're never muted. Off by default.

### Linking players to Discord

Automute only touches players linked to a Discord account. Linking happens in Discord, not
in the app or the game chat. Links are keyed by friend code, so a link survives name and
colour changes, and they're saved in `BepInEx/config/TournamentTracker/links.json`.

* **`/link`**: in any channel of the server, a player types `/link` and their in-game name
  or colour (`/link Red`, `/link Soggy Dingus`). Only they see the answer. A colour works
  when they're in the lobby's voice channel; a name works from anywhere. With several
  lobbies running, the lobby that has that player (or that voice channel) answers.
* **`/new`** (hosts): picks where your lobby's live message goes. Type `/new` in the text
  channel you want (for example each lobby's own channel when several games run at once):
  your game moves the message there, removes the old one, and the bot posts it with the
  colour menu. The choice is remembered. It answers the host whose Discord is linked to the
  lobby's host player; if you haven't linked yourself yet, use `/new code:QWERTY` with your
  lobby code (that links you too). Referees can move any lobby's message with its code.
  Without `/new`, the message goes to the status channel as before. The bot needs View
  Channel and Send Messages in the channel you pick; if it can't post, The Button says so.
* **The newest link wins:** if a colour is already linked to someone, linking it again
  (menu or `/link`) moves it to the new person and unlinks the old one. The lobby chat says
  so ("Linked Pink (Millie) to @millie_b (replacing @someone)"), so a mistake is easy to spot.
* **`/unlink`** removes their link (and stops auto-link from linking them again that session).
* **The colour menu:** when the host has a bot (every tournament host code does), the bot
  posts the live status message itself, with each player's crewmate head and a
  "Select your in-game colour" menu under it, like AutoMuteUs. The menu lists only the
  colours in the lobby right now. Picking one links whoever picked it (only they see the
  answer). The crewmate
  heads are uploaded as the bot's own emojis the first time it connects. The bot needs Send
  Messages in the status channel; without it the status stays a webhook message without the
  menu. `LinkMenu = false` turns the menu off.
* If no open lobby can match the name (the player isn't in the Among Us lobby yet, or used a
  colour from outside the lobby's voice channel), they're told why instead of getting no
  answer. `/link` only works while a host has Among Us open, since the host's game is the bot.
* Every new link is announced in the lobby chat ("Linked Red (Soggy) to @soggy"), so a
  wrong one gets noticed. `AnnounceLinks = false` turns that off.
* **Referees** (anyone who can mute members in the server) link someone else by adding
  `user`: `/link player:Red user:@Soggy`, `/unlink user:@Soggy`. The host can too, once linked.
* **Automatically:** in the lobby, a player whose in-game name matches exactly one person in
  the voice channel (their server nickname, display name or username, ignoring capitals,
  spaces and symbols) is linked for them, and the lobby chat says so. A wrong match is fixed
  with `/unlink`. Turn it off
  with `AutoLinkByName = false`.

Home shows who is linked. Unlinked players are never muted, and
spectators only when spectator muting is on.

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
combined leaderboard. Each host's **Post** (Standings) shows the combined numbers.

In the Developer Portal, turn on **Message Content Intent** (Bot → Privileged Gateway
Intents) for every host's bot. Without it a bot can't read the files other bots posted, and
the log warns about it.

**To reset it**, type `!resetleaderboard` in that channel yourself, from any device, or use
**Reset…** under Standings on The Button's Home. Only games posted after the newest reset
message count. The older game files stay in the channel, so deleting the reset message
brings the old standings back.

## Nothing is typed in the game

Everything the host does is a button in The Button; players link in Discord. The mod never
reads the lobby chat. It only writes to it: the host sees its notes (round progress, a
player who left), and links are announced to everyone (`AnnounceLinks`). `PublicChat = true`
sends the other notes to everyone too.

When the game closes, the mod unmutes everyone it muted before it exits.

## Configuration

`BepInEx/config/com.ljbutton.tournamenttracker.cfg`:

| Section | Setting | Default | |
| --- | --- | --- | --- |
| General | `TournamentName` | Among Us Tournament | A new name starts a new leaderboard. The old one is kept. |
| General | `LobbyLabel` | | Names this lobby (`Game LJ-3`); empty = the host's in-game name |
| General | `RecordReplays` | true | Record a replay of every game |
| General | `OverlayPort` | 8765 | Stream overlay port (this computer only) |
| General | `GamesPerRound` | 3 | Tournament rounds; a setup code overrides it |
| General | `PublicChat` | false | Send announcements to everyone's chat |
| General | `AnnounceLinks` | true | Say in the lobby chat when a player is linked to Discord |
| General | `ControlPort` | 8766 | The app's private connection; -1 turns it off |
| Discord | `StatsWebhookUrl` | | Game reports and leaderboard |
| Discord | `PostLeaderboardAfterEachGame` | true | |
| Discord | `LeaderboardSize` | 15 | |
| Discord | `LeaderboardMinGames` | 1 | Games a player needs before they appear on the leaderboard |
| Discord | `LeaderboardMentions` | false | Show linked players as @mentions on the leaderboard (nobody is pinged) |
| Discord | `LiveStatus` | true | The live lobby status message |
| Discord | `ResultsChannelId` | | Shared channel for the combined leaderboard across hosts |
| Discord | `PublishLive` | true | Keep a "Live data" message in the private results channel for the Organiser tab |
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
| AutoMute | `LinkMenu` | true | The bot posts the live status with a colour menu for linking |
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
| Any other loss | −2 | `CrewOtherLoss` |

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
**Build** workflow by hand (Actions → Build → Run workflow) to choose the version; give it a
`release_tag` (e.g. `v0.1.1`) to publish a release, and `delete_tags` to remove old ones.

Layout:

* `src/TournamentTracker.Core` has everything that doesn't touch the game: the stat tracker,
  scoring, leaderboard, Discord REST and webhooks, the automute planner and dispatcher, and
  the commands The Button sends. It is unit-tested.
* `src/TournamentTracker.AppCore` is The Button's engine and pages (cross-platform, tested);
  `src/TournamentTracker.App` is the Windows window around it (WebView2), built in CI.
* `src/TournamentTracker.Plugin` is the BepInEx plugin: Harmony hooks on the game, and a
  per-frame driver that reads the game phase for automute. It compiles the core in, so the
  build is a single DLL.

If a game update renames a method, only the hook for that method stops working. The rest of
the mod still loads, and the BepInEx log names the hook that failed.
