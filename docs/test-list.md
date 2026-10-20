# Test list 2

Things to try in a real game, from The Button and mod 0.1.41 and Red Alert 0.3.9 on. Tick when it works; note what didn't. New items are added at the end of their section; a new release gets its own section.

## Before you start
_Update everything first. Turn on the F10 profiler for the whole test night._
- [ ] The Button and the mod are 0.1.41 — the log's first lines say "Tournament Tracker 0.1.41 loaded".
- [ ] Red Alert is 0.3.9 — Settings → Updates → Check now → Update → Restart Red Alert.

## Lag
_Polus, 10 players, as the referee, player camera on stream._
- [ ] No "slow frame" lines from PlayerCam — before, 239 of 245 slow frames were the camera (17–35 ms each).
- [ ] PlayerCam's average stays small in the 10 s summary lines — the game feels as smooth as with the camera off.
- [ ] No "reading pictures back the slow way" line in the log — if there is one, send it: the camera fell back to the old way.

## Who's talking in meetings
_Host linked to Discord and in the voice channel; players linked._
- [ ] Speaker and colour outline on the talking player's card — in every meeting, not only the first.
- [ ] Works when you're a normal player, not the referee — the host sees it either way.
- [ ] Log line "Who's talking: meeting with N cards, M matched" — M should equal the number of players. Send it if the lights don't show.
- [ ] Lobby speaker by the name still works

## Referee ghost
- [ ] Chat button stays on screen during play — and opens the full chat to type.
- [ ] Meeting mini chat names match each player's colour — black and brown slightly lighter so they read.
- [ ] Task animations show (scans, asteroid shots, trash) — with Visual tasks off for the lobby. May not work if players' games don't send them: note what you see.

## Settings lock
_Tournament or preliminary code, lock on._
- [ ] Change a setting in the lobby: put back, one message — only in your chat.
- [ ] Players joining and leaving don't repeat the message
- [ ] The message can come once more after the next game

## Discord and The Button's Home
- [ ] Live message shows Server: NA (or EU…) — next to the code and map.
- [ ] Home shows Server next to Code and Map

## Referee tab (The Button)
_Tournament code with a results channel or webhook._
- [ ] After a game: nothing in Discord yet — Home says the game is waiting; the Referee tab shows a number.
- [ ] − / + change a player's points — hover the points to see "Referee" in their breakdown. _(changed in 0.1.65: "Referee change")_
- [ ] Verify & send posts the report and standings — the totals include your change.
- [ ] Void a waiting game, then press Done — nothing about it ever reaches Discord.
- [ ] Verify all sends every waiting game — in order.
- [ ] Waiting games are still there after restarting Among Us
- [ ] Void and Unvoid an older (already sent) game — Discord is told both times.
- [ ] Void the game running now from "This game"
- [ ] Reset all points: type RESET — totals and round count go to zero; the games are in the games folder under "reset …"; Discord unchanged.
- [ ] Games page is gone from the menu

## Replays
- [ ] Nothing is posted to Discord after a game — no replay file in the results channel.
- [ ] Freeplay shows "Replays: press F8" as it opens
- [ ] F8 lists your own games and plays one — on the right map; 1–9 picks.

## Red Alert replays
_OBS connected, Source Record installed._
- [ ] Save replay works on a kill card
- [ ] If it fails, the card says "buffer restarted, try again" — not "no Source Record plugin". The second try works.
- [ ] "no Source Record plugin" only when it really isn't installed

## Red Alert graphics
- [ ] No "Lobby … OFFLINE" bar over the Tournament Points ticker
- [ ] Hide room code is off (Graphics) — turn it on and restart: it stays on.
- [ ] On the player camera the win counter is at the top middle — clear of the host's chat in the corner picture.

## Red Alert Live desk
_1440×900 or bigger window._
- [ ] Everything fits without scrolling the page — the three columns scroll on their own.
- [ ] Host view controls fold under "Host view ▸" on each lobby — open, change lit/vision/eye, close.
- [ ] Caster overlay links are under Settings — Jump to action is in the top strip.

## Player cams
_Player cameras on in Settings; at least two lobbies sending._
- [ ] Player cams list shows every live lobby's players
- [ ] Click a player: that lobby's camera on stream following them — the chip turns red; a green underline is who it's following now.
- [ ] Auto lets it pick
- [ ] Pick two lobbies, press 🎥 Player cams, Send: side by side — three or four: a quad.
- [ ] With several up, clicking a player only changes who that lobby follows — the layout stays.
- [ ] Game ends: back to the whole map by itself — several: back to the same lobbies' whole maps.
- [ ] Outside the followed player's sight is darker — walls block it, like their own screen.
- [ ] Hats, skins and pets keep up with the body — no flicker or lag behind.

## Windows
- [ ] The Button snaps to the left/right half when dragged to an edge — and to a quarter in a corner, and maximises at the top.
- [ ] Red Alert snaps the same way
- [ ] Double-click the title bar still maximises; edges still resize — no white strip at the top.

## New in 0.1.43
_The Button and the mod 0.1.43. A fresh code from the code maker._

### Code maker
- [ ] Impostor rotation is ticked when the page opens
- [ ] Common tasks above 4 (long 15, short 23) snaps back to the limit
- [ ] Roles…: turn on Engineer 1 at 100% — the Rules card lists "Roles on: Engineer 1 at 100%".
- [ ] Tournament host code with a server ID and no bot token is ready — "the host adds their bots in The Button".
- [ ] Preliminary webhook box is called "Private reports channel webhook (yours)"

### Roles and settings lock
_Code with Engineer 1 at 100% and an Engineer vent cooldown typed in; lock on._
- [ ] In the lobby, roles show Engineer 1 at 100% and every other role at 0
- [ ] Change the Engineer's chance or turn on Shapeshifter: put back — one message in your chat.
- [ ] Engineer vent cooldown goes back to the code's number
- [ ] Task bar and Ghosts do tasks are put back too
- [ ] An empty role option (e.g. Scientist battery) is left as you set it
- [ ] Log has no "this game version has no role/option" lines — send them if it does.

### Your Discord (The Button → Settings)
- [ ] Wrong token: red message, "Discord refused that token" — nothing else lost.
- [ ] Bot not invited to the server: "isn't in that server yet"
- [ ] Good token: shows the bot's name and …last 4 — bot goes online in Discord within a few seconds.
- [ ] Restart The Button: the bot comes back online by itself
- [ ] Remove a bot, Save: it goes offline (or the next one takes over)
- [ ] Bots saved here are used instead of the code's — automute and /link work with them.
- [ ] Public report channel: bad URL refused; good one shows its webhook's name

### Preliminaries
_Preliminary code with your private reports webhook; host has a public channel set in The Button._
- [ ] After a game: your private channel gets the report with its data file, then the lobby's Count
- [ ] Next game: the old Count is gone and the new one is at the bottom — numbers include both games.
- [ ] Count shows impostor % and crew % of points, per-player averages, wins and games
- [ ] Public channel gets the report and the lobby's standings — no data file, no Count. _(changed in 0.1.51: results and timeline only)_
- [ ] Void a sent game: both channels are told; the Count updates.
- [ ] Scheduled job (Actions → Preliminary leaderboards → Run): an "all preliminary games" Count next to the leaderboard — one line per lobby.

### Submit (was Verify)
- [ ] Referee tab says Submit points / Submit all — and the in-game message says "press Submit".
- [ ] Submitting sends the report and standings — reply says "Submitted".

## New in 0.1.44
_A new code from the code maker (Task bar (players) is "In meetings" by default)._
- [ ] Players' task bar only fills in meetings
- [ ] ~~Your own screen as host shows the real task bar~~ — changed in 0.1.46: as a normal player you see what everyone sees.
- [ ] As the referee, the small live task bar still shows
- [ ] Back in the lobby, the task bar is its normal size again

## New in 0.1.45
- [ ] Your Discord: type a bot token, then click into the webhook box — the token is still there when you press Save and check.
- [ ] Save Your Discord (or a new setup code) during a game: chat says "used as soon as this game ends" — after the game, the new bots/channels are in use.

## New in 0.1.46
- [ ] Hosting as a normal player: your task bar only fills in meetings, like everyone else's
- [ ] As the referee: the small live task bar still shows
- [ ] Preliminary code with the Red Alert channel and a bot: the lobby shows on Red Alert's Live desk
- [ ] Same code: "Send my game to the caster" works and the video shows in Red Alert
- [ ] Same code: reports still go to the private reports channel with the data file and the Count

