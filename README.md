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
  short delay at each change. A player killed mid-round keeps the same voice state as the
  living until the next meeting, so Discord never gives away who died or when. Voices change as
  soon as a meeting is called, and each bot sends several changes at once. It can also mute spectators. Players link themselves with
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

**Features at a glance:** each app has its own list of everything it does today:
[The Button](docs/features-the-button.md) · [the mod and Discord](docs/features-mod.md) ·
[Red Alert and its Stream Deck plugin](docs/features-red-alert.md). What is still to do is in [docs/improvements.md](docs/improvements.md).

---

## Install (host only)

The current release is **v0.1.67**, a beta.

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
installed", "Mod not installed", "Mod needs repair", "Among Us not found", "Setup code
damaged", "Discord bot refused" (the bot's token was reset: the host needs a new code or a
new token in Your Discord), "Round changes not heard" (the bot's Message Content Intent is
off, so round starts and lobby changes from the Organiser tab aren't heard) or "Live message
can't post" (Discord turned the lobby message down). Click one to go where it's fixed. A new version shows as one pill that does the next step itself: "The
Button vX is out · Update", then "The Button vX is ready · Restart" (for the mod alone, "Mod
vX installs when Among Us closes").

**The Button updates itself.** When a new version is out it downloads it and uses it from
the next start (**Restart The Button to finish** in Settings → Mod and updates). The mod in Among Us
updates itself the same way while the game is closed. Every download is checked against the
size and SHA-256 its GitHub release lists: one that doesn't match is deleted and nothing changes
("The download didn't match the release (it may be incomplete). Nothing was changed; try again
later."). Turn **Update automatically** off there to update both only when you choose.

While you play, the app's pages run the lobby:

* **Home:** a line at the top with the game's latest warnings (a player left and whether to
  void, an extra game, settings put back…; "N more" opens the rest, ✕ dismisses); a
  checklist of what the lobby needs (mod up to date, setup code, round set, Discord voice,
  everyone linked, the lobby message, the upload check, sending video, the live data), red or amber when something's
  missing; and the whole lobby on one page, up to 15 players: code, server, map, round, each player
  as their crewmate with their Discord link and whether automute has them muted or deafened;
  the automute buttons (automute on/off, referee mode, spectator muting, unmute everyone);
  start the next round; and **Tools**: the settings lock, referee ghost slot,
  host authority (see [Host authority](#referee-ghost-slot-experimental) below) and stream overlay switches, **Send my game to the caster**, and **Post**
  (**Leaderboard**, **Server standings** or a fresh **Lobby message**). While the caster's Red
  Alert has your lobby on stream, a red **ON STREAM** bar at the top of Home says how ("On stream
  (full screen)", "On stream (in the quad)"…) with "You're on stream: stay in the game window, no
  menus."; while it's in the caster's Preview, an amber **NEXT** bar. Only a host in the referee
  ghost slot (not playing) sees these bars: a playing host could learn from them that something
  happened in their game.
* **Referee:** every game waits here before Discord sees anything. Under **Waiting for you**
  each finished game shows every player's points (hover for where they came from). Type any
  amount with up to 3 significant figures (0.125, 1.25, 12.5, 125; empty means 1) in the box
  next to a player, then press **+** to add it or **−** to take it away (it shows as "Referee change"
  in their breakdown), **Void**
  keeps it out of the points and off Discord for good, and **Submit points** posts it (the
  report, the standings, the round's scores). **Submit all** sends every waiting game. The
  number on the tab says how many are waiting; they stay waiting across a restart.
  At the top, **Void this game** (with an optional reason) voids the game running now, or the
  last one, **Unvoid** brings it back, and **Reset points…**: type RESET, then **Reset all
  points**, to put every game on this PC away (moved to a dated
  "reset" folder in the games folder, never deleted) and start the totals and round counts
  from zero. Discord isn't touched. Below: every recent game with **Void** / **Unvoid**
  (Discord is told, since it was posted), the point totals (players never see them in the game
  or through the bot: this lobby's round with the cut line and running totals, every lobby's
  round with a results channel, from round 2 the running total; with a preliminary code the
  lobby's leaderboard), and the folded **Results channel** card for whoever has the
  administration code (change the points of any lobby's game, void or unvoid one). Only the
  setup code can turn the hold off (**Hold each game until the host presses Submit**); games
  then post as soon as they end.
* **Settings:** the game folder; the mod and The Button's updates; the setup code as one line (details, change or
  remove it); **Your Discord** (below); **Your connection** (the upload check, what to share, lower quality);
  **Your Twitch**; in a tournament with a results channel, **Tournament** (**Take over** as the lead lobby, and the
  combined leaderboard's **Reset…**); with the administration code, **Administration**; **Help** (the support
  bundle, and the backup of the mod's data: **Restore backup**, **Backup folder**); files under **Advanced**.

**Your Discord** (Settings): the host's own bots and channels, so the organiser doesn't need
anyone's bot token and a host can change bots without a new code. Up to 3 bot tokens, the
server ID (a tournament code's server is used if it's left empty), a **public report
channel** webhook and a live lobby channel webhook. **Save and check** asks Discord about each
one: a token that's refused, a bot that isn't in the server, or a webhook that doesn't exist,
is not saved (and says why); the rest is. Saved bots go online whenever The Button is open (with a setup code in), and
are used instead of any bot in the code. Tokens stay on the PC (the screen shows only each
bot's name and the token's last 4 characters). With a preliminary code, the public channel
gets only each game's results and timeline (no standings, points breakdown or data file); the organiser's
private channel from the code still gets everything. With a tournament code, the public
channel is used only when the code has no results channel webhook.

When a lobby plays its last game of the round, its bot posts the **round's scores** in the
private results channel (staff only, so lobbies without a bot don't post it): every player's
points for the round, in order, and under each one where the points came from, added up over
the round's games ("Kill +3 · Task win +5 · Lost -2…"; a referee's adjustment shows as its
own item). It doesn't say who moves on.

The app talks to the mod over a private connection on your computer only (port 8766, with a
random key the mod writes in its data folder).

**Something went wrong?** **Save support bundle** (Settings → Help) in The Button makes one zip on the Desktop with
what's needed to work out why (its logs and settings, with setup codes, bot tokens, webhooks and
passwords taken out), to send as it is with a line on what happened and when. **Before an event**,
run the [dry run](docs/dry-run.md) with every host and the caster: a 30-minute rehearsal (the
upload check, sending your game, lobby voice, player cameras, replays and a failure drill) to tick
off together.

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
* **Private results channel:** staff only. The hosts' mods post each game's data here, and
  the referees' and your changes made in The Button (points, voids, rounds, a reset) are posted
  here by the administration bot. Anything a person types here is ignored.

### 2. Bots (tournament hosts only)

Create one bot per tournament lobby that runs at the same time, at
<https://discord.com/developers/applications> (New Application → Bot → Reset Token). For
each: turn on **Message Content Intent** (Bot → Privileged Gateway Intents), and invite it
with this link (put in the application ID):
`https://discord.com/oauth2/authorize?client_id=APP_ID&scope=bot+applications.commands&permissions=14011456`
(view channels, send messages, embed links, attach files, read history, add reactions, use
external emojis, connect, mute and deafen members, and the `/link`, `/unlink` and `/new` commands,
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
  * **Red Alert channel (optional):** your private results channel's ID (the one in your
    administration code). With a bot (the code's, or the host's own from The Button), the lobby
    shows live in Red Alert and the host can send their game to the caster, exactly like a
    tournament lobby; reports still go to your private reports channel as usual. For Red Alert's
    standings too, give the administration code the same Tournament ID as the preliminary code.
  * **Automute in the code (optional, older way):** a server ID and a bot's token (and a
    live lobby channel webhook). A code with a bot token in it must be sent privately.
* **Tournament host code:** the tournament name, the results channel webhook, and
  optionally your server ID, up to 3 bot tokens (or none: with the server ID and no tokens,
  the host adds their own bots in The Button), the private results channel, a webhook for
  its own live lobby channel (otherwise the lobby message goes in the results channel, or
  where the host types `/new`), the preliminary channels and the referees' user IDs. The bot is optional: without one, games are still tracked, scored
  and reported, and the host has the whole app, but there's no automute, no `/link`, `/new`
  or colour menu, no combined standings and no changes from the Organiser tab or the Results
  channel card. 2 or 3 bots mute a full lobby faster (Discord limits each bot's speed; the work
  is shared). A code with a bot
  token in it must be sent privately.

* **Administration code** (for you, referees and casters): the tournament name, the private
  results channel and one bot token. Paste it into The Button's Settings → **Setup
  code** box like any code: The Button recognises it, and the **Administration** card and
  **Organiser** tab appear (nobody without the code sees either) (see *Organiser tab* below). It has a bot token in it, so send it privately.

Both carry the point values, so every host scores the same, and the game settings (below).
Tournament host codes also carry the games per round and, for one host only (you), the
**lead lobby** tick that makes that host's mod carry out your round changes from the Organiser
tab. Codes are only encoded, not encrypted: anyone holding one can read what's in it. The Button keeps
its own copy of the host's code, so reinstalling the mod or Among Us doesn't lose it: it's
put back by itself.

* **Settings lock** (on by default in the generator): while a preliminary or tournament code
  is in use, the host's lobby is kept on the tournament's settings (impostors, cooldowns,
  vision, kill distance, tasks, the task bar (in meetings only by default; the referee still sees the live bar), ghosts doing tasks, and the roles). **Roles…**
  sets each role (Engineer, Scientist, Guardian Angel, Noisemaker, Tracker, Detective, Judge,
  Spirit Guide, Shapeshifter, Phantom, Viper) to a number per game and a chance; every role
  left at 0 is off. Each role's options (cooldowns, durations, "leave evidence"…) are held too
  when filled in; an empty one keeps the host's own value. Task counts stop at the game's own
  menu limits (common 4, long 15, short 23). Anything changed in the lobby is put
  back and the host is told, once, in their own chat only (not again on every join, until the
  next game). A game that still starts on the wrong settings says so in its
  report (and to the referees). For a casual game the host turns **Settings lock** off on
  The Button's Home page, under Tools (until they restart Among Us); without a code nothing is ever touched.
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
| After each game | Report, the game's data, the lobby's standings and its count in your private channel; only the game's results (who won, how, each player's role, result and total) and timeline in the server's public channel (set in The Button); a summary in the host's chat | Report in the results channel; the lobby's round standings |
| Leaderboard | The scheduled job's combined board per preliminary | Per lobby per round, with the cut line, plus a running total |
| Automute | Optional: the host adds bots in The Button (otherwise players can use AutoMuteUs) | Yes, with the code's bots or the host's own |
| Live status, referee tools | No (the live status only with a bot and a live lobby channel) | Yes |

### During the tournament

Hosts do all of this from The Button (Home, the Referee tab and Settings → Tournament);
referees and the organiser use The Button with the administration code (the Referee tab's
**Results channel** card and the **Organiser** tab), which posts their changes in the private
results channel as the administration bot. The mods act only on what a bot posted there: a
command typed in Discord by a person does nothing.

* **Rounds:** the host presses **Next round** (or sets a number) on Home before each round's
  first game, for as many rounds as you need, or the organiser starts a round for every lobby at
  once (Organiser tab → Rounds, below). Points restart each round, and a running total across all
  rounds is kept alongside. After every game the lobby's standings for the round are
  posted with a line under the top players who move on (5 by default). **Post →
  Leaderboard** (Home → Tools) also shows every lobby in the round together.
* **Referee changes:** the host changes a player's points with **+** / **−** in the Referee tab
  (see above). A referee with the administration code changes any lobby's game in the Referee
  tab's **Results channel** card: the lobby, the game's number (3 for LJ-3; empty means the game
  that lobby is playing, or played last), the player's colour or name, the points, **−** or
  **+**, and a reason. The Button posts it (`!adjust LJ-3 red -2 meta call`) and that lobby's game
  applies it; its bot reacts ✅ when it's applied, or ❓ if the player couldn't be found. It
  shows in the points breakdown as "Referee: meta call −2" (without a reason, "Referee change").
* **Restarted games:** if a game has to stop and restart, the host presses **Void this game** on
  the Referee tab during it (or straight after it, for the last game); a referee voids any
  lobby's game from the Results channel card (`LJ-3`, or `LJ` for the game that lobby is
  playing, with a reason). A void game is kept for the record and its report says VOID, but it
  scores nothing and doesn't count toward the round, so the replacement game is the one that
  counts. Only **Unvoid** (on the Referee tab, or in the Results channel card) brings it back:
  once the lobby's bot has seen a void it reacts ✅ and reposts the game with it built in, so
  deleting the void message changes nothing. In a preliminary the host voids or changes a game
  in their Referee tab: the game's data file is posted again, and the scheduled job goes by the
  newest copy.
* **Round progress and ties:** standings say which game of the round they're after
  ("after game 2 of 3"), the all-lobbies view shows how far each lobby has got, and a host
  starting a 4th game in a round is warned. When a lobby has played all its games, a tie
  across the cut line is broken by impostor wins, then vote %, then task % (over the
  round). The players it moves above the line get +0.25 on their total; it isn't shown in
  any public breakdown, but the referees get a note in the results channel saying who,
  why and by how much. A tie that's level on all three is left to a referee (give one of them
  +0.25 in the Referee tab).
* **Disconnects:** when a player leaves mid-game the host is told, and pointed to **Void this
  game** (Referee tab) if it's before the first meeting; the referees get a note. A player who leaves keeps
  the points they'd earned and takes their team's loss (and the sabotage penalty if they
  left alive), but doesn't share a win.
* **Automute on or off:** the host can switch automute off on Home (the Automute button);
  it stays off, even after restarting, until they switch it on again. While it's off the bot
  never touches anyone's voice, so AutoMuteUs can be used instead; linking, the live
  message and the results channel still work.
* **Next round's lobbies and round starts** (Organiser tab → **Rounds**, with the administration
  code; posted in the private results channel and carried out by the lead lobby's mod):
  * **Build its lobbies** for round 3 puts everyone who moved on from round 2 into lobbies of up
    to 10, snake-seeded by their round-2 points (1st, 4th, 5th, 8th… in lobby A when there are 2
    lobbies), and lists the best of the rest as alternates. 10 or fewer make the Final.
  * **Change the lobbies:** a player and a lobby (A, B… Final) with **Move there**, **Add there**
    (an alternate or anyone) or **Drop** (shows the next alternates); two players and **Swap**;
    a lobby, its host's lobby label and **Set host**. Each change reposts the list.
  * **Start round** (round 3, starts in 10 min; it asks for a second click) switches every
    lobby's mod to round 3 (after the current game if one is running) and pings each lobby's
    linked players in the tournament channel with their lobby and host, then again when it
    starts. With the minutes left empty it says it's starting now. Each lobby's own game then
    posts where to go: "**Round 3 · LJ's lobby:** join voice #lobby-1 · lobby code `QWERTY` ·
    Polus" (its voice channel is the one its host is in).
  * **Take over** (Settings → Tournament → Lead lobby) makes that host's mod the one that
    carries them out from then on.
  This needs the bot's Message Content Intent (already needed for the results channel).
* **Server standings:** **Post → Server standings** (Home → Tools; and by itself each new round)
  posts servers ranked by their players' total points, with each server's furthest player. A player's server is the one
  they played the most preliminaries in. Unofficial.

### Stream overlay

**Stream overlay** (Home → Tools) starts an overlay for OBS on the host's computer: add a
**Browser source** with `http://localhost:8765/` (size 360×720). With more than 8 players
the list goes into two columns, so a full 15-player lobby still fits. It shows the lobby,
round and game number, the players (as the same crewmate heads The Button uses), the round standings with the cut line, and the latest meetings and
ejections. It only shows what the players in the game already know (a death appears once a
meeting reveals it). `http://localhost:8765/?full=1` adds roles, kills and task bars: it
shows who the impostors are as it happens, so anyone watching a stream with it could see them. Add
`&show=players` (or `standings`, `feed`, comma separated) to show some panels only, for
example the players on one side of the screen and the standings on the other. Standings
and the feed stay hidden until there's something in them. Turning the switch off stops it;
the choice is remembered.

### Organiser tab (administration code)

With an administration code pasted into Settings → Setup code, The Button gets an
**Organiser** tab. The code is kept in The Button's own settings, so it works on a PC without
Among Us (a host's code still needs Among Us found first: it goes in the mod's folder). There,
Home says **Organiser PC** ("Among Us isn't needed here…") with **Open the Organiser tab**, and
the top bar asks for no game, mod or setup code. Every tournament host's game with a bot keeps
one "Live data" message in the private results channel, updated a few seconds after anything
changes; the Organiser tab reads them all:

* **Every lobby live:** phase, map, lobby code, who's alive or dead, and the round's games.
  Tick **Show roles and real deaths** (staff only) to see impostors, real deaths, kills,
  sabotages and players leaving too. A lobby that hasn't sent
  anything for a couple of minutes is shown as gone quiet.
* **Standings:** each lobby's round standings with the cut line, and all lobbies combined.
  Until the new round's first game, the last round's final standings stay up.
* **Awards:** candidates for this round or the whole tournament, the top three in each of
  top score, most kills, best impostor, sharpest voter, impostor hunter, task machine and
  survivor. **Copy for Discord** copies them; you pick the winners.
* **Caster tools** (the caster overlay, game video, the Caster tab, OBS, replays) are in
  their own app now, **Red Alert** (below), for whoever runs the stream. The Organiser tab
  links to it.
* **Rounds:** build the next round's lobbies (**Build its lobbies**), change them (**Move
  there**, **Add there**, **Drop**, **Swap**, **Set host**), then **Start round**, now or in a
  few minutes; see *Next round's lobbies and round starts* above. **Start over…** (after a
  confirm) starts every lobby's combined standings over; deleting the reset message in the
  results channel undoes it.
