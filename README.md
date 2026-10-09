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
  `/link` in Discord, the host links them from The Button, or they're linked automatically when their name matches. It works like AutoMuteUs, but the host's game drives it directly, so no
  capture app is needed.

**Platforms:** the host plays the Windows PC version from Steam or Epic Games (the Xbox app /
Game Pass version can't be modded). Everyone else can join from any platform: PC, phone,
Switch, Xbox or PlayStation.

The host's client runs the game, so it sees every kill, vote and role. That is why only the
host needs the mod.

**Discord goes through The Button.** While The Button is open it keeps the lobby's bot
connected, so the bot shows online (and `/link` is heard) before Among Us starts. The mod sends
everything it does in Discord (automute, results, the live message, webhooks) through The
Button. Among Us without The Button open is plain Among Us: the mod still tracks games and saves
them on the PC, but nothing reaches Discord, and the host's chat says so when a game starts.
Closing The Button unmutes anyone the bot had muted.

---

## Install (host only)

The current release is **v0.1.45**, a beta.

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
   sets itself up. The app connects on its own. BepInEx's black console window
   (the mod's log) is hidden; with the administration code, Settings → Administration →
   **Mod console window** shows it on your PC. The log is always in Settings → Mod and
   updates → **Open log**.

The top bar only shows the logo, plus a warning when something needs you: "No setup code
installed", "New version available", "Mod not installed", "Mod needs repair", "Among Us not
found", "Setup code damaged", "Discord bot refused" (the bot's token was reset: the host
needs a new code) or "Referee commands not heard" (the bot's Message Content Intent is off).
Click one to go where it's fixed.

**The Button updates itself.** When a new version is out it downloads it and uses it from
the next start ("Restart to update" in Settings → Mod and updates). The mod in Among Us
updates itself the same way while the game is closed. Turn **Update automatically** off
there to update both only when you choose.

While you play, the app's pages run the lobby:

* **Home:** a line at the top with the game's latest warnings (a player left and whether to
  void, an extra game, settings put back…; "N more" opens the rest, ✕ dismisses); a
  checklist of what the lobby needs (mod up to date, setup code, round set, Discord voice,
  everyone linked, the lobby message, sending video), red or amber when something's
  missing; and the whole lobby on one page, up to 15 players: code, server, map, round, each player
  as their crewmate with their Discord link and whether automute has them muted or deafened;
  the automute buttons (automute on/off, referee mode, spectator muting, unmute everyone);
  start the next round; the settings lock, referee ghost slot and
  stream overlay switches; post the leaderboard or server standings, or repost the lobby
  message.
* **Referee:** every game waits here before Discord sees anything. Under **Waiting for you**
  each finished game shows every player's points (hover for where they came from): **−** and
  **+** change a player's points by one (it shows as "Referee" in their breakdown), **Void**
  keeps it out of the points and off Discord for good, and **Submit points** posts it (the
  report, the standings, the round's scores). **Submit all** sends every waiting game. The
  number on the tab says how many are waiting; they stay waiting across a restart.
  Below: the game running now (void or unvoid it), every recent game with **Void** / **Unvoid**
  (Discord is told, since it was posted), the point totals (players never see them in the game
  or through the bot: this lobby's round with the cut line and running totals, every lobby's
  round with a results channel, from round 2 the running total; with a preliminary code the
  lobby's leaderboard), the results-channel commands for whoever has the administration code,
  and **Reset all points**: type RESET to put every game on this PC away (moved to a dated
  "reset" folder in the games folder, never deleted) and start the totals and round counts
  from zero. Discord isn't touched. `VerifyResults = false` in the config posts games as soon
  as they end instead.
* **Settings:** the game folder; the mod and The Button's updates; the setup code as one line (details, change or
  remove it); **Your Discord** (below); in a tournament with a results channel, the **lead lobby** and the combined
  leaderboard reset; files under **Advanced**.

**Your Discord** (Settings): the host's own bots and channels, so the organiser doesn't need
anyone's bot token and a host can change bots without a new code. Up to 3 bot tokens, the
server ID (a tournament code's server is used if it's left empty), a **public report
channel** webhook and a live lobby channel webhook. **Save and check** asks Discord about each
one: a token that's refused, or a bot that isn't in the server, is not saved (and says why);
the rest is. Saved bots go online whenever The Button is open (with a setup code in), and
are used instead of any bot in the code. Tokens stay on the PC (the screen shows only each
bot's name and the token's last 4 characters). With a preliminary code, reports and the
lobby's standings go to the public channel (without the data file); the organiser's
private channel from the code still gets everything. With a tournament code, the public
channel is used only when the code has no results channel webhook.

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

* **Private reports channel (preliminaries):** yours, staff only. Every preliminary's reports
  and data land here, with each lobby's count of impostor against crew points kept at the
  bottom. Make one webhook and use it in every preliminary code. Each preliminary server
  keeps its own public channel, which its host sets in The Button.
* **Results channel:** tournament game reports and standings for players. Make a webhook.
* **Private results channel:** staff only. The hosts' mods post each game's data here,
  referees type point adjustments here, and `!resetleaderboard` here starts standings over.

### 2. Bots (tournament hosts only)

Create one bot per tournament lobby that runs at the same time, at
<https://discord.com/developers/applications> (New Application → Bot → Reset Token). For
each: turn on **Message Content Intent** (Bot → Privileged Gateway Intents), and invite it
with this link (put in the application ID):
`https://discord.com/oauth2/authorize?client_id=APP_ID&scope=bot+applications.commands&permissions=14011456`
(view channels, send messages, embed links, attach files, read history, add reactions, use
external emojis, connect, mute and deafen members, and the `/link` and `/unlink` commands,
which appear in the server the first time a host opens Among Us with the code). Never
Administrator: see *What the bots need (least privilege)* for the per-channel version. Preliminary hosts don't need a bot,
but can have one for automute (see below).

### 3. Setup codes

Download **`setup-codes.html`** from the [latest release](https://github.com/Ljbutton/AU/releases/latest) and open it in your browser (it runs on your PC only; nothing you type is sent anywhere). Once your administration code is in, Settings → Administration → **Make setup codes** opens the same page. It fits on one screen: pick the kind of code at the top, then fill in
the three columns in order (Tournament and Discord, Rules, The code). The code column lists
whatever is still missing and ticks it off; **Point values…** and **Load an old code** open on
top. Fill it in:

* **Preliminary code:** the tournament name, the preliminary server's name and your
  **private reports channel** webhook. Make one per preliminary server (the server name is what
  server standings use). Safe to hand out. The host adds their own bots and their server's
  public report channel in The Button (Settings → **Your Discord**), so leave the bot empty:
  * **Automute in the code (optional, older way):** a server ID and a bot's token (and a
    live lobby channel webhook). A code with a bot token in it must be sent privately.
* **Tournament host code:** the tournament name, the results channel webhook, and
  optionally your server ID, up to 3 bot tokens (or none: with the server ID and no tokens,
  the host adds their own bots in The Button), the private results channel, a webhook for
  its own live lobby channel (otherwise the lobby message goes in the results channel, or
  where the host types `/new`), the preliminary channels and the referees' user IDs. The bot is optional: without one, games are still tracked, scored
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
  vision, kill distance, tasks, the task bar (in meetings only by default: the host's own screen always shows the real bar), ghosts doing tasks, and the roles). **Roles…**
  sets each role (Engineer, Scientist, Guardian Angel, Noisemaker, Tracker, Detective, Judge,
  Spirit Guide, Shapeshifter, Phantom, Viper) to a number per game and a chance; every role
  left at 0 is off. Each role's options (cooldowns, durations, "leave evidence"…) are held too
  when filled in; an empty one keeps the host's own value. Task counts stop at the game's own
  menu limits (common 4, long 15, short 23). Anything changed in the lobby is put
  back and the host is told, once, in their own chat only (not again on every join, until the
  next game). A game that still starts on the wrong settings says so in its
  report (and to the referees). For a casual game the host turns **Settings lock** off on
  The Button's Home page (until they restart Among Us); without a code nothing is ever touched.
* **Hold each game until the host presses Submit** (on by default): every game waits in
  The Button's Referee tab, nothing goes to Discord until the host checks it. Untick it to
  post results as soon as a game ends.
* **Impostor rotation** (ticked by default in the code maker; codes made before 0.1.43
  have it only if it was ticked): last game's impostors are rarely impostor
  again straight away. Each of them has a 2% chance (set it in the code maker)
  and everyone else shares the rest equally, so in a 10-player game with 2 impostors the
  other 8 each have about 24.5%. Back to back is possible but rare (about 1 in 50), three
  in a row almost never happens. The mod swaps the roles the game handed out, and announces
  it when a round starts.

### 4. The combined preliminary leaderboard and count

Each lobby's mod keeps its own **count** at the bottom of your private reports channel after
every game: of all the points scored there, the share that went to impostors and to crew
(with the totals, points per player per game, wins and games). A scheduled GitHub job builds
one leaderboard per preliminary across all its lobbies every 10 minutes, and next to it the
**all-games count**: every lobby together, then each lobby on its own line. In the GitHub repository: Settings →
Secrets and variables → Actions → add the secret `DISCORD_BOT_TOKEN` (one of your bots,
invited to the server with the private reports channel) and the variable `PRELIM_CHANNEL_IDS`
(the private reports channel's ID; several, comma separated, if you use more). Optionally set `PRELIM_LEADERBOARD_CHANNEL` to post every leaderboard
in one channel. Scheduled runs only happen on the repository's default branch, so merge
this branch into it first. Actions → *Preliminary leaderboards* → Run workflow updates it
right away.

### Modes

| | Preliminary code | Tournament host code |
| --- | --- | --- |
| After each game | Report plus the game's data and the lobby's count in your private channel; report and standings in the server's public channel (set in The Button); a summary in the host's chat | Report in the results channel; the lobby's round standings |
| Leaderboard | The scheduled job's combined board per preliminary | Per lobby per round, with the cut line, plus a running total |
| Automute | Optional: the host adds bots in The Button (otherwise players can use AutoMuteUs) | Yes, with the code's bots or the host's own |
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
* **Caster tools** (the caster overlay, game video, the Caster tab, OBS, replays) are in
  their own app now, **Red Alert** (below), for whoever runs the stream. The Organiser tab
  links to it.
* **Referee** (on **Home**, for whoever has the administration code): adjust points, void or
  unvoid a game, or type any `!` command; it's posted in the results channel as the
  administration bot and the lead lobby carries it out. On a PC without Among Us, Home
  waits for the game as usual, with the referee tools below.

The administration bot needs View Channel, Read Message History and Send Messages in the
private results channel, and its **Message Content Intent** turned on (Discord Developer
Portal → Bot). Hosts can turn the live data off with `PublishLive = false`.

### Replays

Every game is recorded: each player's position about ten times a second (and whether
they're dead or in a vent), the map's walls, rooms and vents read from the game, and the
game's events. The file (`tt-replay-LJ-3-….json.gz`, about 0.5 MB) is saved with the game on
this PC only (the newest 20 are kept) and never posted to Discord: on stream, replays are Red
Alert's, cut from the game's real picture. Open one in `docs/replay-viewer.html` if you want:
play, pause, scrub, 0.5–8× speed, jump to any kill or meeting, follow a player, show bodies,
trails, vents, roles and ghosts. `RecordReplays = false` in the config turns recording off.

**Watching in Among Us, on the real map.** Anyone with the mod can open **Freeplay** on the
replay's map and press **F8** (Freeplay reminds you as it opens): it lists the newest replays
of the games this PC hosted; press 1–9 to pick one. The players
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
The mod sends each player's role and task list once, as the game itself does, and never sends an
"exiled" message outside a meeting (that got the host kicked from the room): the referee is marked
dead in the player record the host keeps, and the host's own game makes them a ghost. As the
game starts, and after every meeting, the referee's ghost is parked off the edge of the map, where
no player (not even a dead one) ever sees it, and isn't drawn on the host's own screen either. The
host's camera stays over the middle of the map, zoomed all the way out; the **arrow keys** (or
**WASD**) move it and **Home** brings it back. No room name shows at the top. In meetings the
newest chat messages show along the bottom edge of the screen, below the name plates (the **mini
chat**), so the chat doesn't have to be opened to follow the vote; it isn't shown during play.
The chat button still opens the full chat to type. Every kill plays the kill sound on the referee's game (the game
itself only plays it for the killer and the victim), so the host, and the stream's whole-map
view, hear it. Their task bar is smaller
and always shows the real progress, whatever the lobby's task bar setting. The host turns down any kill on the
referee. In meetings the referee's card is taken off the list. That happens on the host's screen and on every player's game that has the
mod; there the referee isn't drawn on the map either. A game without the mod lists them as a dead
player.

## Red Alert (the broadcaster's app)

<img src="docs/red-alert-logo.svg" alt="" width="56" align="left">

**Red Alert** is a separate Windows app for whoever runs the stream: the Caster tab, OBS
scenes, replays and montages, the on-stream graphics, the caster overlay and video pages,
spectator view switches, Twitch, and Simulation. Hosts, referees and the organiser only need
The Button; a host still turns on **Send my game to the caster** in their Button, and Red
Alert receives it. It shows who the impostors are, so it stays on the caster's PC and off
stream.

* **Install:** download **`Install-RedAlert.bat`** from the
  [Red Alert releases](https://github.com/Ljbutton/AU/releases?q=broadcast-v) (tags
  `broadcast-v…`, a beta from v0.1.0) and double-click it. It installs to
  `%LOCALAPPDATA%\Programs\RedAlert` with Start menu and desktop shortcuts, and can sit
  next to The Button on the same PC.
* **The side menu** has a page per job: **Live desk** (everything for the show on one screen: on
  air and the layout buttons along the top, then three columns that scroll on their own: the
  lobbies with their pictures, what's happening, and the player cams, talking points, graphics
  queue and earlier plays), **Lobby health**, **Montages**, **Graphics**, **Standings**, **Sponsors**,
  **Twitch**, **OBS**, **Lobby voice**, **Players** and **Settings** (player camera, Stream Deck,
  updates). A dot beside an item shows what needs a
  look; the number on Live desk is new plays since you last looked there (it clears when you
  open it). The replay keys work on every page; 1-9 and N on the Live desk. Each card's
  explanation is behind its small **?**.
* **On stream, at a glance:** the top of the Live desk says what OBS is showing right now:
  LIVE (red), BREAK (amber) or OFF AIR, the layout and lobbies, whose lobby voice is heard, and a
  shot timer that starts again on every switch. It follows OBS: switches made in OBS by hand
  show too, and a switch OBS doesn't make within a couple of seconds is a red error. Beside it,
  **Talking points**: the best two storyline notes about the lobbies on screen (Next, Used).
* **Layouts:** **Views** (Full, 2-Up, Quad, Grid, Auto grid) and **Breaks** (Be right back,
  Intermission, Auto intermission, in amber) are separate rows, so a break isn't clicked by
  mistake.
* **Pick, then Send:** clicking a lobby anywhere (a card, the lobby list, a tile, the Multiview
  pictures, keys 1-9) picks it under **Next**, numbered in order; nothing changes on stream until
  **Send** (or Enter; Esc clears). One lobby goes full screen, two side by side, three or four in
  the quad, five and more in the grid; a **Views** button chooses the layout instead. Breaks go on
  at once.
* **On stream** says **EMPTY** (not LIVE) when a layout has no lobby in it, and the Live desk warns
  when a lobby on stream isn't in OBS ("LJ isn't in OBS: …"), with **Fix** (it rebuilds the TT
  scenes). No button fails silently: anything that goes wrong says why.
* **OBS that keeps a removed source:** OBS can keep a removed source alive in the background,
  still listed but refusing to go in a scene. Red Alert then makes a fresh one under a new name
  ("TT Lobby LJ 2") and carries on; a source that can't be made is tried again after 30 s or on
  Rebuild, never in a loop. No need to restart OBS.
* **One order:** each lobby gets a number when it first connects and keeps it all session. The
  lobby cards, the Multiview tiles, keys **1-9** (that lobby full screen) and the stream use it;
  how exciting a lobby is shows as a badge and a glow, never by moving it. In 2-up, Quad and
  Grid the numbers on stream are where each lobby is on screen (1 top left).
* **Graphics queue:** one big graphic on stream at a time (a lobby's table after a game, a
  player card, a storyline note, the standings), each for 8 s (change it on the Live desk), then
  the next. One about a lobby goes on that lobby: full screen, inside its tile in a multi-view
  when there's room, or in intermission; otherwise it waits. Off-screen alerts are one at a
  time. The Live desk shows what's on and what's next, with **Skip** and **Clear**.
* **After a game** the Live desk asks "MAL finished, show the table?": **Show**, **Skip** or
  **Save for intermission** (saved tables play when intermission comes up). Unanswered, it goes
  after a minute. Graphics → After a game → **Put the table up by itself** brings back the old way.
* **Simulation** (OBS → Test): four fake lobbies, SIM-1 to SIM-4. While it runs a **TEST MODE**
  bar with **Stop simulation** sits across every page. Stopping removes every trace of them
  (lobbies, cards, games and standings, clips, montages, their OBS sources) without switching
  anything in OBS; simulated games never reach the sponsor log or a real Twitch channel.
* **Unlock** it with the tournament's administration code (the same one as The Button's
  Organiser tab).