## New in 0.1.47
- [ ] Task bonus and reads are exact: a crewmate with 7 of 13 task effort gets "Tasks 54% +1.62", not +1.5
- [ ] Died first shows 90% of the crew average exactly (e.g. +3.87, not +4)
- [ ] An old code made before 0.1.47 (it had "round to halves") also scores exactly
- [ ] Code maker and Points Lab no longer have a rounding box

## New in 0.1.48
_A new code from the code maker (old codes keep the value they were made with)._
- [ ] A crewmate who finishes every task gets "Completed tasks +1" in their breakdown — on top of "Tasks 100% +3".
- [ ] All tasks + task win = +9 before votes and reads

## New in 0.1.49 — crew scoring
_A new code from the code maker (old codes keep their old values; new rules use the new defaults)._
- [ ] Killed crewmate: "Got killed −0.25"
- [ ] Crewmate voted out: "Voted out −1"
- [ ] Crew lose with you alive: "Alive at the loss (N rounds)" = −0.25 × (meetings + 1), never more than −4
- [ ] "Voted in X/Y meetings" — up to +1; skipping counts as not voting
- [ ] "Votes right X/Y (Z%)" — up to +2; 0 right = −2
- [ ] Tasks under 25% go negative (0 tasks = −2); 25% and up as before
- [ ] No "Reads" line any more
- [ ] Points Lab shows the new rules; the code maker's Point values… lists them

## New in 0.1.50 — referee amounts
- [ ] Referee tab: type 0.25 next to a player and press + → their points go up by exactly 0.25 ("Referee +0.25") _(changed in 0.1.65: "Referee change +0.25")_
- [ ] Type 1.5 and press − → down by 1.5
- [ ] Empty box + → one point, as before
- [ ] 1.234 (4 significant figures) or 0 is refused with a message, and nothing changes
- [ ] Referee tab → Results channel (administration code): − / + buttons post !adjust with the sign