* **Results channel** (a card on the **Referee** tab, for whoever has the administration code;
  folded, except on an organiser's PC): change the points of any lobby's game, or void or
  unvoid one (see *Referee changes* and *Restarted games* above). It's posted in the results
  channel as the administration bot, and that lobby's game carries it out. There's no box to
  type a command.

The administration bot needs View Channel, Read Message History and Send Messages in the
private results channel, and its **Message Content Intent** turned on (Discord Developer
Portal → Bot). Hosts can turn the live data off with `PublishLive = false` (under `[General]`
in the config).

### Replays

Every game is recorded: each player's position about ten times a second (and whether
they're dead or in a vent), the map's walls, rooms and vents read from the game, and the
game's events. The file (`tt-replay-LJ-3-….json.gz`, about 0.5 MB) is saved with the game on
this PC only (the newest 20 are kept) and never posted to Discord: on stream, replays are Red
Alert's, cut from the game's real picture. Open one in `docs/replay-viewer.html` if you want:
play, pause, ±5 s, scrub, 0.5–8× speed, jump to any kill or meeting, follow a player, with
bodies and vents drawn and switches for roles, ghosts, trails and wall outlines (**Watch a
sample** shows how it works). `RecordReplays = false` in the config turns recording off.

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
During play a small list down the left edge of the referee's screen shows the players, who is dead
or gone, and a red IMP by each impostor. The host keeps the referee's player record dead (re-sent after the intro, before
each meeting and every 5 seconds; never an exile, kill or role message), and counts the votes, so a
vote for the referee counts as a skip and the referee can't be voted out. (An earlier experimental switch sent the players an exile message
for the referee; it got the host banned from their own room and was removed.)

