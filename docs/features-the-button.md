# The Button: features

The Button is the Windows app the lobby host runs next to Among Us, on the same PC. It installs
and updates the host-only Tournament Tracker mod, takes the organiser's setup code, keeps the
lobby's Discord bot online, and gives the host one window to run the lobby from: automute,
rounds, checking each game's points before Discord sees them, and sending the game to the caster.
Referees and the organiser use the same app with an administration code, which adds the Organiser
tab. This page describes The Button as of v0.1.64. What the mod does inside Among Us, and exactly
what it posts in Discord, is in [features-mod.md](features-mod.md).

## At a glance

* **One app for the host:** finds Among Us, installs, updates and repairs the mod, and updates itself.
* **Discord goes through The Button:** the bot is online while The Button is open, and the mod's
  automute, reports and live messages reach Discord only through it.
* **Home runs the lobby on one screen:** a checklist of what's missing, the players with their
  Discord links and voice state, automute buttons, rounds and the lobby switches.
* **Every game waits for the host:** the Referee tab shows each finished game's points; change them,
  void the game or press **Submit points** to post it.
* **Send my game to the caster:** a private VDO.Ninja page shares the Among Us window, the lobby
  voice and the live game data with the caster's Red Alert.
* **Before an event:** **Check my connection** measures the upload; **Save support bundle** makes
  one zip with secrets taken out when something goes wrong.
* **Organiser tab** (administration code only): every lobby live, standings, award candidates and
  the round controls (build the next round's lobbies, change them, start a round), on a PC
  without Among Us if need be.
* **Nothing lost:** every file is saved whole or not at all, a damaged file is kept aside, and
  The Button keeps a backup of the mod's games, links and rounds outside the Among Us folder.
* **Setup code maker** (`docs/setup-codes.html`): the organiser's page for making preliminary,
  tournament host and administration codes.

## Installing and starting

* **One-click install:** `Install-TheButton.bat` from the latest release installs The Button for the
  Windows user (no admin needed) in `%LOCALAPPDATA%\Programs\TheButton`, with Start menu and desktop
  shortcuts, and opens it. Running it again replaces the app with the newest one. It works from
  any folder, even one with an apostrophe in its name (`C:\Users\O'Brien\Downloads`).
* **Finds Among Us by itself:** every Steam library, Epic Games' install records, then the usual
  folders. Settings → **Among Us** shows the folder (**Open folder**, **Change**); if none is found
  it lists any it can see as buttons, or takes a pasted folder (**Use it**).
* **Nothing typed in the game:** every host action is a button in The Button; the mod never reads
  the lobby chat.
* **Its own files:** settings in `%LOCALAPPDATA%\TheButton\app.json`, its log in `the-button.log`
  next to it (started again past 1 MB), and the backup of the mod's data in `backup` (see
  *Safe saving and the backup*).

## The window

* **Own title bar:** the logo bar is the title bar: drag to move, double-click to maximise or
  restore, and minimise, maximise and close buttons at the right. The thin edge resizes it.
* **Snaps like any window:** drag to a screen edge for half the screen, a corner for a quarter, the
  top to maximise; Win+arrow keys work. Maximised, it stops at the taskbar. Rounded corners on
  Windows 11.
* **Size:** opens at 1180 × 780, centred; it can be made as small as 760 × 520. Home and the Referee
  tab are laid out to fit one screen; a narrow window falls back to one scrolling column.
* **One copy at a time:** starting The Button again brings the open one to the front.
* **No WebView2:** on the rare PC without Windows' WebView2, The Button opens its screens in the
  normal browser and keeps a small window open (closing it quits the app).
* **Links open in the normal browser** (GitHub, the send page, the code maker).
* **Light on the PC:** the drifting starfield is drawn at most 30 times a second, stops while The
  Button is minimised, and stands still with Windows' reduced-motion setting.
* **Remembered in the window:** dismissed warnings, the chosen Points tab and whether the Results
  channel card is open.

## Top bar warnings

The bar shows only what needs the host; clicking a warning goes where it's fixed.

* **Among Us not found**, **Mod not installed**, **Mod needs repair** (the 32-bit loader left from
  before Among Us went 64-bit): open Settings.
