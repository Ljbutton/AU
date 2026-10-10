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
- [ ] − / + change a player's points — hover the points to see "Referee" in their breakdown.
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
- [ ] Referee tab: type 0.25 next to a player and press + → their points go up by exactly 0.25 ("Referee +0.25")
- [ ] Type 1.5 and press − → down by 1.5
- [ ] Empty box + → one point, as before
- [ ] 1.234 (4 significant figures) or 0 is refused with a message, and nothing changes
- [ ] Home → Results channel (administration code): − / + buttons post !adjust with the sign

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
- [ ] Home → Tools: "Show the referee as dead to players" (shows when the referee slot is on). On: ~10 s into a game, ask a player — the referee is crossed out in their next meeting. If the host gets kicked when it's sent, turn it off
- [ ] Red Alert → Settings: "Administration code" card — paste another code and Change (or Lock); works after the first code