**Host authority** (on by default) is what makes that record reach players without the mod. The
mod tells Among Us's servers it's a host-only mod (Innersloth's "+25" flag for host-only mods, the
one Town of Host and similar mods use), so the host, not the server, keeps every player's record.
Once it's active, players without the mod never see the referee on the map and see their card
crossed out (dead) in meetings; without it they see the referee alive. Modded lobbies are kept out
of the public matchmaking list, but lobby codes work as usual. The game sends this as it connects,
so turning it off or on again (The Button sends `hostauth off` / `hostauth on`) applies from the
main menu, to the next lobby you host. If the referee becomes a ghost in a lobby without it, Home
says so. With host authority the host's game also turns down exile, kill, meeting, role, name,
shield and shapeshift messages that come from another player's game (only the host sends those):
it's logged and shown on Home, and nobody is kicked. **Unequip your pet** when you referee: a dead
player's pet stays visible to the living players.

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
itself only plays it for the killer and the victim), so the host and the stream hear it,
whatever is on stream (the player camera included). Their task bar is smaller
and always shows the real progress, whatever the lobby's task bar setting. The host turns down any kill on the
referee. In meetings the referee's card is taken off the list. That happens on the host's screen and on every player's game that has the
mod; there the referee isn't drawn on the map either. A game without the mod lists them as a dead
(crossed-out) player once host authority is active.

## Red Alert (the broadcaster's app)

<img src="docs/red-alert-logo.svg" alt="" width="56" align="left">

**Red Alert** is a separate Windows app for whoever runs the stream: the Live desk, OBS
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
* **The side menu** has a page per job: **Live desk** (everything for the show on one screen, laid
  out like vMix: a status bar, then **Preview**, **Take** and **Program** with the action panel
  beside them, a tile per lobby, and three columns at the bottom that scroll on their own:
  Suggestions, the player cams, and the graphics queue, talking points and the Plays drawer), **Lobby
  health**, **Montages**, **Graphics**, **Standings**, **Sponsors**, **Twitch**, **OBS**, **Lobby
  voice**, **Players** and **Settings** (caster overlay, administration code, player camera, Team
  watch, Stream Deck, updates, help). On the Live desk the
  menu is a narrow rail of short names. A dot beside an item shows what needs a look; the number on
  Live desk is new suggestions from your team since you last looked there (it clears when you open
  it). Keys **G**, **I**, **U**, **B**, **C**, **M**, **V**, **E** and the replay keys work on every
  page; **1–9**, **Shift**+**1–9**, **Enter**, **Esc**, **N**, **H** and **P P** only on the Live
  desk (**?** in its status bar lists them). Each card's explanation is behind its small **?**.
* **One screen:** Red Alert opens maximised the first time, then where and as big as you left it.
  The Live desk fits one screen from about 1000 × 620 up (a smaller window becomes one long page).
* **Restarts and crashes:** if Red Alert's page crashes or hangs it reloads by itself (up to 3
  times a minute) without auto switch reacting; after a restart, Hold, the automation switches and
  the lobbies are back at once (a lobby's tile says **CACHED** until the results channel confirms it).
* **The status bar** says what OBS is showing right now: PROGRAM (red), BREAK (amber), REPLAY or
  OFF AIR (EMPTY when a layout has no lobby in it, SWITCHING while it waits for OBS), the
  layout and lobbies, whether OBS is streaming and recording, whose lobby voice is heard (click it to
  mute every lobby), OBS, the lobbies' health, replays ready, AUTO or HOLD, and a shot timer that
  starts again on every switch. It follows OBS: switches made in OBS by hand show too, and a switch
  OBS doesn't make within a couple of seconds is a red error. Beside it, the worst warning (click
  for the rest, each with its **Fix** or **Undo**): OBS not connected, the stream stopped or
  reconnecting, not recording, dropped frames, low disk space, a lobby on stream missing in OBS.