## New in 0.1.51 — public channel
- [ ] Public channel: each game is one post with two parts, the results (who won and how, map, length, each player's role, result and total) and the timeline
- [ ] Public channel never gets: points breakdown, MVP, meetings/vote counts, wrong-settings warning, lobby code, standings, the Count or the data file
- [ ] Private channel after each game: report with data file, then the lobby's standings, then the Count at the bottom
- [ ] A void after Submit: public gets the game's results again marked VOID; private gets the note, standings and Count

## New in 0.1.52 — cleanup (nothing should look or work differently)
_Unused code was removed. If anything below misbehaves, it's likely from the cleanup._
- [ ] The Button: every page looks as before (Home, Referee, Settings, the administration Organiser card)
- [ ] The Button ↔ mod: Home shows the lobby; Next round / Void / Submit still answer
- [ ] Ghost/spectator overlay in game still draws
- [ ] Red Alert: Live desk, Multiview (no "?" on it now), Graphics, Montages, Lobby health, sponsors and the overlay look as before
- [ ] An old setup code made before 0.1.49 still loads

## New in 0.1.53 — chat, talking lights, automute, ghost zoom
- [ ] Lobby chat: no "Auto-linked…" / "Linked…" lines when players /link or match by name (they show on The Button's Home)
- [ ] Meeting: a talking player's banner gets a coloured outline that fits the banner exactly; no speaker icon
- [ ] Lobby: the speaker by a talking player's name follows them when they walk around, also after a game ends and everyone is back in the lobby
- [ ] Kill someone during tasks: their Discord icons don't change (still muted + deafened like the living) until the meeting
- [ ] At the meeting the dead are muted (can hear); after it, the dead talk among themselves
- [ ] Report a body: Discord voices open as the report animation plays, not after it
- [ ] Automute feels quicker: 10 players switch within about a second with one bot
- [ ] Host playing (not referee), dead, zoomed out: open a task — it's full size and works; close it and the zoom comes back. Same for the map

## New in 0.1.54 — referee and automute timing
- [ ] Referee mode: a few seconds into the game, other players no longer see the referee standing on the map (ask a player, or check a second PC)
- [ ] Referee mode: the chat button shows during tasks; Enter or T also opens the chat
- [ ] Game start: alive players are muted and deafened straight away (no 3-second wait)
- [ ] Meeting end: alive players are muted again as soon as the ejection screen finishes

## New in 0.1.55 — leaderboard, chat, zoom
- [ ] Referee page: press Submit on a game → within a few seconds the leaderboard includes it (also after Void / Unvoid and point changes after Submit)
- [ ] Host just playing (not referee), dead: no zoom at all (scroll and +/− do nothing)
- [ ] Referee mode: zoom still works; typing "-" or scrolling in the chat doesn't zoom
- [ ] Referee mode: open the chat, send, close it — it closes normally every time

## New in 0.1.56 (Red Alert 0.3.10)
- [ ] Meeting: open the map while someone talks — the coloured outline is hidden under/behind the map
- [ ] Meeting: the outline fits the banner, corners included (nothing sticks out at the top corners)
- [ ] Meeting (referee): the mini chat sits in the empty space right of the last row of cards, not at the bottom
- [ ] Referee during play: a list down the left edge — "Alive N/M", each player in their colour; dead crossed out with "dead", leavers "left"; gone in meetings; not in the player camera
- [ ] Referee page: Submit a game → it's on the leaderboard at once (Discord catches up a few seconds later)
- [ ] Red Alert → Settings: "Administration code" card — paste another code and Change (or Lock); works after the first code

## New in 0.1.57 (Red Alert 0.3.11)
- [ ] The Button: the top bar's update button updates The Button and the mod together
- [ ] Sending video on, then close the VDO.Ninja tab → within ~15 s The Button shows Not sending video (and says why)
- [ ] Sending video on, then press Stop sharing in VDO.Ninja → within ~20 s it turns off
- [ ] Home shows "Live data posted N s ago", or "Live data not posting" with the reason (e.g. the bot can't post in the results channel)
- [ ] Red Alert with a bad admin code / channel: a red "Discord: …" banner on Live desk, Lobby health and Lobby voice, and a red dot by Live desk. The message says what to fix (invalid token, missing channel permissions, channel doesn't exist, Message Content Intent off)
- [ ] Red Alert writes red-alert.log next to its settings (problems and recoveries are logged)
- [ ] Red Alert → Settings → paste a host's setup code → Check: says Matches, or what's different (channel, server, bot)
- [ ] Player camera works (no "AsyncGPUReadbackRequest" error in the mod log); at about 12 pictures a second

## New in 0.1.58 (Red Alert 0.3.12)
- [ ] Referee mode, a player leaves before the first meeting → the chat says so and how to void; after the first meeting → nothing in the chat (Home shows it)
- [ ] Player cam on stream, a meeting is called → Red Alert goes back to that lobby's whole map; the host's game stops drawing the camera during the meeting
- [ ] Player cam is smoother on stream (about 20 pictures a second while on stream, 3 while only ready; 960×540)
- [ ] Referee ghost doesn't flicker on the host's screen around meetings
- [ ] A player (without the mod) votes for the referee's card → it counts as a skip; the referee is never voted out
- [ ] No more "Show the referee as dead to players" switch; the host isn't banned from the room during referee games
- [ ] Every kill is heard on the host's game, and on stream, whether the whole map or a player cam is on
- [ ] Mini vitals: "Players · N of M alive", red IMP by impostors, hidden during the role intro

## New in 0.1.59 (Red Alert 0.3.13)
- [ ] Votes count again: vote for a player in a referee game → the results show every vote and the right player goes
- [ ] A player without the mod votes for the referee's card → shown as their vote, but counted as a skip (the referee never goes)
- [ ] Mod log at start: "Self-check: every hook is in place." (or which hook was skipped and why)
- [ ] A player leaves mid-game → no NullReferenceException in the log; the referee view and player cam carry on
- [ ] Replays with Source Record 0.4.8: saving a lobby's replay works (found by its file in TT Replays); with the player cam on stream, the camera angle is saved too
- [ ] Player cam on stream, then a meeting / game over / lobby → Red Alert cuts to the whole map; the cam source shows "Player camera paused", not a frozen picture
- [ ] Player cam runs at full rate even with the Live desk not showing (Red Alert decides, not the page)
- [ ] Player cam smoother on stream (about 30 pictures a second, evenly spaced)
- [ ] Host's send page: if the browser is holding the sound back, it says "Click anywhere on this page…"; Red Alert → Lobby voice shows a "Sent" meter
- [ ] Lobby voice heard but silent in OBS while the host sends sound for 20 s → Red Alert reloads that voice source and says so
- [ ] Referee's player list: dark rounded panel like The Button, PLAYERS header with the alive count in green, IMP/DEAD/LEFT tags

## New in 0.1.60
- [ ] Settings lock on: start a game while players join and leave during the countdown → the game starts normally (the lock doesn't send settings during the countdown or as the game starts)

## New in 0.1.61
- [ ] Host with the mod on PC (referee slot on), join from a phone without the mod, start a game, call a meeting → the referee's card is crossed out (dead) on the phone
- [ ] With `[Debug] RefereeRecordSync = true` in BepInEx/config/com.ljbutton.tournamenttracker.cfg: the log shows "Referee record written for sending: IsDead=True…" after the intro and before each meeting, and no "found alive" / "changed to false" warnings
- [ ] The Button → Home → Tools: the Referee ghost slot says players without the mod see a crossed-out card

## New in 0.1.62 (Red Alert 0.3.14)

### The mod (in Among Us)
- [ ] Player camera on stream for a full game with the F10 profiler on → no regular stutter every second or two; the log has no "Player camera: reading the pixels the slower way" line (if it has one, send it: the camera fell back to the old way but kept working)
- [ ] Player camera picture looks as before: right way up, normal colours; the dark area outside the followed player's sight follows them and stops at walls; standing still at a task it stays right, and a door closing changes it within a moment
- [ ] Referee spectator view (vision dim, rings, eye by a crewmate's name) looks and behaves as before; in the F10 summary, OverlayLate and OverlayDim are lower than in 0.1.61
- [ ] Red Alert switches the player cam pick, the game sound (cam/wide) and the vision → no "[Tracker] Spectator view: …" line in the host's chat or in the referee's mini chat at the next meeting; The Button's own buttons (Next round, etc.) still answer in the host's chat
- [ ] Caster picks a player in Red Alert, waits more than 10 s, the player is killed → the player cam stays on them for about 4 s (the kill is seen), then moves on
- [ ] Save a new setup in The Button during a game → after the game the tracker restarts → Red Alert keeps receiving that lobby's events and snapshots straight away (the lobby doesn't go quiet)
- [ ] Live status on, close Among Us from the lobby → the Discord status message changes to "The host closed the lobby"
- [ ] Update, start Among Us, host an online lobby with the referee slot on, start a game → log line "Referee ghost: … owner -2 … host authority (modded host): True"
- [ ] Phone without the mod in that lobby: the referee is never seen on the map; in meetings their card is crossed out (dead); no kick or ban over 3 or more full games
- [ ] Join the modded lobby by code from a phone, and from a console if you can → works; the lobby isn't in the public game list
- [ ] Send `hostauth off` while in a lobby → the reply says the lobby keeps host authority until you leave to the main menu; go to the main menu, host again, start a game → MakeGhost log says "host authority (modded host): False" and Home shows "Host authority is off…"; `hostauth on` and re-host puts it back
- [ ] The app's status (/api/status) has hostAuthority.on and hostAuthority.active (active is true while hosting with it, null in the menus)
- [ ] Normal games with host authority → no "message … came from another player's game" notices on Home (kills, meetings, names, guardian angel shields all work as usual)
- [ ] First meeting of each game: the referee's mini chat starts empty ("Chat shows here."), with no lines from the lobby or the previous game
- [ ] Referee's mini vitals list stays in place down the left edge with the player cam on, and never appears in the player cam picture; it shows up after the intro (and after the Airship spawn pick)
- [ ] Referee's card is taken off the meeting list on the host's screen and on a modded player's game
- [ ] Referee's task bar still fills live during play
- [ ] Player cam on stream on every map → the followed player's footsteps are heard
- [ ] Mod log at start: "Self-check: every hook is in place." (the new version, modded-check and HandleRpc hooks included)

### The Button
- [ ] The Button, Referee tab at the default window (and maximised at 1080p): the page doesn't scroll; Waiting and Games on the left, Points and Results channel on the right; long lists scroll inside their own card
- [ ] Referee tab, very small window (125% scaling, or narrower than 880 px): falls back to one column and the page scrolls; nothing is cut off
- [ ] Referee tab: type a void reason at the top and press Void on a waiting game, on a sent game, and "Void this game": each void carries the reason and the box empties
- [ ] Referee tab: type an amount in a player's box while the game list redraws (submit another game, or wait for a refresh): the box keeps focus and the cursor; + and − still work
- [ ] Referee tab, Points: tabs for this lobby / every lobby / running total; the chosen tab is remembered after a restart; the cut line and green "moves on" rows are kept; hidden columns are in the row tooltip
- [ ] Referee tab: "Reset points…" opens a small box under the top line; Escape or Cancel closes it; typing RESET and pressing Reset all points still resets
- [ ] Referee tab with an administration code: Results channel is folded (open state remembered); the lobby list is filled when the tab opens (without visiting Home or Organiser first); !adjust / void / unvoid still post _(changed in 0.1.65: no any-command box; rounds and lobbies are on Organiser tab → Rounds)_
- [ ] Home → Tools with the new mod: Host authority switch shows and sends "hostauth on/off"; with an older mod the switch doesn't show and nothing breaks
- [ ] Home: referee slot on, host authority on but the lobby was hosted before it was switched on: the checklist shows "Restart hosting from the main menu so host authority takes effect"; after rehosting from the main menu it goes away
- [ ] Home: the Post Leaderboard / Server standings / Lobby message buttons (now at the bottom of Tools) post as before; Home still fits the default window
- [ ] Send my game to the caster: press Open again: the old tab shows "This tab isn't sending any more" and stops sharing; only the new tab sends, and lobby voice isn't choppy
- [ ] Send my game to the caster: switch it Off in The Button: the open tab stops sharing the screen and lobby voice and says so
- [ ] Restart The Button (update) with the send tab open: the tab keeps sending (no "isn't sending" banner)
- [ ] Lobby voice with the send tab behind Among Us or the browser minimised for several minutes: the caster hears smooth Discord and game sound (no half-second gaps)
- [ ] Close the send tab: within about a minute The Button stops capturing Discord and game sound (CPU drops); opening the page again brings the voice back
- [ ] Lobby voice: drag a level slider: the level changes on the caster's side; Wi-Fi blip: voice says "reconnecting" and comes back without a second stream on the caster's side
- [ ] Player camera on, then Among Us closed: the browser doesn't spin; camera pictures still smooth when back on
- [ ] Organiser tab: lobby ages count up every couple of seconds, cards don't flicker; clicking items in the Home checklist and the update pill works on the first click
- [ ] The Button next to Among Us: the starfield still drifts; CPU/GPU use of The Button is lower than before; minimised, it draws nothing
- [ ] Update automatically, with Among Us running and a newer mod out: the mod still updates by itself shortly after Among Us closes

### Discord bots (inside The Button and the mod)
- [ ] Bot gateway: pull the network for ~10 s during a lobby; the bot comes back (resumes) and a /link used during the drop is still answered; the log shows no rapid reconnect loop.
- [ ] Bot with a reset/wrong token: The Button shows "Discord turned down the bot's token…" and stops reconnecting.
- [ ] Talking lights: host sits in voice 30+ min with people talking; The Button's memory stays flat; lights keep working after the host moves channel.
- [ ] Disconnect the bot from the voice channel in Discord: within ~15–30 s it rejoins and talking lights return.
- [ ] Automute: a muted player leaves voice mid-game; the log doesn't show retries every 2 minutes; when they rejoin voice they get the current state straight away (not stuck muted).
- [ ] Automute with 10+ players and a short meeting: right after the meeting ends nobody is left in the meeting's mute state.
- [ ] Crash (The Button only): mid-game with players muted, end The Button in Task Manager, reopen it with Among Us still running: players get the game's state back; after the game everyone is unmuted.
- [ ] Crash (both): mid-game, end Among Us and The Button in Task Manager, reopen only The Button: within ~30 s everyone is unmuted and BepInEx/config/TournamentTracker/button-muted.json is gone.
- [ ] Among Us crash with The Button open: end Among Us mid-game: within ~35 s everyone is unmuted.
- [ ] Close The Button normally with 10 players muted/deafened: all are unmuted.
- [ ] Live status: pull the network while players join, reconnect: the status message catches up within a minute without another change.
- [ ] Live data: same test; the lobby in the Organiser tab / Red Alert shows current data again within a minute.
- [ ] Organiser / Red Alert: with 100+ messages in the results channel since a lobby's last game, that lobby still shows and keeps updating.
- [ ] Round end in a tournament: report, game file, lobby standings and round summary each post once (no duplicates).
- [ ] Results still post through The Button after it is closed and reopened mid-session, and nothing posts twice.

### Stream Deck plugin
## Stream Deck plugin
_Red Alert with the new Stream Deck state, Stream Deck app 6.5+ on the same PC, RedAlert.streamDeckPlugin from the broadcast-v release._
- [ ] Double-click RedAlert.streamDeckPlugin — the Stream Deck app installs "Red Alert" and its actions show under Red Alert, with the Red Alert version.
- [ ] Drag a Lobby key before pairing — it shows SET UP across the top.
- [ ] Paste the port and key from Red Alert (Settings → Stream Deck) into "Red Alert connection" and press Test — it says how many lobbies there are and whether OBS is connected; a wrong key says the key is wrong.
- [ ] Close Red Alert — every key shows OFFLINE within a few seconds and comes back by itself when Red Alert opens again.
- [ ] Lobby keys 1–4 — the one on stream has a red border and PGM, the one in Preview a green border and PVW, phase and crew v impostors match the desk.
- [ ] Sabotage in a lobby — its key shows the system and seconds, flashing, counting down.
- [ ] Lobby key set to Preview, then Take — the lobby goes to Preview (green on the desk too), Take puts it on stream with the transition picked.
- [ ] Lobby key set to "Add it to Preview" on two lobbies, then Take as 2-up — both lobbies go on stream side by side.
- [ ] Player cam keys slot 1–10 (the lobby on stream) — each shows the right crewmate colour and name; a dead player goes dim with DEAD; the followed player has a ring, red while the camera is on stream.
- [ ] Press a player cam key in play — the camera cuts to them (tick). In a meeting — the key shows ARMED and the camera goes on when play resumes, no frozen picture.
- [ ] Player cam key for a lobby whose host isn't sending a camera — NO CAM on the key, and a press shows the alert triangle (no tick).
- [ ] Wide and Cut to cam — back to the whole map, then back to the same player's camera.
- [ ] Toggles (mute all, hold, auto grid, auto intermission, auto switch, standings, alerts) — each shows ON/OFF (MUTED, HELD, PAUSED) matching Red Alert, and switches it when pressed; switching on the desk updates the key within a second.
- [ ] Replay that, Play last, Back to live, Speed — replay plays the on-air lobby's last seconds; the speed key shows 100/50/25%.
- [ ] Break keys and Panic — the break screen goes up; Panic fades to Be right back and mutes lobby voice in one press.
- [ ] Press a key many times fast — only one request is sent at a time, nothing switches twice.
- [ ] No key, settings panel or plugin log ever shows who the impostors are; the plugin log (plugin's logs folder) has paths and answers, never the key.
- [ ] Stream Deck +: Preview dial turn picks the lobby (strip shows it), push takes; Player dial turn arms the next living player, push cuts to them, tap goes wide; Replay dial scrubs by frames slowly and seconds quickly, push plays/pauses.
- [ ] Custom path key with gfx/skip and with voice/listen/2 — each works and ticks.
- [ ] Panic key, then look at it — it says UNDO PANIC (amber border) while the panic is on; press it — back to what was on, voice and alerts back, and the key says PANIC again. Press it again straight after — nothing else is undone (no tick).
- [ ] Undo key — lit while a panic is on and undoes it; with no panic it takes back the last switch (or an auto switch away from a dropped lobby). With OBS closed (no simulation) it shows the triangle and nothing moves.
- [ ] Custom path key's list: "Undo (the panic, an auto switch, or the last switch)".
- [ ] Preview dial: turn to a lobby that has just gone offline, let it rest (the strip shows the triangle), push straight after — nothing goes on stream (the old Preview isn't taken); turn again and push — that lobby goes on.

### Red Alert: switching engine, Stream Deck, replays
- [ ] Red Alert: Stream Deck `/deck/lobby/1?key=...` puts lobby 1 on; a wrong key answers 401, an unknown lobby 404, OBS closed (no simulation) 503; pressing the same key twice fast shows "ignored repeat"
- [ ] Red Alert: lobby numbers on the Stream Deck stay the same after closing and reopening Red Alert (lobby-numbers.json); a lobby called "Lobby 3" is number 3 when free
- [ ] Red Alert: Stream Deck pvw/lobby + pvw/add builds Preview (green tally on the desk and on the keys), Take puts it on stream; take?t=cut cuts with no swoosh, t=fade fades
- [ ] Red Alert: player camera key while the lobby is in a meeting answers 202 "armed" and the camera goes on by itself when play resumes; pressing it again disarms; with cameras off it is refused (409)
- [ ] Red Alert: camera follow (cam/1/slot3, colour, next/prev) reaches the host's game within a couple of seconds and is sent again until the host has it
- [ ] Red Alert: /deck/state?key=... waits and answers as soon as something changes (rev goes up); no impostor flags in it unless &imp=1; /deck/img keys draw lobby tally/phase and player heads
- [ ] Red Alert: Hold on: auto grid, auto intermission, the camera going back to the map, auto switch off a dropped lobby, break end and armed cameras all wait, and the desk says what was held back; the caster's own switches still go _(changed in Red Alert 0.3.17: auto grid only fills Preview, and Hold stops that)_
- [ ] Red Alert: Panic fades to be-right-back, mutes lobby voice, clears graphics, pauses alerts and holds; Undo puts the previous view, voice and alerts back
- [ ] Red Alert: Panic, then put it right by hand (another lobby, hold off, voice back on). Later press BRB, Hold and Mute all yourself → the desk doesn't show "Undo panic"; Undo takes back the BRB (to the lobby you were on, not the one before the panic) and the voice stays muted
- [ ] Red Alert: Panic, then OBS closes; the Stream Deck's Undo → refused (OBS isn't connected), still on be right back and held; reconnect OBS → the desk still says "Undo panic" and Undo puts the lobby back in OBS. (Or, with OBS still closed, the desk's Back undoes it and says OBS didn't switch)
- [ ] Red Alert: arm a lobby's camera in a meeting, then put another lobby on stream (or intermission, or a sponsor break) by hand → a toast says the armed camera was cancelled; when the meeting ends the stream stays where you put it
- [ ] Red Alert: arm a camera, auto intermission comes up → the camera doesn't cut over intermission; when intermission ends by itself, the camera goes on
- [ ] Red Alert: a player camera on stream, Intermission, then End the break (desk or `break/off?t=cut`) → back to that player camera, with a straight cut
- [ ] Red Alert: Settings → Stream Deck → Lobby numbers: type 1 for the lobby that's 3 → it's 1, the old 1 is 3, and the Stream Deck's Lobby 1 key is that lobby; Renumber online lobbies 1…N → the lobbies sending now are 1, 2, 3…; both survive a restart
- [ ] Red Alert: Replay "that" plays the on-air lobby's last ~8 s with no card; "last" plays the newest clip again; slow motion cycles 50%/25%/100% (quiet while slowed)
- [ ] Red Alert: OBS stream stopped/reconnecting, not recording, dropped frames and low disk show as warnings on the desk within a few seconds
- [ ] Red Alert: /app/admin/program.jpg shows OBS's program picture (blank 204 with OBS closed)
- [ ] Red Alert: many quick Stream Deck presses in a row end with OBS showing exactly what the desk shows, with the right lobby's sound
- [ ] Red Alert: Mute all (hotkey or deck) silences lobby voice in OBS immediately
- [ ] Red Alert: minimised or covered by an OBS projector, host commands and the desk keep running at full speed
- [ ] Red Alert: an error in the window is written to red-alert.log and Red Alert keeps running; past 1 MB the log moves to red-alert.log.1
- [ ] Red Alert: old replay clip files (TT *) are cleared by size/age/free disk; clips used by a montage or the archive, or playing, stay
- [ ] Red Alert: if port 8768 is taken, Red Alert uses 8769–8771 and the Stream Deck page warns about the port change

### Red Alert: one-screen Live desk
- [ ] Red Alert Live desk at 1920x1080 (and 1536x864 / 125% scaling): everything on one screen, no page scroll; only the three bottom columns (and a full action panel) scroll
- [ ] First start of Red Alert 0.3.14 (no window.txt in %LOCALAPPDATA%\RedAlert): the window opens maximised. Make it smaller, move it, close and reopen → it comes back where and as big as it was (maximised if it was)
- [ ] Live desk in a 1180x780 window (and 1024x700): still one screen; the side menu stays the narrow icon rail on the left; Panic, Hold and every break button are visible in the action panel; Take, Clear and Back are visible. Only below about 1000 px wide (or 620 high) it becomes one long page, still with the icon rail
- [ ] Status bar: PROGRAM/BREAK/OFF AIR tag with OBS STREAMING/REC times; AUDIO pill shows who is heard, click mutes all (pill flashes red, click again unmutes); OBS, LOBBIES, REPLAYS, AUTO/HOLD pills; shot timer
- [ ] Warning line shows the worst warning and "+N"; click opens the list with its Fix/Undo buttons; Esc or a click elsewhere closes it; disconnecting OBS shows "OBS not connected"
- [ ] Click a lobby tile: green PVW border and it appears in the PREVIEW box; Ctrl+click a second: 2-up in Preview; TAKE (or Enter) puts it on (red PGM borders), Preview empties
- [ ] Double-click a tile: that lobby full screen at once; hover buttons PVW / +PVW / CUT / camera work
- [ ] Right-click a tile: menu with Preview / Add / On stream now / Listen, host view (lit, vision, eye, whose-vision picker stays open while choosing) and player cards
- [ ] Keys: 1-9 Preview, Shift+1-9 cut, Enter Take, Esc clear, H hold on/off, P twice panic (P once shows "Sure? Panic"; while a replay is up P is its other angle), N action into Preview, G grid into Preview
- [ ] Cut / Swoosh / Fade choice is used by Take, cuts and camera cuts (and remembered after a restart)
- [ ] PROGRAM box shows OBS's own picture (about 2 per second) with OBS connected; a drawing of the layout without OBS
- [ ] Tiles: phase chip (MEETING yellow), crew v imps, tasks, flashing sabotage countdown, tier glow, health dot, camera / ARMED badge, red play badge for a ready replay (click plays it), voice meter
- [ ] Player cams: tabs per lobby (default the Preview lobby, else Program), chips in colour order with heads; click = camera on them (ARMED in a meeting), Shift+click = only follow; Prev/Next, Cut to cam, Wide; one line per other lobby
- [ ] Replay that, Play last and ready-replay buttons; replay controls in the action panel with slow motion 100/50/25%; the cursor moves while playing
- [ ] After-game prompt: buttons stay clickable while the countdown ticks; sponsor read Done/Snooze; Sponsor break button counts down
- [ ] Panic: be right back, AUDIO "All muted", PANIC pill, "Undo panic" puts everything back
- [ ] Hold blocks auto grid / intermission; the held-back switch shows as a toast and a warning with Release hold _(changed in Red Alert 0.3.17: auto grid only fills Preview)_
- [ ] Clicking cards, Save replay and player-card buttons always works (no lost clicks while polls run); tooltips stay up
- [ ] Other pages (Health, Montages, Graphics, Standings, Sponsors, Twitch, OBS, Lobby voice, Players, Settings) still work; lobby voice Discord/Among Us/Sent meters move; Twitch clip picker keeps the chosen clip
- [ ] Lobby pictures don't reconnect when another lobby starts sending
- [ ] Settings → Stream Deck: link builder (lobby, camera slot/colour, cut or arm, switch) gives a working link; Copy all links; port shown with a warning if not 8768; Copy key; Download plugin and How to set it up links open
- [ ] Overlay in OBS: switching layouts glides graphics smoothly; sabotage alarm pulses steadily; sponsor break video stops (no sound) when the break ends and restarts on the next break; win counter in the right place in intermission after a player cam; intermission storyline changes without the whole screen flickering; room-code cover fully hides the code
- [ ] REPLAY tag over replays shows the right title and sponsor and doesn't flicker


## New in 0.1.63 (Red Alert 0.3.15)

### The mod (in Among Us)
- [ ] Mod log at start: "Among Us 2026.x.y (network version …, Application.version "…")", then "Safe mode off (Among Us 2026.x.y, mod built for 2026.x.y)." — "built for" matches the release's BUILT-AGAINST.txt
- [ ] Mod log at start still says "Self-check: every hook is in place." (now includes DisconnectWatch)
- [ ] /api/status has safeMode {on: false, reason: null, gameVersion, builtFor, overridden: false}, and lastDisconnect is null on a fresh install
- [ ] Hosting a lobby, turn Wi-Fi off for about 15 s → log line "Disconnected: Error (network error) — …" (or ClientTimeout / Custom with "It said: …"), ending "(in the lobby ABCDEF, hosting, host authority on)"; the next line says what the mod sent in the 10 s before (or "sent nothing"); The Button's activity log shows the line; status lastDisconnect has at (UTC, ends in Z), reason and inGame: false
- [ ] Same during a game → "(during a game ABCDEF, …)" and lastDisconnect.inGame is true
- [ ] BepInEx/config/TournamentTracker/disconnects.json lists the disconnects, newest last, never more than 20; it survives restarting Among Us
- [ ] Leaving a lobby yourself (Leave button) or closing Among Us adds no "Disconnected:" line
- [ ] If you ever get kicked or banned while hosting: send the "Disconnected: …" line and the "The mod sent in the 10 s before …" line that follows it (role messages, referee record syncs, RpcSnapTo, settings sync, chat)
- [ ] Safe mode ON test: run the build workflow with game_libs_version set to an AmongUs.GameLibs.Steam older than your Among Us, install that DLL → log "Safe mode ON (Among Us X, mod built for Y): Among Us X is newer than this mod (built for Y): risky features are off until an update. …"; status safeMode.on is true with that reason
- [ ] In safe mode, with the referee slot, rotation and settings lock on: Home says (in the lobby) "Safe mode is on, so the referee ghost slot is off this time …" and the same for the impostor rotation; starting a game, the game's own roles stand (log "Roles: safe mode is on …"), with no "Referee ghost:" lines; changed settings aren't put back (Home: "… the settings lock is off …"); the host's screen shows no red impostor names or spectator vision; hostAuthority.active is false while hosting (no +25)
- [ ] In safe mode, tracking, stats, the caster feed, the overlay, the Discord live status and automute work as usual
- [ ] In safe mode, send `safemode off` from The Button → reply "Safe mode OFF for Among Us X: …"; status shows on: false, overridden: true; leave to the main menu and host again → host authority is active and the referee slot works; restart Among Us → still off; `safemode on` → back on, and the reply gives the reason
- [ ] `safemode off` when safe mode isn't on → "Safe mode is already off: everything runs normally on Among Us X." (nothing saved)
- [ ] Settings → Your connection → **Check my connection**: the progress bar moves and the check ends in about 15 s. It shows upload in Mbps, Discord and VDO.Ninja ms with jitter, "While uploading" ms, data sent (no more than 30 MB) and advice. **Stop** stops it and says "Check stopped." The last result stays.
- [ ] Home host checklist (with Send my game available): the chip reads "Connection not checked" before a check, then "Upload N Mbps". It is green at 8 Mbps or more, amber under 8, red under 5. Clicking it opens Settings.
- [ ] On a limited connection (router limit, or a phone hotspot): under 8 Mbps is amber with wired / pause uploads / Lower quality advice. Under 5 is red and adds "Tell the organiser". With speed.cloudflare.com blocked, the result says "Upload: couldn't be measured" (no made-up number).
- [ ] Send my game: the toast and Home say to pick the Among Us window. The send page opens with "The Among Us window (recommended)" ticked. The browser's picker opens on the **Window** tab, and the send page tab itself isn't offered. Pick Among Us: the caster sees the game, and the game sound arrives through the lobby voice.
- [ ] Send page → **Entire screen**: VDO.Ninja reloads and the picker opens on Entire screen, with the Share system audio box. Instructions say to leave it unticked with lobby voice on, or tick it with lobby voice off.
- [ ] Send page with **Send lobby voice** off and window mode: the instructions say a window share has no sound, and to turn lobby voice on or use Entire screen with system audio.
- [ ] Send page → **Pick again**: VDO.Ninja asks again what to share, and the caster's picture comes back after picking.
- [ ] **Lower quality** (send page tick box or Settings → Your connection): the caster's picture drops to 720p at 30 fps. Both places show the same setting. Restart The Button: the share choice and quality are kept.
- [ ] On a slow upload the send page footer shows "Your upload can't keep up, so the picture is lowered…".
- [ ] Settings → Help → **Save support bundle**: TournamentTracker-support-<date>.zip appears on the Desktop and Explorer opens there. README.txt lists every file. The bundle has the-button.log, settings.json, mod/LogOutput.log, the mod cfg, mod/data/state-*.json, games-list.txt, among-us/Player.log, versions.txt and summary.txt. Search all files: no bot token, no discord.com/api/webhooks link, no TT1- code, no password= value, no AppToken. setup-code.txt and host-discord.json are not in it.
- [ ] Mod in safe mode (game version it wasn't built for): Home shows a red "Safe mode: <reason>" card with the versions. **Run normally anyway** asks for a second click, then sends `safemode off`. The card becomes "Running normally on a game version the mod wasn't built for" with **Back to automatic**, which sends `safemode on`.
- [ ] Disconnect (pull the network cable in a lobby, reconnect): Home shows "You were disconnected: the connection to the Among Us server timed out" with time and advice. It is red, with a Referee tab reminder, if it happened during a game. ✕ hides it. It is gone after an hour. Leaving to the main menu on purpose shows no card.
- [ ] With an older mod (0.1.62, without safeMode/lastDisconnect): Home shows no new cards and everything else works as before.

### Red Alert: support bundle and dry run
- [ ] Settings → Help → **Save support bundle** → a toast names `RedAlert-support-<date>-<time>.zip`, the file is on the Desktop and the Desktop folder opens; a second save in the same minute makes `…-2.zip`
- [ ] The zip's README.txt lists every file with what it is, and what was left out or not there
- [ ] The zip has red-alert.log (and red-alert.log.1 when there is one), settings.json, obs.json, caster-priority.json, broadcast.json, lobby-numbers.json, health.json, twitch.json, versions.txt, summary.txt, state.json and stream-deck.txt; it has no twitch-token.json, roster.csv or sponsors.json
- [ ] With OBS running: obs/logs/… in the zip is today's session (the newest file in %APPDATA%\obs-studio\logs), even though OBS still has it open; after an OBS crash, obs/crashes/… is the newest crash report
- [ ] versions.txt: Red Alert, Windows, the window (WebView2), OBS and obs-websocket versions, Source Record found / not installed, ffmpeg, Twitch
- [ ] summary.txt: the warnings the Live desk showed at that moment, Red Alert's known problems, on stream / Preview / hold / panic, each lobby with its number and health, OBS (scene, streaming and recording, setup check), lobby voice (mute all, who's heard, each host's Discord / Among Us / Sent levels), lobby health, and the latest WARN/ERROR lines of red-alert.log
- [ ] Search every file in the zip for the administration code (TT1-…), each bot token, the webhook, the OBS password, the Stream Deck key (Settings → Stream Deck → Copy key), the stream key, the Twitch tokens and a host's VDO.Ninja password (password=… in a lobby's OBS source link) → none found; [removed] in their place
- [ ] state.json has no desk cards, and no players or impostors for any lobby
- [ ] Red Alert locked (no administration code): the lock screen's **Save support bundle** still saves a zip with the log and settings; summary.txt starts with LOCKED
- [ ] Settings → Help → **Dry-run checklist** opens docs/dry-run.md on GitHub in the browser (not inside Red Alert); the card's **?** opens its explanation
- [ ] Run docs/dry-run.md once with the real hosts and the caster before the event; note any step that doesn't match what you see

## New in 0.1.64 (Red Alert 0.3.16)
_Nothing new to see: this release shrinks the code and merges logic both apps had their own copy of. Everything should look and work exactly as in 0.1.63, so these checks cover the parts that now share one copy._

### Both apps
- [ ] The Button and Red Alert look the same as before: fonts, colours, buttons, cards, the toast in the corner
- [ ] Title bar in both: drag to move, double-click to maximise and back, the minimise / maximise / close buttons work
- [ ] Starting a second copy of either app brings the open one to the front instead
- [ ] Links that open the browser (help, GitHub, the dry-run checklist) still open it
- [ ] Updates: on 0.1.64 The Button's top bar offers no update (it looks every 20 minutes); in Red Alert, Settings → Updates → Check now finds nothing newer on 0.3.16. The Button never offers a Red Alert release, and Red Alert never offers a Button release
- [ ] Logs: the-button.log starts again past 1 MB; red-alert.log keeps the one before as red-alert.log.1
- [ ] Save support bundle in both: same files as in 0.1.63, and still nothing secret in them

### The Button and the mod
- [ ] The Button connects to the mod (Home shows the lobby); commands from The Button still get their answers
- [ ] Settings switches (mod auto-update, mod console, The Button auto-update) stay as set after restarting The Button
- [ ] Stream overlay page and crewmate heads load in OBS
- [ ] Discord: live status edits, game reports with the data file, the private count, standings, round start posts, prelim posts, /link and /unlink, void and verify — all worded as before
- [ ] Automute and who's-talking lights still follow Discord voice
- [ ] Mod on screen: the meeting mini chat, mini vitals, the replay theatre's info line, the talking speaker icon and the spectator eyes look as before; no new slow frames in the F10 profiler

### Red Alert
- [ ] Stream Deck keeps working without re-pairing (its key is unchanged)
- [ ] Preview / Take, quad and grid, player cams, Hold, Panic / Undo, intermission, BRB
- [ ] Replay that / Play last: the replay layer shows on top in OBS, and the swoosh too
- [ ] Editing roster.csv or sponsors.json while Red Alert runs: the change shows within a few seconds
- [ ] Lobby numbers stay the same after a restart

## New in 0.1.65 (Red Alert 0.3.17)
_Every host, referee and the organiser on 0.1.65: a mod from before it still acts on commands typed in Discord. Red Alert 0.3.17 on the caster's PC._

### Safe saving
_A crash in the middle of a save is hard to make on purpose, so these check that saving still works and that a damaged file is kept._
- [ ] A normal session: play and Submit games, /link someone, press Next round, void and unvoid a game. Restart Among Us and The Button → the games, the waiting games, the links and the round are all still there
- [ ] After the session: no `.tmp` file left in BepInEx/config/TournamentTracker (or its games folders), %LOCALAPPDATA%\TheButton or %LOCALAPPDATA%\RedAlert
- [ ] Damaged file: close Among Us, open BepInEx/config/TournamentTracker/links.json in Notepad, delete its second half and save. Start Among Us → links.json.corrupt-<time> appears next to it with the broken text; the mod's log says "links.json couldn't be read (…). A copy is kept as links.json.corrupt-…. Starting with no links."
- [ ] Within about 10 s The Button's Home shows "A damaged file was set aside: The mod's links.json couldn't be read (…). A copy is kept next to it as links.json.corrupt-…"; ✕ hides it, and it stays hidden after restarting The Button
- [ ] Put links.json right again (copy the good part back from the .corrupt copy), restart Among Us → the links are back. Note whether Settings → Help → **Backup folder** has the good links.json or the broken one (the backup copies the newest file)
- [ ] The Button's own settings: close The Button, break %LOCALAPPDATA%\TheButton\app.json the same way, open The Button → app.json.corrupt-<time> next to it, the-button.log says so, the setup code comes back from the Among Us folder, and Home shows the damaged-file notice for "The Button's app.json"
- [ ] Red Alert: Graphics → **Edit theme**, make a mistake in broadcast.json (`tru` for `true`) and save → the Graphics page says "broadcast.json couldn't be read (…); using the last good settings." and the look doesn't change; switch a graphic on or off → broadcast.json.corrupt-<time> holds your edit, and broadcast.json has the last good settings with the switch

### The Button's backup of the mod's data
- [ ] After a game is saved (or submitted): within about 10 s %LOCALAPPDATA%\TheButton\backup has games/<tournament>/game-….json, the replay, links.json, state-<tournament>.json and stats-<tournament>.json
- [ ] The backup has no setup-code.txt, host-discord.json, control.json or other connection file
- [ ] Settings → Help: "The Button keeps a copy of the mod's games, links and rounds… It holds N files, last copied at <time>."; **Backup folder** opens the folder
- [ ] Reinstall and restore: close Among Us; move BepInEx/config/TournamentTracker/games to the Desktop (or uninstall Among Us, install it again and let The Button install the mod) → Home shows "The backup has N games that aren't in Among Us's folder" with **Restore** and **Forget**
- [ ] Press **Restore** with Among Us closed → "Put back from the backup: N games, … and the points totals."; start Among Us → the Referee tab has the games, the points and the round again, and waiting games still wait for Submit
- [ ] **Restore** with Among Us running → "Close Among Us first: the mod would save over what's put back." and nothing changes
- [ ] Settings → Help → **Restore backup** when nothing is missing → "Nothing to put back: the mod's folder already has everything in the backup, as new or newer."
- [ ] **Forget** asks "Sure? Click again"; the second click → the notice goes and the backup no longer has those games (put the games folder back from the Desktop afterwards)
- [ ] Save support bundle → summary.txt has a "Backup (The Button's copy of the mod's games, links and rounds): N files, last copied …" line (and a "Damaged file:" line after the test above); no file from the backup folder is in the zip

### Updates checked against the release
- [ ] The Button: with an older version installed, the top bar's **Update** still downloads and installs the new one ("The Button vX is ready · Restart"); the-button.log says "Downloaded TheButton.exe (N bytes): the size and SHA-256 match the release."
- [ ] The mod: Settings → **Reinstall the mod** (Among Us closed) still installs; the log has the same "match the release" line for the bundle
- [ ] Red Alert: Settings → Updates → **Update** (when a newer Red Alert is out) installs, and **Restart Red Alert** opens the new one; red-alert.log has the "match the release" line

### Points and round changes only from The Button
- [ ] Type `!adjust LJ red -2 test`, `!void LJ test`, `!unvoid LJ`, `!lobbies 2`, `!start 2` and `!resetleaderboard` yourself in the private results channel → no ✅ or ❓, no answer from any bot, no lobby changes round, and the standings (Organiser tab, Red Alert, the next standings posted) don't change. Delete them afterwards
- [ ] Preliminary: a `!void` typed in your private reports channel doesn't change the scheduled job's leaderboard (Actions → Preliminary leaderboards → Run); the host's **Void** in their Referee tab does
- [ ] Settings → Tournament → Lead lobby says the lead lobby "builds the next round's lobbies and pings the players…"; **Take over** posts "LJ carries out the round changes from The Button's Organiser tab now."
- [ ] A bot without its Message Content Intent → the top bar says "Round changes not heard"

### Referee tab → Results channel (administration code)
- [ ] The card has Lobby, Game (number), Player, Points, − / +, Reason, the void row, and "Rounds and lobbies: the Organiser tab."; there's no box to type a command
- [ ] Pick LJ, type 3 in Game, red, 0.5 and a reason, press + → the channel gets `!adjust LJ-3 red +0.5 <reason>` from the administration bot; LJ's bot ticks it when it next posts standings; the player's breakdown in game LJ-3 shows "Referee: <reason> +0.5"
- [ ] Same with Game empty → it lands on the game LJ is playing (or played last)
- [ ] No reason → the breakdown says "Referee change"
- [ ] A player name ending in a number ("Bob 2") with a points amount → refused ("… can't be told apart from the points: use the player's colour."), nothing posted
- [ ] Void and Unvoid with `LJ-3` and a reason → `!void LJ-3 <reason>` and `!unvoid LJ-3` posted; LJ reposts the game each time and ticks the message

### Organiser tab → Rounds (administration code)
- [ ] The Rounds card shows the next round in its Round box (current round + 1)
- [ ] **Build its lobbies** for round 2 → the lead lobby posts round 2's lobbies and alternates, ending "Change them in The Button's Organiser tab (Rounds), then start round 2 there."; for round 1 → "Lobbies are built for round 2 or later, from the round before." and nothing is posted
- [ ] **Move there**, **Add there**, **Drop**, **Swap** and **Set host** each post their change; the lead lobby posts the lobbies again with it (Drop lists the next alternates); the Player boxes suggest this round's players
- [ ] A change before any lobbies were built → the lead lobby answers "No lobbies to change yet: build them first in The Button's Organiser tab." with ❓
- [ ] **Start round** once → it says "Sure? Start round 2" (back to Start round after 5 s, nothing posted); click again → `!start 2 in 10` (with 10 in "starts in") is posted
- [ ] → "**Round 2 starts in 10 minutes!**" with each lobby's players pinged; every lobby's Round chip shows round 2 (a lobby mid-game: its host's chat says "Round 2 was started by the organiser: it begins after this game."); each lobby posts where to go; 10 minutes later "Round 2 is starting now!"
- [ ] Minutes empty → "Round N is starting now!"
- [ ] **Start over…** → "Start every lobby's combined standings over?" with Reset and Cancel; **Reset** → the administration bot posts the reset message, the toast says "The standings start over from now. Deleting the reset message in the results channel undoes it.", the combined standings start over; delete the message → they come back

### Organiser PC (no Among Us)
_A PC without Among Us, or with Among Us's folder renamed for the test._
- [ ] Paste the administration code into Settings → Setup code → "Organiser view unlocked for …" (not "Find Among Us first.")
- [ ] Home shows "Organiser PC" with "Among Us isn't needed here: …" and **Open the Organiser tab**, which opens it
- [ ] The top bar shows no Among Us, mod or setup code warning; Settings → Mod and updates says "Organiser PC: Among Us isn't needed here."
- [ ] The Referee tab's Results channel card is open the first time (fold it, restart: it stays folded); adjust and void post
- [ ] A host's setup code pasted there → "Find Among Us first."; no Among Us folder is made anywhere
- [ ] Save support bundle → summary.txt says "organiser PC (no Among Us here, and none needed)"

### Red Alert: auto grid fills Preview
_Auto grid on (Live desk → Auto → Grid), two or more lobbies, OBS connected._
- [ ] Every lobby between games or in a meeting → the grid goes into Preview with "All lobbies quiet: Grid is in Preview. Take to show it."; nothing switches on stream until Take (Enter, TAKE or the Stream Deck's Take)
- [ ] Grid taken, then a game starts → what was on before (or the top lobby) goes into Preview: "A game started: … is in Preview. Take to go back."; nothing switches until Take
- [ ] A lobby you put in Preview yourself is never replaced; clear auto grid's suggestion (Esc) → it isn't put back until something changes
- [ ] The suggestion goes again when a game starts before you Take the grid, when auto grid is switched off, and when a break goes on
- [ ] **Hold** on → no suggestion in Preview, and the desk says "Held back: Auto grid"
- [ ] Stream Deck: the suggested lobbies' keys show green PVW like any Preview; the Take key puts the grid on

### Installers
- [ ] Put Install-TheButton.bat and Install-RedAlert.bat in a folder with an apostrophe in its name (Downloads\Jo's stuff) and double-click each → each installs, makes its Start menu and desktop shortcuts, and opens

### Messages
- [ ] F9 in Among Us (hosting, automute set up) → the chat says "Everyone unmuted; automute is OFF. Press Automute on The Button's Home to turn it back on."; **Unmute all** on Home answers the same
- [ ] **Referee** on Home with automute off → "Automute is off: press Automute on The Button's Home first."; with automute on, the answer ends "Press Referee on The Button's Home to end it."
- [ ] **Send my game to the caster** on → the answer says to press the share button, open the Window tab and pick Among Us (not the Entire screen)
- [ ] Home → Tools → **Stream overlay** on → the answer ends "Size 360×720."
- [ ] Automute card on a code with no bot → "Off: it needs a Discord bot. Add one in Settings → Your Discord, or use a setup code with a bot in it."
- [ ] Referee tab: + with an empty box on a waiting game → the breakdown says "Referee change +1" (not "Referee: referee"); Submit → the report says the same

## New in 0.1.66 (Red Alert 0.3.18)
_Red Alert 0.3.18 on the caster's PC with OBS connected and the Stream Deck plugin from the same release. Hosts on The Button 0.1.66 for the On stream light and the voice bot fix._

### Red Alert: Best replay
_Two or more lobbies playing, replays saving (Source Record)._
- [ ] After a kill: **N** (or the Live desk's **Best replay** button) → Preview shows the clip's still, REPLAY and "Replay: LJ · … (MEDIUM)" (or HIGH, VERY HIGH, MUST SHOW); the toast ends "In Preview: Take plays it."; nothing changes on stream
- [ ] **Take** (Enter) → the replay plays in TT Replay, as Play last does, and Preview empties
- [ ] Several replays ready: the highest tier comes first, and in the same tier a play from a lobby that wasn't on stream comes before one viewers saw; **N** again → the next one down, and after the last back to the best
- [ ] A replay that has played, a **Replay that** clip, a montage or a play older than 3 minutes is never offered; with none left → "No replay worth showing yet." (or "No replay worth showing yet: one is still being saved.") and Preview doesn't change
- [ ] **Hold** on → N still puts a replay in Preview (it switches nothing), and Take plays it
- [ ] Auto grid on: a replay in Preview isn't replaced by auto grid's suggestion
- [ ] OBS disconnected with a replay in Preview → Take says "OBS isn't connected: replays play in OBS." and the replay stays in Preview
- [ ] Stream Deck: a Lobby key set to **Best replay** and a Replay key set to **Best replay** show BEST REPLAY with the pick's lobby and kind of play ("LJ: kill", never a player's name), "in Preview" while it's staged, "nothing yet" when there's none; a press does what N does
- [ ] An old `lobby/action` or `action` link (Companion, a Website key) does the same as `replay/best`

### Stream Deck: select, then submit
- [ ] A new Lobby key: its **Press** is **Put it in Preview (Take puts it on)**; a press puts the lobby in Preview (green border) and nothing on stream; the Take key reads TAKE with the lobby under it; Take puts it on stream
- [ ] A new Player cam key: a press answers "Preview: Lobby 3's player camera on Bob. Take puts it on." and sets who it follows; the camera's next, previous and auto keys have a green border; Take puts TT Player Cam on stream
- [ ] With Lobby 3's player camera on stream, press a Player cam key set to Preview for another player → the stream keeps the same player until Take; Take switches to the new one
- [ ] Best replay from the Stream Deck: the answer (and the plugin's log) gives the play ("LJ: kill"), no player names
- [ ] A key set up with the older plugin (its Press or any other setting changed before) still cuts straight to stream after the plugin update
- [ ] A key whose settings were never opened (Lobby 1 as it came) now puts its lobby in Preview: set **Press** to **Cut it to stream** if it should cut
- [ ] A replay in Preview → the Take key reads TAKE with REPLAY L<n> under it
- [ ] Settings → Stream Deck → link builder: **Camera into Preview (Take puts it on)** adds `&to=pvw`; the Replay list has **Best replay into Preview (Take plays it)**; the Lobby list no longer offers "Where the action is"

### Red Alert: Starting soon
- [ ] Live desk → Breaks → **Starting soon** → TT Starting Soon on stream at once with the tournament's name and logo; the status bar says BREAK, the Program box STARTING SOON, and the Starting soon card shows
- [ ] **Starts**: type 10 and **Set** → the card says "at <time> · 9:59 to go" and the stream counts down; 19:30 → counts down to 19:30 (a time already past today means tomorrow); empty → "No countdown"; "abc" → "Type minutes (10) or a time (19:30)."
- [ ] Your line: type it and press **Set** (or Enter) → it shows on stream; it takes at most 120 characters
- [ ] At zero the stream says "Starting now" and stays; while it's on, auto grid, auto intermission, armed cameras and the graphics queue wait, and lobby voice is silent
- [ ] Sponsors listed for `startingsoon` in sponsors.json take turns every 10 s; with none listed, the `break` sponsors do; **Export report** has "Starting soon screen" appearances
- [ ] Click **Starting soon** again while it's on → the cursor goes to **Starts**; nothing switches
- [ ] **Take** a lobby with the broadcast delay on (3 s) → the lobby comes on at once and no Starting soon graphic lingers for 3 s on top of it
- [ ] **End break**, **Undo** and Panic's undo come off it, or go back to it
- [ ] Stream Deck: the Break key set to **Starting soon** shows SOON / STARTING SOON and puts it on; a Custom path `break/soon?in=10&text=Doors%20open` puts it on with that countdown and line; `soon/set?at=19:30` changes the countdown without switching

### The Button: On stream light
_A host sending their game to Red Alert 0.3.18._
- [ ] Their lobby on stream full screen → Home shows the red **ON STREAM** bar: "On stream (full screen)" and "You're on stream: stay in the game window, no menus."; in a 2-up, the quad, the grid, a player camera or a sponsor break it says "On stream (in the 2-up)", "(in the quad)", "(in the grid)", "(player camera)", "(sponsor break)"
- [ ] Their lobby in Red Alert's Preview (not on stream) → amber **NEXT**: "Next: the caster has your lobby in Preview."; neither → no bar
- [ ] Close Red Alert, or turn **Send my game to the caster** off → the bar goes within about 10 s
- [ ] Another host's Home shows only their own lobby's state

### Red Alert: broadcast delay
- [ ] After the update, every TT Lobby, TT Voice and TT Cam source in OBS reloads once (a moment of black); the OBS card's **Broadcast delay** says 3 s
- [ ] With 3 s: a kill on the Live desk reaches the stream about 3 s later, and its impostor tags, alive counts, sabotage timer and off-screen alert arrive with the picture, not before; **Take** changes the layout on stream at once
- [ ] Change it to 1 s → "Broadcast delay 1 s: the lobbies and the graphics reach the stream 1 s late. The Live desk stays live. Every lobby in OBS reloaded with it."; **Off** → "Broadcast delay off: the lobbies go to the stream as they come."; back to 3 s
- [ ] Off with impostor tags on → the warnings rail says "No broadcast delay" with **Turn on a 3 s delay**, and the setup check says "Broadcast delay off · impostor tags are on"; the button puts it back to 3 s
- [ ] On: the setup check's delay line says your mic isn't held back and gives a Sync Offset of 3000 ms
- [ ] **Replay that** and the kill clips play about 3 s later than in 0.3.17, and the zoom, crop and the play's mark still line up
- [ ] Intermission, Be right back and a replay go on with no 3 s of the old graphics left on them
- [ ] Simulation: the graphics are delayed too
- [ ] The lobby voice on stream is in step with the picture (fine-tune with the per-lobby delay if not)

### Red Alert: Hear Preview
_OBS → Settings → Audio → Advanced → Monitoring Device set to the caster's headphones; Desktop Audio muted._
- [ ] Setup check: "Headphones (OBS monitoring device): <your headphones>" is OK; set the device to Default → OK with a note to check it is your headphones; Disabled → a warning
- [ ] A lobby in Preview → **HEAR** turns green "🎧 LJ" and you hear LJ live in your headphones; the stream and the recording don't have it (OBS's **TT Cue** source is Monitor Only)
- [ ] **Take** it → the headphones go quiet (Preview is empty); another lobby in Preview → that one is heard
- [ ] Mute all, Panic, an empty Preview and auto grid's own suggestion → silent
- [ ] Unmute Desktop Audio → **HEAR** turns amber: "Hear Preview waits while Desktop Audio is capturing: it could carry your headphones to the stream. Mute Desktop Audio."; mute it → it plays again
- [ ] Click **HEAR** → **HEAR: OFF** ("Hear Preview off."); again → on ("Hear Preview on: the lobby in Preview plays in your headphones (OBS's monitoring device), not on stream.")
- [ ] Stream Deck Toggle → **Hear Preview in headphones** shows HEAR PREVIEW on or off and switches it
- [ ] Delete TT Cue in OBS → Red Alert makes it again; close Red Alert while TT Cue is playing and open it → TT Cue is muted when it connects
- [ ] While HEAR plays, set TT Cue to **Monitor and Output** in OBS (Advanced Audio Properties) → it's muted at once and goes back to Monitor Only, then plays again in your headphones only

### Red Alert: restarts and crashes
- [ ] Turn on Hold, Auto grid, Auto intermission, set a countdown, Win counter: this round, Standings: whole tournament → close and reopen Red Alert → all still set (a countdown or timed hold that ran out meanwhile isn't)
- [ ] With lobbies showing → close and reopen Red Alert (or reopen with the internet off) → the lobbies and their OBS sources are there at once; a lobby not sending yet says **CACHED** on its tile (hover: "Kept from before Red Alert restarted…") until the results channel confirms it, within a few seconds once Discord answers
- [ ] A lobby whose live message was deleted meanwhile → it goes after the first poll
- [ ] **Save support bundle** → no lobbies-cache.json in the zip, and no VDO.Ninja password in any file
- [ ] Lock Red Alert → lobbies-cache.json is gone from %LOCALAPPDATA%\RedAlert
- [ ] End the desk's msedgewebview2.exe renderer process in Task Manager → the page comes back by itself, the lobbies reconnect, no auto switch or LOBBY DOWN cards; red-alert.log says "Window: the page's renderer crashed" and "The desk page is back"
- [ ] End the WebView2 browser process (the parent msedgewebview2.exe) → a new one starts and the desk comes back
- [ ] The Button: end its renderer process the same way → its page comes back; the-button.log says "Window: the page's renderer crashed"

### Discord voice bot
- [ ] Host with the bot in their voice channel → close Among Us → the bot leaves the voice channel and stays out (try it several times, also straight after The Button reconnects to Discord); it never comes back into the host's channel by itself
- [ ] Host again → the bot joins once