* **First start:** on a PC that already had The Button's caster tools, Red Alert copies
  their settings once (never overwriting): `obs.json` (OBS, replay folder and keys),
  `caster-priority.json`, `roster.csv`, `broadcast.json`, `sponsors.json`, `health.json`, the
  Twitch settings and sign-in, the kept games and the administration code. Its own folder is
  `%LOCALAPPDATA%\RedAlert`.
* **It was called TT Broadcast** (v0.1.0). Installing Red Alert replaces it (its shortcuts and
  program folder go) and its settings come over the first time Red Alert opens; a TT Broadcast
  that updates itself becomes Red Alert where it is.
* **Updates (Settings):** Red Alert never updates by itself, so nothing changes in the middle of
  a stream. **Settings → Updates** says when a new version is out (it looks every 30 minutes, or
  **Check now**); **Update** downloads it and **Restart Red Alert** starts it. Hosts never need
  to reinstall anything for a broadcast change.
* **Player camera:** a second picture from each host's game that follows one player up close,
  sharp at 720p and up to 30 frames a second, sent with the host's game. It's on by default
  (Settings → **Player cameras** turns it off). The Live desk's **Player cams** list has every
  live lobby's players: click one to put that lobby's camera on stream following them, or
  **Auto** to let it pick (an impostor closing in on someone alone, else whoever is busy; a pick
  is kept a few seconds after they die, to see it). To show several at once, pick the lobbies,
  press **🎥 Player cams** in Views and Send: two side by side, three or four in a quad (clicking a
  player then only changes who that lobby's camera follows). When the game ends the stream goes
  back to the whole map by itself. The camera darkens what the followed player can't see, as on
  their own screen, and the referee sees task animations even with visual tasks off for the
  players (when their game sends them). Red Alert adds a **TT Player Cam** scene to OBS: one
  camera full screen with the lobby's whole map small in the bottom right, or the cameras side by
  side. Switching to it, back, and from player to player is a straight cut (no
  swoosh). How it gets to you: the host's mod draws it off screen (the host never sees it and
  can't change it) and hands each picture to The Button, whose *Send my game to the caster* page
  sends it as its own VDO.Ninja stream next to the game and the lobby voice; OBS's TT Cam source
  shows that stream. It costs each host about one more video stream of upload, and only while
  they send their game.
* **Player camera sound:** while a lobby's player camera is on stream, its host's game sound is
  what the followed player would hear: sounds are measured from where they stand (room ambience,
  vents, doors), their own footsteps play, and a kill is heard only if they're the killer or the
  victim, as in the game. With the whole map on stream it's back to the referee's: alarms and every
  kill. Red Alert tells each host which view is on stream, by itself; their task sounds stay on
  their own PC.
* **Player camera replays:** the camera keeps its last 30 s in OBS like the lobbies, so every
  replay saved while it was on has a second angle. A kill the camera was following opens on the
  close-up; **🎥 Player cam / 🗺 Whole map** in the replay controls (key **P**) switches angle at
  the same moment.
* **Stream Deck (Settings):** a link per action for the Stream Deck's **Website** action (tick
  **GET request in background**): a lobby full screen (**lobby/1**, **lobby/2**…), next and
  previous lobby, where the action is, quad, grid, the player camera (by itself, next and
  previous player, or a colour: **cam/red**, **cam/blue**…), back to the whole map, and mute all
  lobby voice. The links work while Red Alert is open on this PC; **New key** makes new ones.
  8 keys fit lobby next/previous, action, quad, player cam, next/previous player and the whole
  map.