* **Preview, Take, Program:** clicking a lobby (its tile, a card, keys **1-9**) puts it in
  **Preview** (green); **Ctrl**+click adds another (two side by side, three or four in the quad, five
  and more in the grid). **Take** (or Enter) puts Preview on stream, in **Program** (red); Esc clears
  Preview. **Full**, **2-Up**, **Quad**, **Grid** and **Cams** beside Take choose Preview's layout
  (with nothing in Preview, what's on stream in that layout). To switch at once instead:
  **Shift**+**1-9** or a double-click. The Program box shows OBS's own picture, about twice a
  second (a drawing of the layout without OBS). **Back** undoes the last switch.
* **Cut, Swoosh or Fade:** the choice beside Take is how every switch from the desk goes on (Take,
  cuts, camera cuts), and is remembered. Swoosh is the stinger described under
  [Swoosh](#caster-tab-redzone-broadcast) below.
* **Breaks** (Starting soon, Be right back, Intermission, Sponsor, in amber) sit in the action
  panel, apart from the layouts, so a break isn't clicked by mistake; they go on at once.
  **Starting soon** (the **TT Starting Soon** scene) counts down to the start (minutes or a time
  on its Live desk card, with a line of your own), with the tournament's logo and sponsors; at
  zero it says "Starting now" and stays, and nothing switches by itself, until you Take a lobby.
* **Hold** (**H**, or the AUTO pill): nothing switches by itself until you release it (a camera
  going back to the map, auto switch off a dropped lobby, a sponsor break ending, a montage going
  back to intermission, the grid closing up after a drop, armed cameras), and auto grid puts
  nothing in Preview. What it held back shows as a toast and a
  warning. Your own switches still go through.
* **Panic** (**P** twice, or the Panic button): something must come off stream now. It fades to
  Be right back, mutes every lobby voice, clears the graphics, pauses alerts and holds switching.
  While it's on, Panic says **Undo panic** and **Back** (or the Stream Deck's Undo) puts it all
  back: what was on, the voice, the alerts and your own hold if you had one. Put right by hand
  instead (another lobby, the hold released, the voice back on), it's over: a BRB, hold or mute
  you set later is yours, and Undo undoes your last switch. Panic's switch goes on at once.
* **Replay that** saves the last seconds of the lobby on stream and plays them as soon as they're
  saved, no card needed; **Play last** plays the newest replay again. Ready replays show as buttons
  beside them and as a badge on their lobby's tile; the replay controls (with slow motion 100 / 50 /
  25%) take over the action panel while one plays. **Best replay** (key **N**, or the Stream Deck)
  puts the best recent replay into Preview (the highest rated one from the last 3 minutes not
  played yet); Take plays it, and pressing again offers the next one.
* **The status bar** says **EMPTY** (not PROGRAM) when a layout has no lobby in it, and the Live desk warns
  when a lobby on stream isn't in OBS ("LJ isn't in OBS: …"), with **Fix** (it rebuilds the TT
  scenes). No button fails silently: anything that goes wrong says why.
* **OBS that keeps a removed source:** OBS can keep a removed source alive in the background,
  still listed but refusing to go in a scene. Red Alert then makes a fresh one under a new name
  ("TT Lobby LJ 2") and carries on; a source that can't be made is tried again after 30 s or on
  Rebuild, never in a loop. No need to restart OBS.
* **Lobby numbers:** each lobby gets a number when it first connects (the number at the end of its
  name when that's free, else the lowest free one) and keeps it across restarts, in
  `lobby-numbers.json`, for 18 hours after it was last seen. The tiles, the cards, keys **1-9** and
  the Stream Deck's lobby keys use it; how exciting a lobby is shows as a badge and a glow, never by
  moving it. In 2-up, Quad and Grid the numbers on stream are where each lobby is on screen (1 top
  left). Settings → Stream Deck → **Lobby numbers** changes one (a lobby that had the number takes
  the old one) or, for a new day or round, **Renumber online lobbies 1…N**.
* **Graphics queue:** one big graphic on stream at a time (a lobby's table after a game, a
  player card, a storyline note, the standings), each for 8 s (change it on the Live desk), then
  the next. One about a lobby goes on that lobby: full screen, inside its tile in a multi-view
  when there's room, or in intermission; otherwise it waits. Off-screen alerts are one at a
  time. The Live desk shows what's on and what's next, with **Skip** and **Clear**.
* **After a game** the Live desk asks "MAL finished · Show the table?": **Show**, **Skip** or
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
  **Check now**); **Update** downloads it (checked against the size and SHA-256 its release
  lists: one that doesn't match is refused and nothing changes) and **Restart Red Alert** starts
  it. Hosts never need to reinstall anything for a broadcast change.
* **Player camera:** a second picture from each host's game that follows one player up close,
  960×540 at up to 30 frames a second, sent with the host's game. It's on by default
  (Settings → **Player cameras** turns it off). The Live desk's **Player cams** list has every
  live lobby's players: click one to put that lobby's camera on stream following them, or
  **Auto** to let it pick (an impostor closing in on someone alone, else whoever is busy; a pick
  is kept a few seconds after they die, to see it); **Shift**+click only changes who it follows.
  In a meeting or between games the press is **ARMED**: the camera goes on stream by itself when
  play resumes (pressed again: taken back). It waits out a replay, Be right back, intermission and a
  sponsor break, and a switch you make by hand to another lobby or a break cancels it (a toast says
  so). To show several at once, put the lobbies in Preview, press **Cams** and **Take**: two side
  by side, three or four in a quad (clicking a player then only changes who that lobby's camera
  follows). At a meeting or when the game ends the stream goes
  back to the whole map by itself (unless switching is held). The camera darkens what the followed player can't see, as on
  their own screen, and the referee sees task animations even with visual tasks off for the
  players (when their game sends them). Red Alert adds a **TT Player Cam** scene to OBS: one
  camera full screen with the lobby's whole map small in the bottom right, or two cameras side by
  side, three or four in a quad. Switching to it, back, and from player to player is a straight cut (no
  swoosh). How it gets to you: the host's mod draws it off screen (the host never sees it and
  can't change it) and hands each picture to The Button, whose *Send my game to the caster* page
  sends it as its own VDO.Ninja stream next to the game and the lobby voice; OBS's TT Cam source
  shows that stream. It costs each host about one more video stream of upload, and only while
  they send their game.
* **Player camera sound:** while a lobby's player camera is on stream, its host's game sound is
  what the followed player would hear: sounds are measured from where they stand (room ambience,
  vents, doors) and their own footsteps play; every kill is still heard (the referee's game plays
  them all). With the whole map on stream it's back to the referee's: alarms and every kill. Red Alert tells each host which view is on stream, by itself; their task sounds stay on
  their own PC.
* **Player camera replays:** the camera keeps its last 30 s in OBS like the lobbies, so every
  replay saved while it was on has a second angle. A kill the camera was following opens on the
  close-up; **🎥 Cam** / **🗺 Map** in the replay controls (key **P**) switches angle at
  the same moment.
* **Stream Deck:** Red Alert has its own Stream Deck plugin, **RedAlert.streamDeckPlugin** (with
  each Red Alert release): live keys that light red for Program and green for Preview, show each
  lobby's phase and sabotage, each player's crewmate and name for the player cameras, and whether
  toggles are on, plus Take, Undo, Panic, replays, breaks and dials for the Stream Deck +. New
  Lobby and Player cam keys select into Preview and Take puts them on stream (keys you set up
  before keep cutting; one whose settings were never opened now selects); the Best replay key
  puts the best recent replay in Preview. It never
  shows who the impostors are. Settings → Stream Deck has the port and key to pair it, a **link
  builder** (pick what a key does: a lobby, a player's camera by slot or colour, cut, into Preview or
  arm, the transition…) for Companion or the Stream Deck's **Website** action (tick **GET request in
  background**), every ready-made link, and the lobby numbers. **New key** makes new links. See
  [docs/stream-deck.md](docs/stream-deck.md) for install, pairing, every action and the links.