* **No setup code installed** and **Setup code damaged**: open Settings.
* **Discord bot refused** (the token was reset or refused), **Round changes not heard** (the
  bot's Message Content Intent is off, so round starts and lobby changes from the Organiser tab
  aren't heard) and **Live message can't post**: open Home.
* **Updates, one pill at a time:** "The Button vX is out · Update", "Updating The Button to vX…",
  "The Button vX is ready · Restart", "The Button vX didn't install · Try again"; for the mod alone,
  "Mod vX installs when Among Us closes" (automatic updates on) or "Mod update in Among Us: vX".
* On an organiser's PC (an administration code and no Among Us), the game, mod and setup code
  warnings are left out.

## Home

### Notices about saved data

Shown at the top of Home, with or without Among Us running:

* **The backup has N games that aren't in Among Us's folder** (after reinstalling Among Us, say):
  **Restore** puts them back (close Among Us first); **Forget** (a second click: "Sure? Click
  again") takes them out of the backup too, when they were removed on purpose. Shown only when The
  Button has found Among Us.
* **A damaged file was set aside:** "<The Button's or The mod's> <file> couldn't be read (<when>).
  A copy is kept next to it as <file>.corrupt-<time>, so nothing in it is lost." Shown for 7 days;
  ✕ dismisses it.

### Before Among Us connects

* **Waiting for Among Us:** "Start Among Us and host a lobby: The Button connects on its own."
* **What's in the way instead:** "Among Us not found", "Mod not installed" or "Mod needs repair",
  each with **Open Settings**.
* **Organiser PC** (an administration code and no Among Us): "Among Us isn't needed here: the live
  lobbies and standings are on the Organiser tab, and the results channel is on the Referee tab",
  with **Open the Organiser tab**.
* **The bot's state:** with a bot set up, "Discord bot online: <name>", "Connecting the Discord
  bot…" or why Discord refused it, before Among Us is even started.

### The lobby

* **Lobby facts:** the phase as the title (Main menu, Lobby, Tasks, Discussion, Game over), then
  Lobby, Code, Server, Map, Round (tournament codes) and the number of players.
* **Host checklist:** one line of chips, green, amber or red; Mod, Code and connection chips open
  Settings when clicked:
  * **Mod:** "Mod up to date", "Mod vX is out" or "Mod needs repair".
  * **Code:** the tournament's name, or "No setup code".
  * **Round** (tournament codes): "Round N" or "Round not set".
  * **Voice** (with a bot): "Discord voice connected", "Connecting to Discord" or "Discord bot problem".
  * **Links** (with a bot): "7/10 linked".
  * **Live:** "Lobby message can't post", when Discord turns the lobby message down.
  * **Net** (when the game can be sent): "Connection not checked", "Upload N Mbps" (green from
    8 Mbps, amber under 8, red under 5) or "Connection problem".
  * **Video** (when the game can be sent): "Sending video", "Sending video (page not open yet)" or
    "Not sending video".
  * **Data** (with the private results channel): "Live data posted N s ago", "not posted yet" or
    "Live data not posting" with Discord's reason.
  * **Referee:** "Restart hosting from the main menu so host authority takes effect", when the
    referee ghost slot is on in a lobby hosted before host authority was.
* **Warnings line:** what the game said on its own in the last 20 minutes (a player left and
  whether to void, an extra game, settings put back, safe mode holding something back, a new link,
  a message from another player's game turned down). The newest shows on one line; **N more**
  opens the rest; ✕ dismisses (on the top line, all of them). Answers to the host's own buttons
  show as a pop-up instead.

### Safe mode and disconnect cards

* **Safe mode card (red):** shown when the mod has switched its in-game changes off because this
  Among Us is newer than the version it was built for; it gives both versions. The mod then leaves
  host authority, the referee ghost slot, the impostor rotation and the settings lock off; tracking,
  stats, overlays and Discord carry on.
* **Run normally anyway:** needs a second click ("Sure? Click again"); remembered for this game
  version only; pressed mid-game, it applies from the next game.
* **Running normally card:** after that, "Running normally on a game version the mod wasn't built
  for", with **Back to automatic**.
* **Disconnect card:** "You were disconnected: <why>" with the time and what to do, for any of
  Among Us's disconnect reasons in plain words (timed out, kicked, banned, wrong version, servers
  full, anti-cheat…). Red, with a reminder to check the Referee tab, when it happened during a game.
  Shown for an hour; leaving on purpose shows nothing; ✕ dismisses it.
* Both cards need a mod from v0.1.63 or later; with an older mod they don't appear.

### Players and linking

* **Players card:** every player as their crewmate head and name, with "you", "ref" (the referee
  ghost) or "left" tags; dead and departed players are faded.
* **Discord link under each player:** their @name, or a highlighted **Link** when they aren't linked.
* **Voice state:** while automute is on, a muted, deafened or open icon by each linked player.
* **Link from The Button:** press **Link** (or a player's @name): an @ is already in the box; type
  the rest of their Discord name (username or server nickname) and press **Link**. If several people
  match, The Button lists them to type more. **Unlink** removes a link; Esc or **Cancel** closes the
  box. Needs a bot and the server.
* Players can still link themselves with `/link` or the colour menu in Discord; new links show on
  Home, not in the game chat.

### Automute card

* **Status line:** "N of M linked · the rest type /link in Discord", "Connecting to Discord voice…",
  "Referee mode: only referees can talk", "Off: the bot won't touch anyone's voice…", or why it's
  not working. Without a bot: "Off: it needs a Discord bot. Add one in Settings → Your Discord, or
  use a setup code with a bot in it."
* **Automute:** on or off. Off stays off after restarts until it's switched on again, so
  AutoMuteUs can be used instead; linking and the lobby message still work.
* **Referee:** mutes everyone in the voice channel (they can still hear) except the host and the
  code's referees, to explain the rules; press again to end. Needs automute on and the host
  linked to talk; it ends by itself when a game starts.
* **Spectators:** mutes people in the game's voice channel who aren't playing while a game runs.
* **Unmute all:** unmutes everyone at once and switches automute off; press **Automute** to resume
  (the answer says so: "Press Automute on The Button's Home to turn it back on.").
* Without a bot the buttons are hidden and the card says automute needs one.

### Round card (tournament codes)

* "Round N: X of Y games played", or a reminder to start round 1 before the first game.
* **Next round**, or type a number and **Set**. Points restart for the round; the running total
  keeps counting. Only between games.

### Tools card

* **Settings lock:** keeps the lobby on the tournament's settings from the code. Off is for a
  casual game and lasts until Among Us restarts. Greyed out when the code has no settings.
* **Referee ghost slot** (experimental): the host becomes a ghost referee at the start of each game
  (never impostor, no tasks, not scored); set the lobby to 11 players. Only between games.
* **Host authority:** on by default; makes players without the mod see the referee as a dead
  (crossed-out) card and never on the map. Applies to the next lobby hosted from the main menu;
  modded lobbies are hidden from public matchmaking, lobby codes work. Only shows with a mod that
  reports it.
* **Stream overlay:** starts the mod's OBS overlay and shows its address (`http://localhost:8765/`);
  remembered. See the README's [Stream overlay](../README.md#stream-overlay).
* **Send my game to the caster:** see the next section. **Open again** opens a fresh send page.
* **Post:** **Leaderboard** (a tournament's round standings for this lobby and every lobby, else
  the combined or lobby leaderboard), **Server standings** (needs a tournament code with
  preliminary channels) and **Lobby message** (posts a fresh live lobby message at the bottom).

## Send my game to the caster

* **What it does:** shares the host's Among Us window, the lobby's voice and the mod's live game data
  with the caster's Red Alert over one private VDO.Ninja link (random name and password, kept for the
  tournament, written only in the private results channel).
* **Needs:** a setup code with a bot and the private results channel (tournament host code, or a
  preliminary code with the Red Alert channel). Otherwise the switch isn't shown.
* **The send page:** turning it on opens "Sending to the caster" in the normal browser, with
  VDO.Ninja inside it and instructions for what to pick. Leave the tab open while playing.
* **What to share:** **The Among Us window** (recommended: the rest of the screen stays private;
  the browser's picker opens on its Window tab and won't offer the send page itself) or **Entire
  screen** (picker opens on the screen, with Share system audio). Set on the page or in Settings →
  Your connection; the instructions follow the choice and whether lobby voice is on.
* **The referee's view is shared:** as the referee ghost the host's screen shows who the
  impostors are. It goes only to the caster, and with Entire screen everything on that screen goes
  too (The Button's Referee tab included).
* **Pick again:** asks VDO.Ninja what to share again (wrong window, or Among Us restarted); the
  caster's picture comes back once picked.
* **Lower quality (slow upload):** 720p at 30 fps, at most 2.5 Mbps, instead of 1080p at up to 60
  fps. One setting, on the page or in Settings; kept across restarts.
* **Lobby voice:** The Button records Discord's sound (the lobby as the host hears it, never their
  own voice) and Among Us's sound separately, mixes them and the page sends the mix as its own
  stream. Nothing is played back to anyone.
  * Meters and level sliders (0–150%) for **Discord** and **Among Us** (100% and 50% to start),
    **Include my microphone** and **Send lobby voice**; levels are remembered.
  * A window share has no sound, so the lobby voice carries the game sound; with it off, share
    Entire screen and tick Share system audio instead.
  * Needs Windows 10 version 2004 or later; no virtual cables. If the browser holds sound back, the
    page says to click anywhere on it.
  * Stops recording about 30 seconds after the page is closed, and starts again when it reopens.
  * When it starts sending, the bot posts a note in the lobby's voice channel chat that voice may
    be recorded for the broadcast (at most once every 30 minutes per channel).
* **Player camera:** when the caster turns it on in Red Alert, the page also sends the mod's
  close-up of one player as a third stream (the caster picks who; nothing shows on the host's
  screen). The footer counts the pictures; during meetings it sends a "Player camera paused" card.
* **Live data that isn't lost:** every message is kept until the caster confirms it, and sent again
  after a drop with its own time; the page shows "Disconnected from caster, reconnecting… Keep
  playing: nothing is lost" meanwhile.
* **Health for the caster:** every 2 seconds the page tells Red Alert whether the picture and data
  are going out and how much is waiting, plus the host's Twitch name.
* **Warnings on the page:** "No picture is going out to the caster" (press Pick again), "Your
  upload can't keep up, so the picture is lowered…", "Live game data isn't reaching The Button"
  (press Open again).
* **Never "sending" when it isn't:** The Button switches sending off if the page has been closed
  for 15 seconds or the picture has stalled for 20, and says so.
* **One tab at a time:** **Open again** opens a new page; the old tab stops sharing and says "This
  tab isn't sending any more". Switching sending off does the same.
* **Survives an update:** The Button keeps the same local address and key after a restart, so an
  open send page carries on.
* **The caster's controls come back the same way:** Red Alert can switch the referee's spectator
  view and send roster names for the referee's nameplates; only those commands are accepted.
* **Upload:** about 4–6 Mbps for the picture, 2–3 Mbps more for the player camera, 0.1 Mbps for the
  voice: 10 Mbps recommended.

## Referee tab

Every finished game waits here before Discord sees anything. The tab shows who the impostors
were: keep The Button where players can't see it. Players never see these points in the game or
through the bot.

* **Tab count:** the number on **Referee** is how many games are waiting.
* **Top line:** the last game's result, a **Void reason (optional)** box used by every Void button
  on the page, **Void this game** (the running game, or the last one), **Unvoid**, and **Reset
  points…**.
* **Reset points…:** type RESET, then **Reset all points**: every game on this PC is moved to a
  dated "reset" folder in the games folder (never deleted), totals and round counts go to zero.
  Discord isn't touched.

### Waiting for you

* **Each game:** its name, round, winner, map and length, and a **Wrong settings** tag (listing
  what) when it started off the locked settings.
* **Each player:** crewmate head, name and points; impostors ringed and named in red. Hover the
  points for where they came from.
* **Referee changes:** type any amount up to 3 significant figures (0.125, 1.25, 12.5, 125; empty
  means 1) and press **+** or **−**; it shows as "Referee change" in their breakdown.
* **Void:** keeps the game out of the points and, if it's never submitted, off Discord for good.
* **Submit points:** posts the game (report, standings, the round's scores); on a void game the
  button reads **Done (stays off Discord)**. **Submit all N** sends every waiting game.
* Waiting games stay waiting across restarts. With **Hold each game until the host presses Submit**
  off in the code (or nowhere to post), the tab says games post as they end.

### Games

* The 12 newest sent games, one line each (name, round, result, map and time), with **Void** or
  **Unvoid**. Voiding or unvoiding a sent game tells Discord.

### Points

* One table at a time, with tabs: this lobby's round (with the cut line and the players moving on
  in green, and from round 2 the running total), every lobby's round (with a results channel), and
  the running total; with a preliminary code, the lobby's leaderboard.
* Columns: points, total, games, wins and losses, impostor wins, kills, votes (right and wrong);
  columns that don't fit are in each row's tooltip. A note says how many voided games are left out.

### Results channel (administration code)

* A card for whoever has the administration code: any lobby's games, posted in the private
  results channel as the administration bot. Folded (remembered), except that on an organiser's
  PC it starts open.
  * Adjust: pick the lobby, the game's number if need be (3 for LJ-3; empty: the game that lobby
    is playing, or played last), the player (colour or name), the points, **−** or **+**, a
    reason. A name that could be read as the points is refused ("use the player's colour").
  * Void or unvoid a game (`LJ-3`) or the game a lobby is playing (`LJ`), with a reason.
  * Rounds and lobbies are on the Organiser tab; there's no box to type a command.
* Works without Among Us; each lobby's mod applies the change to its own game, and its bot ticks
  the message (✅, or ❓ when the player wasn't found).

## Organiser tab (administration code)

Hidden from everyone without the administration code. It reads the "Live data" message each
tournament lobby's bot keeps in the private results channel, so it works on a PC without Among Us.

* **Header:** the tournament, the round and how many lobbies are live; a card pointing casters to
  **Get Red Alert** (the caster tools live there now).
* **Lobbies:** a card per lobby: name, phase (Lobby, In game, Meeting, Game over, Closed), round
  and game "N of M", map, lobby code, how long since it was heard from, **Video** / **No video**,
  the crewmates (dead faded) and the last two events. A meeting, ejection or game end marks it for
  a few seconds.
* **Gone quiet:** a lobby silent for over two and a half minutes is faded, with "No news from LJ
  for N min: check the host's game is still open and online."
* **Show roles and real deaths (staff only):** adds impostors (ringed red), real deaths, kills,
  sabotages and players leaving. Keep it off any screen players can see.
* **Standings:** each lobby's round table ("after game N of M" or "final", top N move on) and every
  lobby together; until a new round's first game, the last round's final tables stay up.
* **Awards:** the top three candidates for Top score, Most kills, Best impostor, Sharpest voter
  (at least 3 votes), Impostor hunter, Task machine and Survivor, for **This round** or **Whole
  tournament**. **Copy for Discord** copies them as a message; the organiser picks the winners.
* **Rounds:** posted in the private results channel as the administration bot; every lobby's
  game switches round, and the lead lobby builds the lobbies and pings the players.
  * **Round** (filled with the next round) and **Build its lobbies**: everyone who moved on from
    the round before, snake-seeded into lobbies of up to 10, with the best of the rest as
    alternates (round 2 or later).
  * **Starts in** N **min** and **Start round** (a second click: "Sure? Start round N"): every
    lobby switches to that round (after the game it's playing) and the players are pinged with
    their lobby; empty or 0 starts it now.
  * **Change the lobbies:** a player and a lobby (A, B… Final) with **Move there**, **Add there**
    (an alternate, or someone new) and **Drop**; two players and **Swap** (one word each: the
    start of a name is enough); a lobby, the lobby label of its host and **Set host**. The boxes
    suggest this round's players and the lobbies' hosts. Each change posts the new lobbies in the
    results channel.
  * **Standings: Start over…** asks "Start every lobby's combined standings over?" (**Reset** or
    **Cancel**): every lobby's combined standings count only games after it. Deleting the reset
    message in the results channel undoes it.
* **Problems:** says why the channel can't be read (the bot's permissions, a wrong channel).

## Settings tab

* **Among Us:** the game folder (see *Installing and starting*).
* **Mod and updates:** both versions on one line ("The Button 0.1.64, 64-bit Among Us with the mod
  v0.1.64. Everything is up to date."; on an organiser's PC, "Organiser PC: Among Us isn't needed
  here."), one button for whatever is needed (**Install the mod**,
  **Repair the mod**, **Update the mod to vX**, **Update The Button to vX**, **Update both to vX**),
  **Restart The Button to finish**, **Reinstall the mod**, **Open log** (the mod's log), the
  **Update automatically** switch, and whether The Button is connected to the mod.
* **Setup code:** paste the organiser's `TT1-…` line and press **Use this code**.
  * In use, it's one line: the tournament, the kind of code, the server, "automute on (N bots)" or
    "no bot", whether lobby settings are locked, and "lead lobby".
  * **Show details:** the code (kind, tournament and its ID, server, automute, players who move on,
    games per round, impostor rotation, lead lobby) and every locked lobby setting and role. Point
    values aren't shown: they stay with the organiser.
  * **Change code** (the new one replaces it) and **Remove** (the mod goes back to its settings file).
  * Saved during a game, a code is used as soon as that game ends.
  * The Button keeps its own copy and puts it back if reinstalling the mod or Among Us removes it.
  * An administration code pasted here unlocks the Administration card and the Organiser tab
    instead. It's kept in The Button's own settings, so it works on a PC without Among Us; a
    host's code needs Among Us found first (it goes in the mod's folder).
* **Your Discord:** the host's own bots and channels, so the organiser needn't hold anyone's token
  and a host can change bots without a new code.
  * **Server ID** (the code's server is used when it's empty), **Bot 1** to **Bot 3** (2 and 3 for
    faster muting), **Public report channel webhook**, **Live lobby channel webhook** (with a bot).
  * **Save and check** asks Discord about each one: a refused token, a bot not in the server or a
    webhook that doesn't exist is not saved, with why; the rest is.
  * Saved bots go online whenever The Button is open with a setup code in, and are used instead of
    any bot in the code. Tokens stay on the PC; the screen shows only each bot's name and the
    token's last 4 characters. **Remove** takes a bot or webhook out.
  * With a preliminary code the public channel gets each game's results and timeline only; the
    organiser's private channel still gets everything.
* **Your connection:** **Check my connection** (below), **What to share** (The Among Us window or
  Entire screen) and **Lower quality**, the same settings as the send page.
* **Your Twitch:** the host's channel name, shown with their lobby on the tournament stream (sent
  with the game). Letters, numbers and _ only; empty removes it.
* **Tournament** (tournament codes with a results channel):
  * **Lead lobby:** **Take over** makes this lobby's game the one that builds the next round's
    lobbies and pings the players when the organiser starts a round from the Organiser tab; one
    lobby at a time (usually the organiser's).
  * **Combined leaderboard: Reset…** starts every lobby's combined leaderboard over, after a
    confirm. Deleting the reset message in the results channel undoes it.
* **Administration** (administration code only): "Unlocked: <tournament>", **Make setup codes**
  (opens the code maker in the browser), **Lock** (hides the card and the Organiser tab again), and
  **Mod console window**: BepInEx's black log window when Among Us starts, Hidden by default,
  changed on this PC only.
* **Help:** **Save support bundle** (below), and with Among Us found, the backup: "It holds N files,
  last copied at <time>", **Restore backup** (close Among Us first) and **Backup folder** (see
  *Safe saving and the backup*).
* **Advanced** (folded): **Data folder**, **Config file** and **Mod log**.

## The mod: installing, updating and repairing

* **Install:** downloads the latest release's full bundle (the mod and BepInEx 6 build 735, 64-bit)
  from GitHub and copies it into the Among Us folder, with progress ("Downloading vX (64-bit)…",
  "Installing…"). The first Among Us start afterwards takes a few minutes while BepInEx sets up.
* **Only what changed:** files already the same are left alone. A file that's briefly busy is tried
  again; one that stays in use is renamed aside and cleaned up next time, or The Button names the
  program holding it.
* **Never loses settings:** links, stats, games and the setup code live in BepInEx's config folder,
  which the bundle never touches.
* **Checked download:** the bundle is unpacked only when its size and SHA-256 match what the GitHub
  release lists; a mismatch is deleted and nothing changes. Older releases without a checksum still
  install (the log says so).
* **Repair:** a 32-bit loader left from before Among Us went 64-bit shows as "Mod needs repair";
  The Button swaps in the 64-bit one by itself once Among Us is closed (trying again every 15
  minutes if it fails), or on **Repair the mod**.
* **Updates:** with **Update automatically** on, a new mod version installs by itself while Among
  Us is closed. **Update** pressed while Among Us runs waits and installs as soon as it closes.
* **Checks itself:** after installing, The Button confirms the mod and a 64-bit loader are in place,
  and says what to do if not (close Among Us, run as administrator for a game in Program Files).
* **Console window kept as set:** BepInEx's console stays hidden (or shown, if chosen) after
  installs, repairs and BepInEx's first run.
* **Version shown:** read from the mod file itself, so a copy installed by hand shows correctly.

## Updating The Button itself

* **Looks for a new release** on GitHub every 20 minutes while open.
* **Update automatically** (on by default, one switch with the mod's): downloads the new
  `TheButton.exe` next to the running one; it's used from the next start. The top bar then offers
  **Restart**, and Settings **Restart The Button to finish**.
* **Off:** the top bar and Settings say when a version is out; **Update** downloads it.
* **Checked download:** the download must match the size and SHA-256 the GitHub release lists, and be
  a complete Windows program. One that doesn't is deleted and nothing changes: "The download didn't
  match the release (it may be incomplete). Nothing was changed; try again later.", and the top bar
  shows "The Button vX didn't install · Try again". Older releases without a checksum still install
  (the log says so).
* **Safe swap:** the old copy is kept until the new one is in place and deleted at the next start.
* **Restart after an update:** the new copy waits for the old one to close, then opens.

## Safe saving and the backup

* **Saved whole or not at all:** every file The Button and the mod save (settings, games, the
  waiting list, links, rounds, totals) is written to a side file next to it first, flushed to the
  disk, and only then takes the old file's place. A crash, a power cut or a full disk part-way
  through leaves the old file as it was. A busy file (antivirus, another program reading it) is
  tried again for a moment.
* **A damaged file is kept:** a file that can't be read (half-written, empty, edited wrongly) is
  kept as `<name>.corrupt-<time>` next to it before anything is saved over it, the log says so,
  and Home shows "A damaged file was set aside" (see *Notices about saved data*).
* **The backup:** every 10 seconds The Button copies what changed of the mod's data to
  `%LOCALAPPDATA%\TheButton\backup`, outside the Among Us folder: each saved game, the waiting
  list, the replays (the newest 20 per tournament), `links.json`, and each tournament's round and
  totals. Never the setup code, the host's Discord, the connection files or the count message
  (it holds a webhook). Each copy keeps its file's time, and a newer file is never replaced by an
  older one.
* **Games removed from the mod's folder stay in the backup,** and while they do, the backup keeps
  its own links, waiting list, round and totals for that tournament. Home then shows "The backup
  has N games that aren't in Among Us's folder" with **Restore** and **Forget**.
* **Restore backup** (Settings → Help, or **Restore** on Home; Among Us closed, else "Close Among
  Us first: the mod would save over what's put back."): puts back what's missing or older in the
  mod's folder and says what ("Put back from the backup: 3 games, 2 replays and the points
  totals."). Where the mod's copy is newer it stays, but links and the waiting list are added to;
  a tournament that got games back has its totals worked out again from its games.
* **Backup folder** opens it.

## Check my connection

* **What it measures:** Discord's and VDO.Ninja's answer times and jitter, the upload speed (four
  uploads at once to Cloudflare's speed test for about 7 seconds), and Discord's answer time while
  uploading (lag under load).
* **Small and quick:** sends at most 30 MB and takes about 15 seconds; **Stop** stops it; a run
  never takes more than 75 seconds.
* **The verdict:** green "Your connection is ready to send your game", "Enough… with little to
  spare", amber "weak", or red "too weak"/"found a problem": 10 Mbps upload recommended, under 8 a
  warning, under 5 a problem; answers over 200 ms or jittery are a warning, over 400 ms a problem.
* **What helps:** advice for the result: a wired connection, pausing cloud backups and other
  uploads, **Lower quality**, a router's SQM or QoS setting, telling the organiser when it's too slow.
* **Honest when blocked:** if the speed test, Discord or VDO.Ninja can't be reached, it says
  "couldn't be measured" or "didn't answer" rather than giving a number.
* The latest result shows on Home's checklist and goes into the support bundle.

## Support bundle

* **Save support bundle** (Settings → Help): one zip,
  `TournamentTracker-support-<date>-<time>.zip`, on the Desktop (else Downloads), and the folder
  opens so it can be sent.
* **What's in it:** a README listing every file; a summary of what The Button saw (mod connection,
  safe mode, the last disconnect, host authority, the last connection check, sharing and lobby
  voice, the bot, the setup code's description, update settings); versions (The Button, the mod,
  Among Us, the loader, Windows); The Button's log and settings; the mod's log, config, BepInEx's
  settings and its state, safe mode and disconnect records; only a list of saved games and replays;
  Among Us's own log. The summary also counts the backup (files, last copy, games only it has)
  and lists any damaged file set aside, but no file from the backup goes in; on an organiser's PC
  it says so, with the Organiser tab's problem if there is one.
* **Nothing secret:** bot tokens, webhooks, setup and administration codes, passwords and keys are
  taken out of every file; the setup code file, the host's Discord file, links and connection files
  never go in. Files over 5 MB keep their last 5 MB; anything that isn't text is left out.
* Works on a PC without Among Us too (it says why there are no mod files).

## Discord through The Button

The Button carries the mod's Discord traffic; the mod decides what to post (see
[features-mod.md](features-mod.md)).

* **Bot online early:** while The Button is open with a setup code in (or bots in Your Discord),
  the lobby's first bot is connected, so it shows online and `/link` works before Among Us starts.
* **Only through The Button:** the mod's automute, reports, live messages and webhooks go through
  it, and only to Discord addresses. Without The Button open nothing reaches Discord, and the mod
  says so when a game starts.
* **Follows the host's voice channel:** while Among Us is hosting, the bot sits muted in the host's
  voice channel (for who's talking) and leaves when Among Us closes.
* **Never leaves people muted:** The Button keeps a list of who it has muted (server and user IDs
  only). If Among Us closes or crashes, they're unmuted after about half a minute; when The Button
  closes it unmutes them on the way out; after a crash of The Button, the next start unmutes anyone
  left (unless the game from before is still running and takes them back). Someone not in voice is
  unmuted when they return.
* **Warnings:** a refused token or missing Message Content Intent shows in the top bar and on Home.

## What the host sees and what reaches Discord

Stays in The Button (host and staff only):

* Each finished game's points and impostors while it waits in the Referee tab, and the point totals
  (players never see them in the game or through the bot).
* Point breakdowns on hover, wrong-settings tags, the voice icons, the warnings line, safe mode and
  disconnect cards, the checklist.
* Games voided before **Submit** (they never reach Discord), and **Reset all points** (Discord isn't
  touched).
* The Organiser tab, with **Show roles and real deaths** for staff.

Reaches Discord (posted by the mod through The Button, details in [features-mod.md](features-mod.md)):

* The live lobby message, kept up to date; **Lobby message** posts it again.
* Each game's report, standings and round scores, on **Submit points** (or as each game ends when
  the code doesn't hold games).
* Voids and unvoids of games already sent.
* **Leaderboard** and **Server standings** when pressed; the combined leaderboard reset message.
* Automute (voice state changes), **Referee** mode and **Spectators**, and links made from Home.
* The "Live data" message in the private results channel (read by the Organiser tab and Red Alert),
  which carries the private video link while the game is being sent; the lead lobby note.
* The voice-recording note in the voice channel chat when lobby voice starts.
* Changes from the Results channel card and the Organiser tab's Rounds card (Start over…
  included), as the administration bot.

## Setup code maker (`docs/setup-codes.html`)

The organiser's page for making codes. It runs in the browser on the organiser's PC; nothing typed
is sent anywhere. Download it from the release, or open it from The Button with an administration
code (Settings → Administration → **Make setup codes**).

* **One screen:** pick the kind of code at the top, then fill three columns in order: **1 Tournament
  and Discord**, **2 Rules**, **3 The code**.
* **Kinds of code:**
  * **Preliminary** (hosts in other servers): tournament name, preliminary server, your private
    reports channel webhook; optional server ID, bot token(s), Red Alert channel ID and live lobby
    channel webhook. Usually no bot: hosts add theirs in Your Discord.
  * **Tournament host** (your trusted hosts): tournament name, results channel webhook; optional
    server ID, up to 3 bot tokens (or none: the host adds theirs), private results channel, live
    lobby channel webhook, preliminary channel IDs, referee user IDs, and **Lead lobby** (one host).
  * **Administration** (you, referees, casters): tournament name, one administration bot token and
    the private results channel ID; unlocks The Button's Organiser tab and Red Alert.
* **Tournament ID:** filled in from the name (and the server, for preliminaries); every code for the
  same event needs the same one.
* **Rules:** players who move on per lobby (default 5) and games per round (default 3) for
  tournament and administration codes; **Hold each game until the host presses Submit** (on);
  **Impostor rotation** (on, each repeat at 2%, changeable).
* **Lock the lobby to these settings** (on): impostors, kill cooldown and distance, player speed,
  emergency meetings and cooldown, discussion and voting time, crewmate and impostor vision, common,
  long and short tasks (capped at the game's 4, 15 and 23), task bar, confirm ejects, anonymous
  votes, visual tasks, ghosts do tasks.
* **Roles…:** Engineer, Scientist, Guardian Angel, Noisemaker, Tracker, Detective, Judge, Spirit
  Guide, Shapeshifter, Phantom and Viper, each with a number per game and a chance; all start off.
  Each role's options (cooldowns, durations, "leave evidence"…) can be held too; an empty one keeps
  the host's own value. **All roles off** clears them.
* **Point values…:** every rule on the point sheet, changed values in green and counted ("N changed
  from the point sheet"), **Back to the point sheet**, and a box for values copied from the
  [Points Lab](points-lab.html). Defaults:
  * Impostor: kill +1 each, first blood +1, voted a crewmate out +2 each, sabotage win +5, vote win
    +4, kill win +4, lost to tasks −3, lost to vote −2, voted out first −2, voted out later −1.
  * Crewmate: finished every task +1, task % bonus up to +3 (a long task counts as 2 short), correct
    vote out +2 each, voted every meeting up to +1, votes on impostors up to +2, below 25% the %
    bonuses slide to −2 at 0%, caught and voted out the killer +1, died first ends on 90% of the crew
    average, got killed −0.25, voted out −1, incorrect vote out −2 each, task win +5, vote win +3,
    alive at a sabotage loss −5, any other loss −2, alive at a loss −0.25 per round up to −4.
  * Other: win by disconnect 0.
* **The code:** a to-do list ticks off what's still missing (with checks on webhook links, server and
  channel IDs and token shapes); when complete it says "Ready: …" and **Copy code** copies it.
* **Private when it must be:** a code with a bot token in it gets a warning to send it privately.
  Codes are encoded, not encrypted: anyone holding one can read what's in it.
* **Load an old code:** paste any code to fill the form, change it and copy a new one.
* **What the host does:** the steps for the host (or, for an administration code, for referees and
  casters) under the code.

## Local connections

Everything listens on this PC only (127.0.0.1). The Button's own requests, the Discord bridge and
the mod's connection each need a private key, so no website can drive them.

| What | Port |
| --- | --- |
| The Button's screens and the send page | A free port, kept between runs |
| The Discord bridge the mod uses | A free port each start, written in the mod's folder |
| The mod's connection, which The Button talks to | 8766 (or a free one, written in the mod's folder) |
| The stream overlay (mod) | 8765 |

## Limits and requirements

* **Windows PC** with the Steam or Epic Games version of Among Us, 64-bit (The Button always installs
  the 64-bit loader). Uses Windows' WebView2; without it the screens open in the browser.
* **Discord needs The Button open:** without it the mod still tracks and saves games, but nothing is
  posted and nobody is muted.
* **Most of Home and the Referee tab need Among Us running with the mod;** rounds and the referee
  ghost slot change only between games. Waiting games reappear when Among Us is back.
* **A host's setup code needs an Among Us folder first** (it goes in the mod's folder). An
  administration code doesn't: the Organiser tab and the Results channel card work on a PC without
  Among Us.
* **Automute, linking, Referee and Spectators need a bot** (from the code, or Your Discord with a
  setup code in) and the server ID. Referee mode needs the host linked to talk.
* **Send my game to the caster needs** a code with a bot and the private results channel, the send
  page left open in the browser, and about 10 Mbps upload. Lobby voice needs Windows 10 version
  2004 or later.
* **Installing, updating or repairing the mod needs Among Us closed;** a game folder in Program Files
  may need The Button run as administrator.
* **Host authority and safe mode changes apply from the main menu** (the next lobby hosted); in safe
  mode the referee ghost slot, impostor rotation, settings lock and host authority are off.
* **Shows impostors:** the Referee tab, the Organiser tab with roles on and the shared referee view
  must never be where players can see them.
* **Codes aren't encrypted;** any code with a bot token in it must be sent privately.
* **Points and round changes only from The Button:** the mods act only on what a bot posted in the
  results channel, so a command typed in Discord does nothing; every host needs a mod from this
  version on, or a typed command would still count in their lobby.
* **The backup** is on the host's PC only, and only for the mod's data folder (not the setup code,
  which The Button keeps in its own settings anyway).
* **No "check now" for updates:** The Button looks every 20 minutes; it doesn't remember its window
  size between runs.
* **Check my connection** reaches speed.cloudflare.com, discord.com and vdo.ninja; a firewall, VPN or
  school or work network can block it.
* **The Organiser tab only sees lobbies** whose game has a bot and the private results channel.

## See also

* [README: Install (host only)](../README.md#install-host-only)
* [README: Running a tournament (organiser)](../README.md#running-a-tournament-organiser) and
  [3. Setup codes](../README.md#3-setup-codes)
* [README: During the tournament](../README.md#during-the-tournament)
* [README: Organiser tab](../README.md#organiser-tab-administration-code)
* [README: Referee ghost slot and host authority](../README.md#referee-ghost-slot-experimental)
* [README: Automute](../README.md#automute-bot), [Referee mode](../README.md#referee-mode),
  [Spectators](../README.md#spectators) and [Linking players](../README.md#linking-players-to-discord)
* [README: Scoring](../README.md#scoring)
* [README: Red Alert (the broadcaster's app)](../README.md#red-alert-the-broadcasters-app) and
  [Caster overlay and game video](../README.md#caster-overlay-and-game-video)
* [features-mod.md](features-mod.md): the mod in Among Us and everything it posts in Discord
* [dry-run.md](dry-run.md): the 30-minute rehearsal with every host and the caster
* [broadcast-protocol.md](broadcast-protocol.md): the feed between a host and Red Alert
* [setup-codes.html](setup-codes.html) and [points-lab.html](points-lab.html)