* **OBS setup check** (OBS page, when OBS connects): the scene collection and profile names
  (steps to rename them), a leftover empty "Scene" (**Delete it**), Desktop Audio reaching the
  stream (**Mute Desktop Audio**; the Live desk warns while it's on), what replays need (the
  Source Record plugin; OBS's own Replay Buffer isn't used and can stay off), and Impostor tags
  without a stream delay (**Set a 90 s delay**).
* **Ports:** its screen is on `127.0.0.1:8768` (this PC only, with a private key); the OBS
  pages are on `localhost:8767`, as before. The mod uses 8765 and 8766 on the host's PC.
* **Older hosts:** hosts send their game data in a versioned format
  ([docs/broadcast-protocol.md](docs/broadcast-protocol.md)). A host whose mod is older than
  Red Alert still shows, with **host needs update** in Lobby health.

### Caster overlay and game video

* **Jump to action** (or **N**): puts the lobby worth watching on stream (a meeting first, then
  whatever just happened, else the next lobby in turn). A lobby whose host has gone silent
  for a couple of minutes gets a warning at the top.
* **Caster overlay:** an OBS Browser source at `http://localhost:8767/` that follows what's on
  stream (the full-screen lobby, or slot 1 of a multi-view), as do the video and multiview
  pages; **Pin** keeps them on one lobby (shown as PINNED, with **Follow the stream**). It shows
  players, round standings and the latest meetings and ejections for that game.
  `http://localhost:8767/?full=1` adds roles, kills and task bars (for a stream on a delay
  only).