* **OBS setup check** (OBS page, when OBS connects): the scene collection and profile names
  (steps to rename them), a leftover empty "Scene" (**Delete it**), Desktop Audio reaching the
  stream (**Mute Desktop Audio**; the Live desk warns while it's on), what replays need (the
  Source Record plugin; OBS's own Replay Buffer isn't used and can stay off), and OBS's
  monitoring device for Hear Preview (warns when it is Disabled; Default gets a note to check it
  is your headphones).
* **No broadcast delay:** it's off in this version (it may come back later). Nothing is held
  back, whatever `obs.json` says: the lobbies and the graphics reach the stream live, and the OBS
  page has no delay setting. OBS's own stream delay isn't used either, so impostors show on stream
  as it happens.
* **Ports:** its screen and the Stream Deck links are on `127.0.0.1:8768` (this PC only, with a
  private key), or 8769, 8770 or 8771 when 8768 is taken (the Stream Deck plugin finds it there by
  itself; Settings → Stream Deck warns that copied links need copying again). The OBS pages are on
  `localhost:8767`, as before. The mod uses 8765 and 8766 on the host's PC. The Watch page for the
  production team is on 8780 (or the next free one up to 8789), only while Team watch is on, and is
  the one port other PCs on the network can reach, behind the watch code (see
  [Team watch](#team-watch)).
* **Support bundle and dry run (Settings → Help):** **Save support bundle** puts Red Alert's log
  (and the one before it), its settings, OBS's newest log and crash report, the versions, the Live
  desk's warnings and what the desk, OBS, the lobby voice and the lobbies' health were doing into one
  zip on the Desktop (`RedAlert-support-<date>-<time>.zip`; the lock screen has the button too). The
  administration code, bot tokens, webhooks, OBS and VDO.Ninja passwords, stream keys, the Stream
  Deck key, the watch code and Twitch sign-ins are taken out, so it can be sent as it is (the kept
  lobbies, `lobbies-cache.json`, never go in). **Dry-run checklist** opens
  [docs/dry-run.md](docs/dry-run.md): a 30-minute rehearsal with the real hosts, OBS and the Stream
  Deck, with a failure drill and what to send when something fails.
* **Files** in `%LOCALAPPDATA%\RedAlert`: `settings.json`, `obs.json`, `caster-priority.json`,
  `lobby-numbers.json`, `roster.csv`, `broadcast.json`, `sponsors.json`, `health.json`,
  `lobbies-cache.json` (the lobbies last read, deleted on Lock), the kept games, and
  `red-alert.log`: what Red Alert did and anything that went wrong (an error in the window is
  written there and Red Alert carries on; past 1 MB it moves to `red-alert.log.1`).
* **Older hosts:** hosts send their game data in a versioned format
  ([docs/broadcast-protocol.md](docs/broadcast-protocol.md)). A host whose mod is older than
  Red Alert still shows, with **host needs update** in Lobby health.

### Caster overlay and game video

* **Best replay** (or **N**): puts the best recent replay into Preview; Take plays it. A lobby
  whose host has gone silent for a couple of minutes gets a warning at the top.
* **Caster overlay:** an OBS Browser source at `http://localhost:8767/` (360×720) that follows what's on
  stream (the full-screen lobby, or slot 1 of a multi-view), as do the video and multiview
  pages; **Pin** (Settings → Caster overlay) keeps them on one lobby (shown as PINNED, with **Follow the stream**). It shows
  players, round standings and the latest meetings and ejections for that game.
  `http://localhost:8767/?full=1` adds roles, kills and task bars (it shows impostors as it
  happens).
* **Game video, RedZone style:** each host turns on **Send my game to the caster** (Home →
  Tools) in their Button. It opens a private VDO.Ninja page in their browser; they share **the Among
  Us window** (the default; The Button sends the game sound and lobby voice), or **Entire screen**
  with **Share system audio** when lobby voice is off (a window share has no sound on Windows), and
  leave the tab open. The link is random, kept for the tournament, and only
  written in the private results channel. In your OBS:
  * `http://localhost:8767/video` (1920×1080): the lobby you're casting, full frame, with its
    game sound. Every lobby's picture stays connected in the background, so picking a lobby
    switches instantly; the sound follows a moment later and only the lobby on air is ever
    heard. Put the caster overlay on top of it. `?sound=0` for picture only.
  * `http://localhost:8767/multiview` (1920×1080): every lobby at once, with its name,
    phase and players alive, the one on air outlined, and a red mark on a lobby with a
    meeting or an ejection happening. Silent (the video page carries the sound);
    `?sound=1` plays the lobby on air if you use the multiview on its own.
  * The Live desk's **Suggestions** shows what your team suggests from the Watch page, so you know
    where to jump (see [Team watch](#team-watch)).
  * Each host needs about 10 Mbps upload (4-6 for the picture, 2-3 more for the player camera;
    **Check my connection** in The Button measures it), and you download every lobby at once, so
    use a wired connection.
  * Nothing holds the stream back: no OBS stream delay and no Red Alert broadcast delay.
    Impostors show on stream as it happens.

### Caster tab (RedZone broadcast)

Red Alert's main screen is the **Live desk**: every lobby's live play,
ranked, and one click to put a lobby on stream. It shows who the impostors are, so it stays on
the caster's PC and off stream.

* **Where the data comes from:** each tournament host plays as the referee ghost and turns on
  *Send my game to the caster*. Their Button opens its own page with VDO.Ninja inside: it shares
  the screen and sends the mod's live data (kills with room and position, meetings, ejections,
  sabotages with time left, vents, task bar, kill-ready, danger, a snapshot every second) along
  the same private stream. Red Alert joins each stream for its data. A lobby that
  stops sending shows as offline; the rest keep going.
* **Suggestions:** the desk's own items first (lobby down or back, interrupted games, chat's
  pick), then the team's suggestions from the Watch page, newest first (**Into Preview**, **Cut**,
  **×**). A suggestion clears when its lobby goes on stream, after 2 minutes or when dismissed,
  and moves to *Earlier*.
* **Plays:** Red Alert still ranks every play highest priority first (must show, very high, high,
  medium), with the lobby, the play ("Reactor 12s, nobody fixing", "Purple killed Lime in
  Electrical") and how it's on stream (LIVE (full), LIVE (quad, slot 2)). The plays are on the
  Watch page and in the Live desk's **Plays** drawer ("N now · M earlier"): plays going on now
  with **Save replay** / **▶ REPLAY**, **Preview** and **Card**; calmed-down ones with **Watch
  again**.
* **Ranking:** every weight and limit is in `caster-priority.json` in Red Alert's
  folder (written the first time, re-read when you save it).
* **OBS:** in OBS, Tools → WebSocket Server Settings → Enable WebSocket server, set a password,
  and connect from Red Alert's OBS page. Red Alert builds the scenes **TT Full**, **TT 2-up**,
  **TT Quad**, **TT Grid**, **TT Player Cam**, **TT Sponsor Break**, **TT Intermission**, **TT Be
  Right Back**, **TT Starting Soon** and **TT Replay** with one source per lobby (each stays connected, so switching is instant), places
  the lobbies in their slots, plays only slot 1's game sound, and switches scene when you click.
  It never touches your other scenes. Switch in OBS yourself and the status bar and LIVE labels follow.
  Settings and the lobby → source mapping are in `obs.json` in Red Alert's folder.
* **Replays** (needs the free **Source Record** plugin for OBS, by Exeldro): Red Alert puts a
  Source Record replay buffer (last 30 s) on every lobby source. Every kill saves a clip by itself
  (8 s before to 2 s after); any play in the **Plays** drawer can save one with **Save replay**,
  while OBS still has it. **▶ REPLAY** on the play (or **Watch again**) plays it in the **TT
  Replay** scene: a Media Source, so you can pause and scrub, cropped and zoomed (up to 2.5×) to keep the killer and
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
  vent in MedBay" card. Switch each one per lobby from its tile's right-click menu on the Live
  desk (it goes to the host over their VDO.Ninja link), or with keys for the lobby on stream (M lit, V vision,
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
    standings changes, storyline notes, off-screen alerts and player cards.
  * **Hide room code** (off by default: hosts stream with Among Us's streamer mode on): a "Room
    code hidden" box over the room code while a lobby on stream is in its lobby or menu, so
    viewers can't join. Where it goes is `roomCodeBox` in `broadcast.json` (shares of the game
    picture).
  * Graphics slide to their new places when the layout changes and fade in and out, so a switch
    is smooth under the swoosh.
* **Grid:** every active lobby at once in **TT Grid** (1 full, 2 side by side, up to 4×4, then five wide), each
  tile with its label, a status line, the impostor tag and a border that pulses at high
  priority; empty tiles show a sponsor or the logo. Tiles and their status lines stop above the
  stats ticker while it shows. Click a tile (on stream) for full screen. Once it's on, the grid
  only changes by itself when a lobby on it drops (see Lobby drops below): a lobby joining or
  going to its menu waits until you change the grid.
  **Auto grid** puts it into Preview whenever no lobby is mid-game (Take shows it).
* **Multiview:** the lobby tiles on the Live desk are a small live picture of every lobby sending
  its game. Click one for Preview, Ctrl+click to add more (they're numbered in the order you pick),
  then **Take**: one goes full screen, two side by side, three or four in the quad, five and more
  in the grid. Nothing changes on stream until Take (Shift+1-9 or a double-click cut at once).
* **Stats and storylines:** every game is kept on the caster's PC (`broadcast-games`), and the
  **Standings & storylines** card lists talking points from them (records, streaks, first
  blood, rivalries, who keeps getting voted out, milestones), the ones about players on stream
  first: pin, dismiss, mark used, or put one on stream as a lower third.
* **Standings** come from the tournament's own scoring (the same tables as The Button's Referee
  tab and Discord: `Stats/Scoring.cs` and `Stats/Standings.cs`, rules from the setup code). On the
  **Standings** page: **Lobby on stream** (the default: the lobby full screen, or slot 1 of a
  multi-view, in its own round), **Round · every lobby** (that round's totals, all lobbies) or
  **Tournament**. Lobbies can be in different rounds (a big tournament starts round 2 in one
  lobby while another finishes round 1): each lobby's table and each round's totals only count
  their own round's games, and before a lobby's first game of a new round its last round's final
  table stays up. Near the end of a game, *points on the line* shows what each ending does to
  the lobby's table; after each game, arrows show who moved.
* **Montages:** clips are saved by themselves for kills, ejections, meetings, witnessed kills and
  game ends. When a game ends Red Alert builds a montage of it with ffmpeg, about 45 s long (each moment
  cut to a few seconds, cropped like replays, a lower third such as "Jake → Maria,
  Electrical", wipes between them); when a round ends, one of about 2 minutes: "every kill" then
  the top plays counting down. The **Montages** card shows them ready with Play (in the replay scene), Preview and
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
  lobby on the left and the sponsor on the right). **A sponsor break can't be interrupted:** it
  runs its time, then goes full screen on its lobby. A lobby you pick meanwhile (a cut, Take, a
  Stream Deck key, a player camera) goes into the break's picture and the break carries on.
  Replays, montages, Be right back, Intermission, Starting soon and another break are refused
  ("A sponsor break is on: 18 s left. End break first.") until you press **End break**. Panic
  still works at once. If the break's lobby drops, auto switch puts a healthy lobby in the break.
  Switching scenes in OBS itself still ends it. **Verbal reads:** give a sponsor `readScript` and
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
  that isn't on screen; about 4 s each, one at a time, the rest queued, merged per lobby. Each
  kind switches off in the graphics card; **B** pauses them all.
* **Intermission:** the **TT Intermission** scene between rounds: a countdown to the next round
  (set it on the Live desk's intermission card: minutes or a time), standings, storyline notes
  taking turns and the montage up next. Offered when every lobby has been out of a game for 45 s
  (**Intermission** or **Not now**); it doesn't come on or go off by itself between rounds (auto
  switch still puts it on when every lobby drops between games). A lobby going live is said on
  the card ("LJ IS LIVE"): go back with **Back to the game**. Key **I**.
* **Player cards:** a lower third with the player's real name, rank, points and today's
  impostor and crewmate records and kills. **Card** buttons on every notification, lobby and
  roster row, key **C** for the top notification's player, and by itself for the key player of
  each replay and montage moment. They go through the graphics queue, one big graphic at a time
  for 8 s by default (a replay's own card, 6 s): on the lobby's tile in a multi-view, or waiting
  while that lobby isn't on screen or its tile is too small.
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
  under** your mic (an OBS compressor keyed to it; the obvious mic is picked the first time).
  **HEAR** on the Live desk's Preview (Hear Preview, on by default) plays the lobby in Preview in
  your headphones (OBS's monitoring device, monitor only, through one **TT Cue** source), never on
  stream; it's silent on Mute all and while Desktop Audio is capturing. Apart from that, it only
  goes to the stream: nothing is played back to the players, and when voice sending starts the
  bot posts "voice in this channel may be recorded" in the lobby's voice channel chat. On older
  Windows, the referee ticks *Share system audio* instead (the old way: game sound and voice
  together, no separate levels).
* **Game replays kept by Red Alert:** every host's mod sends its game's replay (positions, timeline,
  walls) as the game is played, and Red Alert keeps it (`game-replays` next to its settings). So a
  game can be watched, to count, void or replay it, even when the host's PC drops out before the
  game is posted: the copy runs up to where the host stopped sending. **Lobby health → Game
  replays** lists them with **Watch**; interrupted games have a Watch button too. It opens the web
  replay viewer (`docs/replay-viewer.html`, built into Red Alert), which shows the impostors: keep it off stream.
* **Auto switch** (Lobby health, shown **Auto switch: ON** or **OFF**; the same setting as **Switch away** in the Live desk's Auto buttons) moves
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
  action back that many seconds so it matches what viewers see.
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
    card with a button that puts that lobby into Preview (**LJ into Preview**). Nothing switches
    until you press it and then **Take**.
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

### Team watch

The **Watch page** lets the rest of the production team (a producer, a second caster, someone
spotting plays) watch every game at once on their own PC or tablet, on the same network as the
caster's PC, and suggest what to show. They can't switch the stream: their **Show this** lands in
the Live desk's **Suggestions**, and the caster decides. It's off by default.

* **The page shows impostors.** Like the Live desk, it shows who the impostors are. Only give the
  link and the code to the production team, keep their screens off stream, and away from players.
* **Set it up (the caster):**
  1. Red Alert → Settings → **Team watch**: switch it on. The toast says the link ("Team watch on:
     your team opens http://192.168.1.20:8780/ and types the code.").
  2. The first time, Windows Firewall asks whether Red Alert may use the network: allow it on
     **private networks** only. Declined, nobody can connect.
  3. Give the team the link shown on the card (with **Copy**) and the code, by word of mouth or in
     a private message. Never post the code in a public channel.
* **Signing in (the team):** open the link, type your name (so the caster knows who's suggesting)
  and the watch code. A wrong code says "That code isn't right. It's in Red Alert: Settings → Team
  watch."; after too many it says "Too many wrong codes. Wait a minute, then try again."
* **What the team sees:** the tournament and round, what's ON STREAM and in PREVIEW, and a card
  per lobby: its picture with the red or green tally, the excitement badge (MUST SHOW, VERY HIGH,
  HIGH), the phase, map and health, crew v impostors, the task bar, a sabotage countdown, the
  players (dead crossed out, impostors marked IMP), the last 8 events with times, why it's rated
  as it is, and what the team has already suggested.
* **Show this:** a reason (Kill coming, Clutch, Meeting, Sabotage, Funny), or the player camera on
  a living player, and a few words if wanted (up to 60 characters). The same suggestion from
  someone else adds their name to it. One every few seconds per person. A lobby already on stream
  can only be suggested for its camera.
* **On the Live desk:** each suggestion shows the lobby, the reason, who sent it and how long ago,
  with **Into Preview** (Take puts it on), **Cut** and **×**. It clears when its lobby (or its
  camera) goes on stream, after 2 minutes, or when dismissed, and moves to **Earlier**. The
  Stream Deck's `suggest/next` puts the newest one into Preview.
* **The pictures:** with OBS connected, Red Alert takes a small picture of each lobby from OBS a
  few times a second, only while someone is watching, so the hosts upload nothing more however many
  watch. Without OBS each card shows the host's own video, small and muted ("OBS isn't connected:
  video straight from the host"), which costs each host a little more upload per person watching.
* **The code:** the card shows it, with **New code**: a new code signs everyone out, and they type
  the new one. Switching Team watch off signs everyone out too, and the page can't be reached at
  all. The card lists who's watching (name, address, last seen) with **Sign out** for each. The
  code is kept across a restart, but sign-ins aren't: after Red Alert restarts, the team signs in
  again.
* **Safety:** it's plain HTTP on your own network, so use it on a network you trust (the venue's
  or your home network, not public Wi-Fi), and don't forward its port on the router. The watch code
  is never written in the log and is taken out of support bundles.

## Discord setup (without a setup code)

### Stats (webhook, no bot needed)

Channel settings → Integrations → Webhooks → New Webhook → Copy URL. Paste the URL into
`StatsWebhookUrl`.

The same channel gets a **live status message**: the lobby code, the server (NA, EU, Asia or a custom one), map, phase, and each
player's colour, name and Discord link. It updates as people join, link and play, and moves
below each game report so it stays at the bottom. Deaths only appear once the game has
revealed them (at a meeting or the end), so it never gives away a kill. Give it its own
channel with `StatusWebhookUrl`, or turn it off with `LiveStatus = false`. **Post → Lobby
message** (Home → Tools) in The Button posts a fresh copy.

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
| Private results channel | View Channel, Send Messages, Embed Links, Attach Files, Read Message History, Add Reactions | Game files and standings (it reads back earlier games), the changes posted by The Button |

Also in the Developer Portal: **Message Content Intent** on, for the changes posted by The Button in the results channel.
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

The bot isn't hosted anywhere: The Button on the host's PC keeps it online while it's open
(the mod in Among Us does the work through it), so there's no server to run. The bot shows as
offline when no host has The Button open.

#### Timing

Muting for tasks happens straight away; opening voices up at the end can wait a moment:

| Change | Default | Setting |
| --- | --- | --- |
| Game starts → alive players muted and deafened | none | |
| Meeting ends (after the ejection screen) → alive players muted again | none | |
| Game ends → everyone unmuted | 3 s | `DelayGameEnd` |
| Meeting called → alive players unmuted | 0 s, so nobody misses the start of the discussion | `DelayMeetingStart` |

A death shows at the next meeting (see above), and **Unmute all** (Home) / **F9** never wait. Muting always goes out
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
player's meeting banner is outlined in their colour while they talk, and in the lobby a speaker
in their colour shows by their name. Players' own games don't change. Turn it off with `ShowTalking = false` under
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
  can type more. The Referee tab's **Points** card shows each player's @ next to their name.
* **`/new`** (hosts): picks where your lobby's live message goes. Type `/new` in the text
  channel you want (for example each lobby's own channel when several games run at once):
  your game moves the message there, removes the old one, and the bot posts it with the
  colour menu. The choice is remembered. It answers the host whose Discord is linked to the
  lobby's host player; if you haven't linked yourself yet, use `/new code:QWERTY` with your
  lobby code (that links you too). Referees can move any lobby's message with its code.
  Without `/new`, the message goes to the status channel as before. The bot needs View
  Channel and Send Messages in the channel you pick; if it can't post, The Button says so.
* **The newest link wins:** if a colour is already linked to someone, linking it again
  (menu or `/link`) moves it to the new person and unlinks the old one. The answer and The
  Button's Home say so ("Linked Pink (Millie) to @millie_b (replacing @someone)"), so a mistake is easy to spot.
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
  answer. `/link` only works while a host has Among Us open, since the host's game answers it.
* New links show on The Button's Home ("Linked Red (Soggy) to @soggy") and in the mod's log,
  not in the game chat.
* **Referees** (anyone who can mute members in the server) link someone else by adding
  `user`: `/link player:Red user:@Soggy`, `/unlink user:@Soggy`. The host can too, once linked.
* **Automatically:** in the lobby, a player whose in-game name matches exactly one person in
  the voice channel (their server nickname, display name or username, ignoring capitals,
  spaces and symbols) is linked for them, and The Button's Home says so. A wrong match is fixed
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
combined leaderboard. Each host's **Post → Leaderboard** (Home → Tools) shows the combined numbers.

In the Developer Portal, turn on **Message Content Intent** (Bot → Privileged Gateway
Intents) for every host's bot. Without it a bot can't read the files other bots posted, and
the log warns about it.

**To reset it**, a host presses **Reset…** (Settings → Tournament → Combined leaderboard), or
the organiser **Start over…** on the Organiser tab (Rounds). Typing `!resetleaderboard` in the
channel yourself does nothing. Only games posted after the newest reset message count. The older
game files stay in the channel, so deleting the reset message brings the old standings back.

## Nothing is typed in the game

Everything the host does is a button in The Button; players link in Discord. The mod never
reads the lobby chat. It only writes to it: the host sees its notes (round progress, a
player who left); links only show on The Button's Home. `PublicChat = true`
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
| General | `ControlPort` | 8766 | The app's private connection; -1 turns it off |
| General | `PublishLive` | true | Keep a "Live data" message in the private results channel for the Organiser tab and Red Alert |
| Discord | `StatsWebhookUrl` | | Game reports and leaderboard |
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
| AutoMute | `DeadCanTalkDuringTasks` | true | From the round after their death is revealed at a meeting |
| AutoMute | `MuteDeadDuringMeetings` | true | |
| AutoMute | `DelayGameEnd` | 3 | Seconds; see *Timing* |
| AutoMute | `DelayMeetingStart` | 0 | Seconds |
| AutoMute | `MuteSpectators` | false | See *Spectators* |
| AutoMute | `VoiceChannelId` | | Empty = where most linked players are |
| AutoMute | `SpectatorExemptUserIds` | | Comma separated |
| AutoMute | `AutoLinkByName` | true | |
| AutoMute | `LinkMenu` | true | The bot posts the live status with a colour menu for linking |
| AutoMute | `ShowTalking` | true | See *Who's talking* |
| AutoMute | `RefereeUserIds` | | Who else can talk in referee mode, comma separated |
| Scoring | *(see below)* | | Every point value on the tournament sheet |
| Debug | `FrameProfiler` | false | Log the mod's slow frames (see below) |
| Debug | `RefereeRecordSync` | false | Log each time the referee ghost's player record is sent, and whether it says dead |

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
| Finished every task | +1 (on top of the task bonus) | `CompletedTasks` |
| Voted for an impostor who got ejected | +2 each | `CorrectVoteOut` |
| Called the meeting where an impostor got ejected | +1 | `CaughtKiller` |
| Killed | −0.25 | `GotKilled` |
| Voted out by the crew | −1 | `EjectedAsCrew` |
| First player killed: ends on 90% of their crew teammates' average | see below | `DiedFirstShareOfCrewAverage` |
| Voted for a crewmate who got ejected | −2 each | `IncorrectVoteOut` |
| Voting bonus: share of meetings (alive) where they voted for someone | up to +1 | `VotingBonus` |
| Correct-voting bonus: share of their votes that were on an impostor (−2 at 0%, see below) | up to +2 | `CorrectVoteBonus` |
| Task bonus: % of their task effort finished, a long task counting double (−2 at 0%, see below) | up to +3 | `TaskPercentBonus`, `LongTaskWeight` |
| Win by tasks | +5 | `CrewTaskWin` |
| Win by vote | +3 | `CrewVoteWin` |
| Alive when the team loses to sabotage | −5 | `CrewSabotageLossAlive` |
| Any other loss | −2 | `CrewOtherLoss` |
| Alive when the team loses, on top of the loss: per round (meetings + 1), up to −4 | −0.25 each | `AliveLossPerRound`, `AliveLossCap` |

A crewmate's vote scores in three ways. A vote that **ejected** someone is a vote out: +2 on an
impostor, −2 on a crewmate. On top of that, two percentage bonuses cover every meeting:

* **Voting bonus (up to +1):** the share of meetings they were alive for where they voted for
  someone. Voting at every meeting earns +1; voting at 2 of 4 earns +0.5. A skip or no vote
  isn't voting.
* **Correct-voting bonus (up to +2):** the share of their votes (for a player) that were on an
  impostor, whether or not anyone was ejected. 3 of 4 right earns +1.5.

**Below 25%** the correct-voting and task bonuses don't just shrink to nothing: from 25% they
slide in a straight line down to −2 at 0% (`LowPercentBelow`, `LowPercentPoints`). So tasks at
25% earn +0.75, at 12.5% −0.625, at 0% −2; votes right at 25% earn +0.5, at 0% −2. The voting
bonus isn't affected. A crewmate who never voted for anyone gets no correct-voting line.

**Alive at the loss:** a crewmate still alive when the crew loses loses 0.25 more for every
round the game lasted (a round is the play between meetings, so rounds = meetings + 1), up to
−4, on top of the loss itself.

The **task bonus** scales with the share of task effort finished (up to +3), where a long
task counts as two short ones (`LongTaskWeight`). With 2 common, 3 long and 5 short tasks,
finishing everything but the long ones is 7 of 13 (54%, +1.615…), not 7 of 10.

The **first crewmate killed** ends the game on 90% of the average of their crew teammates
(everyone else on the crew who didn't disconnect), whatever they scored themselves. Dying
first once is often bad luck and costs little; dying first every game keeps a player out of
the top half. The report shows it as "Died first: 90% of crew average".

Points are never rounded: the percentage bonuses and the died-first score keep their exact value (a third of a point stays a third), and totals add the exact values. Screens and Discord show up to two decimals. Win and loss points go to the whole team, dead or alive, but not to anyone
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
* `games/<tournament>/game-<id>.json` (for example `game-LJ-3-20261003-192144.json`) holds
  the full record of each game: roles, every kill with its time, every meeting with the caller, the body and each vote, ejections, sabotages
  and the result.

A game the host leaves before it ends is saved as `-abandoned` and is not counted; a void
game is saved as `-void`.

**Saved safely.** Every file (the mod's, The Button's and Red Alert's) is written whole to a side
file next to it first, and only then takes the old file's place, so a crash, a power cut or a full
disk part-way through leaves the old file as it was. A file that can't be read (half-written, or
edited wrongly by hand) is kept as `<name>.corrupt-<time>` before anything is saved over it, and
the log says so; The Button's Home shows "A damaged file was set aside" for its own files and the
mod's.

**The Button's backup.** Every 10 seconds The Button copies what changed of the mod's games
(each game, the waiting list, the replays), `links.json` and each tournament's round and totals to
`%LOCALAPPDATA%\TheButton\backup`, outside the Among Us folder (never the setup code, the host's
Discord or the connection files). After reinstalling Among Us, Home says "The backup has N games
that aren't in Among Us's folder": close Among Us and press **Restore** (or Settings → Help →
**Restore backup**) to put back what's missing or older, or **Forget** them if they were removed on
purpose. **Backup folder** opens it.

## Building

```bash
dotnet test tests/TournamentTracker.Tests          # core logic and The Button, no game needed
dotnet test tests/TournamentTracker.Broadcast.Tests # Red Alert
dotnet build src/TournamentTracker.Plugin -c Release -p:GameLibsVersion=2026.x.y
```

The plugin builds against `AmongUs.GameLibs.Steam` from the BepInEx NuGet feed, with
`BepInEx.IL2CPP.MSBuild` generating the interop assemblies at build time. By default it
uses the newest game version (currently 2026.9.29: v0.1.64 was built against it); pass
`GameLibsVersion` to match your game. BepInEx is pinned to be.735, because newer builds use an Il2CppInterop version the
interop generator can't run. On GitHub, run the
**Build** workflow by hand (Actions → Build → Run workflow) to choose the version; give it a
`release_tag` (e.g. `v0.1.1` for The Button and the mod, `broadcast-v0.1.0` for Red
Alert) to publish a release, and `delete_tags` to remove old ones.

The workflow also has a **Windows** job, run on the same events beside the Linux build: the same
build and tests on Windows (the plugin and both apps' windows too), the Stream Deck plugin's tests
and `npm run validate`, then it starts TheButton.exe and RedAlert.exe (each must keep running and
answer on its own server), checks every installer script with Windows PowerShell 5.1, and runs
both .bat installers as a user would, from a folder with an apostrophe in its name. Its logs,
screenshots and test results are kept as the **windows-checks** artifact. Releases don't wait for
it: the Linux build alone builds and publishes them.

Layout:

* `src/TournamentTracker.Core` has everything that doesn't touch the game: the stat tracker,
  scoring, leaderboard, Discord REST and webhooks, the automute planner and dispatcher, and
  the commands The Button sends. It is unit-tested.
* `src/TournamentTracker.AppCore` is The Button's engine and pages (cross-platform, tested);
  `src/TournamentTracker.App` is the Windows window around it (WebView2), built in CI, and started
  there on Windows.
* `src/TournamentTracker.Common` is what both apps share: the organiser's tournament link
  (the Live data messages) and self-updating.
* `src/TournamentTracker.Broadcast` is Red Alert's engine and pages (cross-platform, tested);
  `src/TournamentTracker.BroadcastApp` is its Windows window, built in CI, and started there on
  Windows. The feed between a host and Red Alert is defined in `Core/Broadcast/FeedProtocol.cs`
  and `docs/broadcast-protocol.md`.
* `src/TournamentTracker.Plugin` is the BepInEx plugin: Harmony hooks on the game, and a
  per-frame driver that reads the game phase for automute. It compiles the core in, so the
  build is a single DLL.

If a game update renames a method, only the hook for that method stops working. The rest of
the mod still loads, and the BepInEx log names the hook that failed.