* **Game video, RedZone style:** each host turns on **Send my game to the caster** (Home →
  Tools) in their Button. It opens a private VDO.Ninja page in their browser; they choose **Entire
  screen**, tick **Share system audio** (a window share has no sound on Windows) and leave
  the tab open. The sound is everything the host's PC plays, Discord included. The link is random, kept for the tournament, and only
  written in the private results channel. In your OBS:
  * `http://localhost:8767/video` (1920×1080): the lobby you're casting, full frame, with its
    game sound. Every lobby's picture stays connected in the background, so picking a lobby
    switches instantly; the sound follows a moment later and only the lobby on air is ever
    heard. Put the caster overlay on top of it. `?sound=0` for picture only.
  * `http://localhost:8767/multiview` (1920×1080): every lobby at once, with its name,
    phase and players alive, the one on air outlined, and a red mark on a lobby with a
    meeting or an ejection happening. Silent (the video page carries the sound);
    `?sound=1` plays the lobby on air if you use the multiview on its own.
  * The Caster tab ranks what's happening in every lobby, so you know where to jump.
  * Each host needs about 3-6 Mbps upload spare, and you download every lobby at once, so
    use a wired connection.
  * Put a delay on your public stream (OBS → Settings → Advanced → Stream Delay, 2-3
    minutes) so players in other lobbies can't watch it for information.

### Caster tab (RedZone broadcast)

Red Alert's main screen is the **Caster** tab: every lobby's live play,
ranked, and one click to put a lobby on stream. It shows who the impostors are, so it stays on
the caster's PC and off stream.

* **Where the data comes from:** each tournament host plays as the referee ghost and turns on
  *Send my game to the caster*. Their Button opens its own page with VDO.Ninja inside: it shares
  the screen and sends the mod's live data (kills with room and position, meetings, ejections,
  sabotages with time left, vents, task bar, kill-ready, danger, a snapshot every second) along
  the same private stream. Red Alert joins each stream for its data. A lobby that
  stops sending shows as offline; the rest keep going.
* **Cards:** "Happening now" lists plays highest priority first (must show, very high, high,
  medium), with the lobby, the play ("Reactor 12s, nobody fixing", "Purple killed Lime in
  Electrical"), crew v impostors, task bar and time. Repeats update the same card. Each card and
  lobby says if it's on stream and how (LIVE (full), LIVE (quad, slot 2)); plays that happened on
  stream get a SHOWN badge. Calmed-down plays move to *Earlier*, with **Watch again**.
* **Ranking:** every weight and limit is in `caster-priority.json` in Red Alert's
  folder (written the first time, re-read when you save it).
* **OBS:** in OBS, Tools → WebSocket Server Settings → Enable WebSocket server, set a password,
  and connect from the Caster tab. Red Alert builds the scenes **TT Full**, **TT 2-up**,
  **TT Quad**, **TT Grid**, **TT Sponsor Break** and **TT Intermission** with one source per lobby (each stays connected, so switching is instant), places
  the lobbies in their slots, plays only slot 1's game sound, and switches scene when you click.
  It never touches your other scenes. Switch in OBS yourself and the LIVE labels follow.
  Settings and the lobby → source mapping are in `obs.json` in Red Alert's folder.
* **Replays** (needs the free **Source Record** plugin for OBS, by Exeldro): Red Alert puts a
  Source Record replay buffer (last 30 s) on every lobby source. Every kill saves a clip by itself
  (8 s before to 2 s after); any card can save one with **Save replay**, while OBS still has it.
  **▶ REPLAY** on the card (or **Watch again**) plays it in the **TT Replay** scene: a Media
  Source, so you can pause and scrub, cropped and zoomed (up to 2.5×) to keep the killer and
  victim in frame, following them if the host's camera moves, with a slow push-in from the wide
  shot and a REPLAY tag. Controls: play/pause, ±1 s, frame step, scrub bar, restart, zoom, pan,
  follow, back to live; every one has a key (change them under **Keys**). Clip lengths, zoom,
  folder (default Videos\TT Replays) and keys are in the `replay` part of `obs.json`. If Source
  Record's buffer on a lobby isn't running (it sometimes doesn't start with OBS), the save says
  so, Red Alert starts it again, and the next save works.
* **Spectator view** (the host's own screen, while they play as the referee ghost; drawn only in
  their game, never sent to players): the whole map lit with every player shown (impostors in
  vents stay hidden); **vision** CREWMATES shows every living crewmate's real sight together
  (wall-blocked, their vision setting, lights sabotage included, soft-edged and redrawn every
  frame) with the rest of the map a little darker (or just one crewmate's, if you pick one),
  or OUTLINES draws every living player's sight in their colour; impostors' names in red (in the game and in
  meetings); a faint **eye** by a crewmate's name while an impostor is in
  their sight, flashing when they see a kill or a vent, which also makes a "Lime SAW Purple
  vent in MedBay" card. Switch each one per lobby under its row in the Caster tab (it goes to
  the host over their VDO.Ninja link), or with keys for the lobby on stream (M lit, V vision,
  E eye; change them under Keys).
* **Real names:** `roster.csv` in Red Alert's folder (name, Discord ID, in-game names,
  friend codes, pronunciation; `;` between several). Each player is matched by the Discord
  account automute links them to, then friend code, then in-game name; pick someone by hand in
  the **Players** card to override. Names show with their colour swatch everywhere (cards, on
  stream, montages), and each referee's nameplates show the roster names (on their screen only).
* **On-stream graphics:** one transparent 1920×1080 page (`/broadcast` on the caster port; Red
  Alert adds it as **TT Broadcast** on top of every TT scene) that places each graphic on the
  right lobby in every layout. Colours, fonts and logo are in `broadcast.json`; switch each
  graphic on the **Graphics** page:
  * **Lobby labels:** the host's Twitch channel with Twitch's logo (so viewers can find their
    stream), else the lobby's name. Hosts type it in The Button: Settings → **Your Twitch**.
  * **Top 3:** top left, each playing lobby's top three for its own round, a different lobby
    every 8 s.
  * **Stats ticker:** along the bottom, leader boards taking turns five names at a time, like a
    football broadcast: tournament points, sharpest voters (vote %), most kills, impostor win %,
    task machines and survivors (top 10 of the counted games).
  * Impostor tags per feed (dead crossed out), a big reactor/O2 countdown with the fixing
    progress (lights and comms as a small icon), grid tiles, standings, points on the line,
    standings changes, storyline notes, off-screen alerts, the win counter and player cards.
  * **Hide room code** (off by default: hosts stream with Among Us's streamer mode on): a "Room
    code hidden" box over the room code while a lobby on stream is in its lobby or menu, so
    viewers can't join. Where it goes is `roomCodeBox` in `broadcast.json` (shares of the game
    picture).
  * Graphics slide to their new places when the layout changes and fade in and out, so a switch
    is smooth under the swoosh.
* **Grid:** every active lobby at once in **TT Grid** (1 full, 2 side by side, up to 4×4), each
  tile with its label, a status line, the impostor tag and a border that pulses at high
  priority; empty tiles show a sponsor or the logo. Click a tile (on stream) for full screen.
  **Auto grid** brings it up whenever no lobby is mid-game.
* **Multiview:** the **Multiview** card on the Live desk has a small live picture of every lobby
  sending its game. Click the ones you want (they're numbered in the order you pick), then
  **Send**: one goes full screen, two side by side, three or four in the quad, five and more in
  the grid. Nothing changes on stream until you press Send.
* **Stats and storylines:** every game is kept on the caster's PC (`broadcast-games`), and the
  **Standings & storylines** card lists talking points from them (records, streaks, first
  blood, rivalries, who keeps getting voted out, milestones), the ones about players on stream
  first: pin, dismiss, mark used, or put one on stream as a lower third.
* **Standings** come from the tournament's own scoring (the same tables as the Points page and
  Discord: `Stats/Scoring.cs` and `Stats/Standings.cs`, rules from the setup code). On the
  **Standings** page: **Lobby on stream** (the default: the lobby full screen, or slot 1 of a
  multi-view, in its own round), **Round · every lobby** (that round's totals, all lobbies) or
  **Tournament**. Lobbies can be in different rounds (a big tournament starts round 2 in one
  lobby while another finishes round 1): each lobby's table and each round's totals only count
  their own round's games, and before a lobby's first game of a new round its last round's final
  table stays up. Near the end of a game, *points on the line* shows what each ending does to
  the lobby's table; after each game, arrows show who moved.
* **Montages:** clips are saved by themselves for kills, ejections, meetings, witnessed kills and
  game ends. When a game ends Red Alert builds a 30–60 s montage of it with ffmpeg (each moment
  cut to a few seconds, cropped like replays, a lower third such as "Jake → Maria,
  Electrical", wipes between them); when a round ends, "every kill" then the top plays counting
  down. The **Montages** card shows them ready with Play (in the replay scene), Preview and
  Discard; **Get ffmpeg** downloads it the first time. The **Moments** card lists every clip:
  pick some, drag them into order and Build montage; its clips are marked USED and can go in
  new montages. Once a montage has played it moves to the **Archive** (title, when, game or
  round, its clips): **Replay**, **Rebuild** from its clips, **Open** the video or its
  **Folder**, or **Delete** for good (a second click confirms).
* **Sponsors:** `sponsors.json` (name, logo, tagline, video, placements). Placements: `killcam`
  ("Kill Cam presented by" on kill replays), `replay` ("presented by" on every other replay),
  `multiview` (a "Multiview presented by" badge while 4 or more lobbies are on screen),
  `montage` (an opening card), `standings`
  ("Presented by" under the table), `grid` (empty tiles), `break` (a split-screen break, the
  lobby on the left and the sponsor on the right, that ends by itself, and straight back to
  full screen on a must-show play). **Verbal reads:** give a sponsor `readScript` and
  `readEveryMinutes`, `readEveryGames` or `readEveryRounds`; when one is due the Live desk
  shows the script (only to you) with **Done** (logged as a read) and **Snooze 5 min**.
  Every appearance is logged; **Export report** saves a CSV
  and a summary.
* **Swoosh:** a stinger plays on every switch (scenes, pictures moving within a layout, the grid
  changing, replays coming up), once per 1.5 s, never while scrubbing a replay. Add a Stinger
  transition called **TT Swoosh** in OBS (Scene Transitions → +) to have OBS play it for scene
  changes; Red Alert sets its video. It's made with ffmpeg in the tournament's colours (from
  `broadcast.json`): a slanted band with crewmate heads of every colour tumbling across it and the
  logo in the middle as it covers the screen; it's made again when the colours or logo change.
  `swoosh.path` in `obs.json` uses your own video instead.
* **Off-screen alerts:** a banner for each kill, win, body report and emergency button in a lobby
  that isn't on screen; about 4 s each, three at once, the rest queued, merged per lobby. Each
  kind switches off in the graphics card; **B** pauses them all.
* **Intermission:** the **TT Intermission** scene between rounds: a countdown to the next round
  (set it in the tab: minutes or a time), standings, the win counter, storyline notes taking
  turns and the montage up next. Offered when every lobby has been out of a game for 45 s, or by
  itself with **Auto intermission**; a lobby going live brings it back (auto) or says so.
  Key **I**.
* **Win counter:** IMPOSTOR WINS · CREWMATE WINS, today or this round, in the corner and in
  intermission (top middle on the player camera, clear of the host's picture and its chat).
* **Player cards:** a lower third with the player's real name, rank, points and today's
  impostor and crewmate records and kills. **Card** buttons on every notification, lobby and
  roster row, key **C** for the top notification's player, and by itself for the key player of
  each replay and montage moment. One at a time, gone after 6 s; on the lobby's tile in a
  multi-view, or skipped when the tile is too small.
* **Lobby voice:** the referee's Button captures what Discord plays (the lobby voice as they
  hear it, which never includes their own voice) and what Among Us plays, each on its own
  (Windows 10 2004 or later; no virtual cables), mixes them with a level for each and, if they
  tick it, their microphone, and sends that as a second VDO.Ninja stream next to their screen.
  Their send page shows a meter for each. In OBS each lobby gets a **TT Voice** source; the
  voice follows the picture (the full-screen lobby, or slot 1), and replays, montages and
  intermission are silent. The **Lobby voice** card shows each lobby's status and levels, and
  has a volume and delay per lobby, a live meter of what OBS's TT Voice source is putting out
  (also small on each lobby card), **Listen** to keep one lobby up whatever is on screen,
  **Mute all** (key **U** in Red Alert, and **Ctrl+Shift+M** from any window, changed in Lobby
  voice; the Live desk shows ALL LOBBY AUDIO MUTED with Unmute while it's on), and **Duck
  under** your mic (an OBS compressor keyed to it; the obvious mic is picked the first time). It only
  goes to the stream: nothing is played back to the players, and when voice sending starts the
  bot posts "voice in this channel may be recorded" in the lobby's voice channel chat. On older
  Windows, the referee ticks *Share system audio* instead (the old way: game sound and voice
  together, no separate levels).
* **Game replays kept by Red Alert:** every host's mod sends its game's replay (positions, timeline,
  walls) as the game is played, and Red Alert keeps it (`game-replays` next to its settings). So a
  game can be watched, to count, void or replay it, even when the host's PC drops out before the
  game is posted: the copy runs up to where the host stopped sending. **Lobby health → Game
  replays** lists them with **Watch**; interrupted games have a Watch button too. It's the same
  replay viewer as The Button's and shows the impostors: keep it off stream.
* **Auto switch** (Lobby health, shown ON/OFF, and "Auto switch ON" by the On stream indicator) moves
  off a lobby that drops while it's on stream, once per outage, and the Live desk says what it did
  with **Undo / keep**. A lobby you put on yourself while it's down stays on (with a note) until it
  comes back and drops again.
* **Lobby drops:** the **Lobby health** card shows each lobby green (all good), yellow
  (degraded: no data for 3 s, sound silent for 2 min while sending voice, a voice problem, data
  2 s later than usual) or red (down: no data for 10 s, video lost for 3 s, data more than 8 s
  late), with the problem in plain words. All of it is in `health.json` (**Thresholds**).
  * A lobby on stream that goes red is switched away from at once, even when you switch by hand
    (**Auto switch** turns that off): to the next lobby worth showing, else the grid. A card says
    what happened; another says when it's back. It isn't put back by itself.
  * In 2-up, quad and the grid the tile says **LOBBY 3 · RECONNECTING** and its graphics (label,
    impostor tags, countdowns) stay at their last state, greyed and marked;
    after 45 s the next lobby takes the slot (the grid closes up).
  * Every lobby down mid-game: the **TT Be Right Back** scene ("Technical difficulties", standings
    and storylines taking turns); between games it's intermission instead. **Be right back** puts
    it up by hand. It goes back to a lobby when one returns.
  * Nothing is lost: the referee's page keeps every message until Red Alert says it
    arrived and sends the rest again when the link is back, with their own times. Repeats are
    dropped; anything more than 8 s old counts for the stats but is never shown as live. The
    referee sees "Disconnected from caster, reconnecting…" meanwhile; their game isn't touched.
    Data links reconnect by themselves (5 s, then longer each time, up to a minute), and so does
    lobby voice.
  * A referee's game that crashes or restarts mid-game (or starts a new game before the last one
    ended, or abandons it) is **INTERRUPTED**: a card, and that game stays out of the standings
    on stream until you pick **Count**, **Void** (posts `!void <game> interrupted`) or **Replay**
    (void it, and a card to replay it).
* **Twitch:** the **Twitch** card. Set up once:
  1. At dev.twitch.tv → Your Console, register an application with client type **Public**
     (any OAuth redirect URL, e.g. `http://localhost`).
  2. Paste its Client ID into the card.
  3. Press **Sign in**: enter the code shown at twitch.tv/activate, signed in as the channel.

  Red Alert keeps the sign-in fresh (it's in `twitch-token.json` on this PC only) and listens to
  the channel with EventSub. Settings are in `twitch.json`. Polls, predictions and channel point
  rewards need **Affiliate or Partner** (the card says so if the channel isn't); the !sus vote and
  Chat Detective work on any channel. Twitch allows one poll and one prediction at a time; polls
  have 2–5 choices of up to 25 characters and last 15–1800 s; predictions have 2–10 outcomes and
  a 30–1800 s window. Red Alert keeps to those. **Stream delay** (0 by default) holds every
  action back so it matches what viewers see.
  * **Predictions:** "LJ: Impostors or Crewmates?" opens when a game starts in the featured lobby
    (the one you pick, else the one on stream), locks after 90 s and resolves when the game ends;
    an interrupted game cancels it and everyone's points go back. By hand: **First to finish**
    (which lobby ends its game first) and **More wins** (more impostor or crew wins across the
    lobbies playing). Lock, cancel or resolve from the card any time.
  * **Meetings** in the lobby on stream: a "Who's the impostor?" poll when five or fewer are alive,
    else a **!sus name** chat vote (a full or first name, in-game name or colour; one vote per
    viewer, the latest counts). The stream shows CHAT THINKS with live bars and, after the
    ejection, what chat thought and who went. Whether chat was right is shown when the game ends
    (tournament lobbies don't confirm ejects; tick the setting if yours do).
  * **Chat Detective:** each !sus vote for a real impostor scores; the leaderboard is in
    intermission, and the round's best get a shoutout in chat and on stream when it starts.
  * **Which lobby next?** in a calm moment (by hand, or by itself with Auto): chat's pick becomes a
    card with a **Put it on** button. Nothing switches until you press it.
  * **Round MVP:** a poll of the round's top five from the standings when intermission starts.
  * **Channel points:** Red Alert makes "Request a replay" and "Shoutout a player". Requests wait in
    the card: approve a replay (pick the clip) or a shoutout (their player card goes up and chat is
    told), or deny it (points back). Shoutouts must name someone on the roster; a filter turns
    away bad words at once, with the points back.
  * **Nothing gives impostors away:** options are shuffled, players are listed by real name only,
    and nothing from the caster's own view is used.
  * **Test mode:** a fake Affiliate channel with a pretend audience (votes, !sus, redemptions), so
    all of it can be tried with Simulation and nothing is sent. **Twitch off** stops everything.
* **Simulation:** the Simulation button plays four fake lobbies (with stand-in video in OBS, or
  stand-in clips made with ffmpeg when OBS isn't connected), to try everything above without
  real games. In simulation each lobby's health row has test buttons: Video, Audio, Data, All
  (drops), Lag, Crash (the referee's game dies mid-game and comes back in the lobby) and
  Reconnect.

## Discord setup (without a setup code)

### Stats (webhook, no bot needed)

Channel settings → Integrations → Webhooks → New Webhook → Copy URL. Paste the URL into
`StatsWebhookUrl`.

The same channel gets a **live status message**: the lobby code, the server (NA, EU, Asia or a custom one), map, phase, and each
player's colour, name and Discord link. It updates as people join, link and play, and moves
below each game report so it stays at the bottom. Deaths only appear once the game has
revealed them (at a meeting or the end), so it never gives away a kill. Give it its own
channel with `StatusWebhookUrl`, or turn it off with `LiveStatus = false`. **Repost lobby
message** on The Button's Home posts a fresh copy.

### Automute (bot)

1. Create an application at <https://discord.com/developers/applications>. Under **Bot**,
   reset the token and copy it. The bot needs no privileged intents.
2. Invite the bot with the View Channels, Connect, Mute Members and Deafen Members permissions and
   its slash commands:
   `https://discord.com/oauth2/authorize?client_id=YOUR_APP_ID&scope=bot+applications.commands&permissions=13632512`
   (Connect is for *Who's talking*; a bot invited before needs it in the voice channels, which
   most servers already give everyone.)
3. Turn on Developer Mode in Discord, right-click your server → **Copy Server ID**.
4. In the config, set `[AutoMute] Enabled = true`, `BotTokens = <token>` and `GuildId = <server id>`.

#### What the bots need (least privilege)

The bots never need Administrator. Give each bot's role no server-wide permissions and allow only
these, on the channels where they're used (channel → Edit Channel → Permissions → add the bot's role):

| Where | Allow | Why |
| --- | --- | --- |
| Each lobby voice channel | View Channel, Connect, Mute Members, Deafen Members, Send Messages | Automute; *Who's talking* (Connect); the "voice may be recorded" note in the channel's chat |
| Live status channel | View Channel, Send Messages, Embed Links, Use External Emojis | The lobby message with its colour menu and crewmate heads |
| Private results channel | View Channel, Send Messages, Embed Links, Attach Files, Read Message History, Add Reactions | Game files and standings (it reads back earlier games), referee commands |

Also in the Developer Portal: **Message Content Intent** on, for referee commands in the results channel.
Not needed: Administrator, Manage Server/Roles/Channels/Messages/Webhooks, Move Members, Kick, Ban,
Speak. The webhooks for stats and status are made by the organiser, not the bot, and slash commands
come with the `applications.commands` scope.

The simplest invite with just these (server-wide, no Administrator) is
`https://discord.com/oauth2/authorize?client_id=YOUR_APP_ID&scope=bot+applications.commands&permissions=14011456`.
For the strictest setup, invite with `permissions=0` and add the channel permissions above.

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

A death applies at once, and **Unmute all** (Home) / **F9** never wait. Muting always goes out
before unmuting: when a meeting starts the dead are muted first, and when it ends the living are
muted before the dead get their voice back, so nobody who should be quiet is still talking while
the bot works through everyone else.

#### Referee mode

Press **Referee** under Automute on Home to explain the rules: everyone in the voice channel (players and
spectators) is muted, but can still hear, except you and anyone in `RefereeUserIds`
(co-referees, casters). Pressing it again gives everyone their voice back. It only ever turns on
by hand, and it ends by itself when a game starts, so a forgotten toggle can't silence a
meeting. You need to be linked to Discord yourself to be the one talking. The live status
message shows when the referee is speaking.

#### Who's talking

While you host, the first bot sits in your voice channel, muted, and follows you when you
move. When you leave voice or stop hosting, it leaves too. It only looks at which people are
sending sound; it never records or plays anything. On your screen (and so on stream), a linked
player's meeting card lights up in their colour while they talk, with a speaker on the outline's
corner, and in the lobby a speaker in
their colour shows by their name. Players' own games don't change. Turn it off with `ShowTalking = false` under
`[AutoMute]`.

#### Spectators

With **Spectators** on (Automute on Home, or `MuteSpectators = true`), anyone in the game's voice channel who
isn't playing is muted while a game is running and unmuted in the lobby. The game's voice
channel is the one the host is in (once the host is linked), otherwise the one most linked
players are in; set `VoiceChannelId` to pin it. Put casters
and referees in `SpectatorExemptUserIds` so they're never muted. Off by default.

### Linking players to Discord

Automute only touches players linked to a Discord account. Linking happens in Discord or
in The Button, never in the game chat. Links are keyed by friend code, so a link survives name and
colour changes, and they're saved in `BepInEx/config/TournamentTracker/links.json`.

* **`/link`**: in any channel of the server, a player types `/link` and their in-game name
  or colour (`/link Red`, `/link Soggy Dingus`). Only they see the answer. A colour works
  when they're in the lobby's voice channel; a name works from anywhere. With several
  lobbies running, the lobby that has that player (or that voice channel) answers.
* **In The Button** (hosts): on Home, press **Link** under a player (or their @name to change
  it). An `@` is already in the box: type the rest of their Discord name (username or server
  nickname) and press Link. If more than one person matches, The Button lists them so you
  can type more. The Points page shows each player's @ next to their name.
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
| Debug | `FrameProfiler` | false | Log the mod's slow frames (see below) |

**Frame profiler.** To find hitches, press **F10** in the game (or set `FrameProfiler = true`
under `[Debug]`). Press **F10** again to stop. While it's on, any frame where the mod takes more
than 8 ms is written to `BepInEx/LogOutput.log`, at most once a second. Each line breaks the time
down by part (vision, checks, nameplates, the caster feed and so on) and counts the garbage
collections since the last line. Every 10 seconds a summary line gives each part's average and
worst time. Nothing shows in the game.

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
dotnet test tests/TournamentTracker.Tests          # core logic and The Button, no game needed
dotnet test tests/TournamentTracker.Broadcast.Tests # Red Alert
dotnet build src/TournamentTracker.Plugin -c Release -p:GameLibsVersion=2025.x.y
```

The plugin builds against `AmongUs.GameLibs.Steam` from the BepInEx NuGet feed, with
`BepInEx.IL2CPP.MSBuild` generating the interop assemblies at build time. By default it
uses the newest game version (currently 2026.8.18); pass `GameLibsVersion` to match your
game. BepInEx is pinned to be.735, because newer builds use an Il2CppInterop version the
interop generator can't run. On GitHub, run the
**Build** workflow by hand (Actions → Build → Run workflow) to choose the version; give it a
`release_tag` (e.g. `v0.1.1` for The Button and the mod, `broadcast-v0.1.0` for Red
Alert) to publish a release, and `delete_tags` to remove old ones.

Layout:

* `src/TournamentTracker.Core` has everything that doesn't touch the game: the stat tracker,
  scoring, leaderboard, Discord REST and webhooks, the automute planner and dispatcher, and
  the commands The Button sends. It is unit-tested.
* `src/TournamentTracker.AppCore` is The Button's engine and pages (cross-platform, tested);
  `src/TournamentTracker.App` is the Windows window around it (WebView2), built in CI.
* `src/TournamentTracker.Common` is what both apps share: the organiser's tournament link
  (the Live data messages) and self-updating.
* `src/TournamentTracker.Broadcast` is Red Alert's engine and pages (cross-platform, tested);
  `src/TournamentTracker.BroadcastApp` is its Windows window, built in CI. The feed between a
  host and Red Alert is defined in `Core/Broadcast/FeedProtocol.cs` and
  `docs/broadcast-protocol.md`.
* `src/TournamentTracker.Plugin` is the BepInEx plugin: Harmony hooks on the game, and a
  per-frame driver that reads the game phase for automute. It compiles the core in, so the
  build is a single DLL.

If a game update renames a method, only the hook for that method stops working. The rest of
the mod still loads, and the BepInEx log names the hook that failed.
