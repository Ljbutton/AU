# Dry run: a 30-minute rehearsal before the event

Run this a day or two before the event with the **real people on their real PCs**: every host on
their own PC with The Button, the mod and Among Us, and the caster on the streaming PC with Red
Alert, OBS and the Stream Deck. Same internet, same OBS, same Stream Deck as on the day. It takes
about 30 minutes plus the setup before it, and it finds the problems that only show up with real
hosts: a host's upload that can't carry their game, a voice that doesn't reach the stream, a Stream
Deck key that means the wrong lobby.

Print this page or copy it into a shared document, and tick each box as you go. Write anything that
goes wrong in **Notes** at the end, with the time on the PC's clock, and save a support bundle
straight away (see [What to send if something fails](#what-to-send-if-something-fails)).

**Who's needed**

* [ ] **The caster**: Red Alert, OBS and the Stream Deck on the streaming PC.
* [ ] **The hosts**: as many of the event's hosts as can make it (at least two), each with their
  own setup code.
* [ ] **Players**: enough to play real games in at least two lobbies (four or more players a lobby,
  besides the host when the host is the referee).
* [ ] **The organiser**, with the administration code and the private results channel open.
* [ ] **Someone watching what viewers get**: a test stream to an unlisted channel on a phone, or
  OBS recording to play back at the end.
* [ ] Everyone in one Discord voice call to talk it through (not a lobby's voice channel).

---

## Before: T-60 to T-30

### Everyone

* [ ] **Update everything** and restart each app afterwards:
  * The Button: no **The Button vX is out** in the top bar; Settings → Mod and updates says
    **Everything is up to date**.
  * The mod: no **Mod not installed** or **Mod needs repair** in The Button's top bar.
  * Red Alert: Settings → Updates says **up to date**.
  * OBS, the Stream Deck app, and the Red Alert Stream Deck plugin from the same Red Alert release.
* [ ] Write the versions in **Notes** (The Button, the mod, Red Alert, OBS).

### The organiser

* [ ] **Codes**: each host has their own host setup code (`TT1-…`), sent to them privately; the
  caster has the administration code. Never post a code in a public channel.
* [ ] **Bots online**: with each host's Button open, their lobby's bot shows as online in the
  server's member list.
* [ ] In Red Alert (Settings → Administration code), paste each host's code into **Check**: every
  one says it matches.

### Each host (The Button and the mod)

* [ ] The setup code is in (Settings); the tournament's name shows and the top bar has no warning.
* [ ] **Not in safe mode**: if The Button's Home shows a red safe mode card (Among Us has updated
  past the version the mod was made for), stop and tell the organiser. Don't press **Run normally
  anyway** on your own on the day.
* [ ] **Upload check**: The Button → Settings → Your connection → **Check my connection**, on the PC
  and the internet you'll use on the day: it has to pass. If it doesn't: plug in a cable, stop other uploads (cloud backups, another stream), and tell
  the caster (they can turn player cameras off, which saves about one video stream of upload).
* [ ] **Host authority** on (Home → Tools; it's on by default). It applies to the next lobby you
  host from the main menu, so check it before hosting.
* [ ] **Referee ghost slot** on (Home → Tools) if the event has the host referee, with the lobby set
  to 11 players.
* [ ] Among Us started and a lobby hosted: The Button connects by itself and Home's checklist is
  green (or says what's missing).
* [ ] In your lobby's Discord voice channel, with **automute** on.

### The caster (Red Alert, OBS, Stream Deck)

* [ ] OBS open with the event's scene collection and profile (not "Untitled"), the WebSocket server
  on (Tools → WebSocket Server Settings).
* [ ] Red Alert unlocked with the administration code. The OBS page says **Connected**; press
  **Rebuild TT scenes**.
* [ ] **OBS scenes built**: TT Full, TT 2-up, TT Quad, TT Grid, TT Player Cam, TT Intermission,
  TT Be Right Back, TT Sponsor Break and TT Replay are all in OBS.
* [ ] OBS → **Setup check** all green: Source Record found (replays), Desktop Audio muted, and a
  stream delay if Impostor tags are on.
* [ ] **Stream Deck plugin paired**: the plugin's **Test** finds Red Alert (Settings → Stream Deck
  has the port and the key), and no key shows SET UP, OFFLINE or WRONG KEY.
* [ ] **Lobby keys set to Preview**: each Stream Deck lobby key's **Press** setting is **Put it in
  Preview**, with a **Take** key next to them (as in the
  [recommended layouts](stream-deck.md#recommended-layouts)). A lobby key left on the default,
  **Cut it to stream**, puts its lobby on stream at once.
* [ ] **Simulation off**: no TEST MODE bar across the top.
* [ ] The test stream (unlisted) or the recording is ready to start.

---

## The 30 minutes

Start the test stream or the recording at T+0. Each step says who does it, what to do and what you
should see. If something doesn't happen as it says, note the time, carry on, and save support
bundles at the end.

### T+0 to T+3 · Hosts send their game

* [ ] **Each host**: The Button → **Send my game to the caster**. On the page that opens, keep **The
  Among Us window (recommended)**; when the browser asks what to share, pick the **Window** tab and
  the **Among Us** window, not the whole screen (nothing else on
  the host's PC can end up on stream, and it uses less upload).
* [ ] **Caster**: each lobby appears on the Live desk with a number. Settings → Stream Deck →
  **Lobby numbers** → **Renumber online lobbies 1…N**, and write down which lobby is which number.
* [ ] **Caster**: the Stream Deck's lobby keys show the same numbers and names.

### T+3 to T+6 · Lobby health and voice

* [ ] **Caster**: Lobby health shows every lobby **green** (data, video and audio).
* [ ] **Caster**: Lobby voice shows a row per host, sending. While people talk and play, each row's
  **Discord** and **Among Us** meters move, and so does **Sent** (the sound as it leaves the host's
  PC). Sent moving but silence in OBS means OBS's side; Sent still means the host's side.
* [ ] **Each lobby in turn** says something: the stream carries only the lobby on stream (the voice
  follows the picture). **Listen** on a row keeps that lobby's voice up; press it again to stop.

### T+6 to T+9 · Every lobby: Preview, then Take

* [ ] **Caster**, for each lobby: its number key (1-9), or its Stream Deck lobby key set to **Press:
  Put it in Preview**, puts it in **Preview** (green); **Take** (Enter, or the Take key) puts it on
  stream. Program turns red, OBS switches within a couple of seconds, and the status bar says PROGRAM.
  (A lobby key left on the default, **Cut it to stream**, skips Preview and puts the lobby on stream
  at once.)
* [ ] The watcher sees the right lobby with the right label, and hears that lobby.
* [ ] Two lobbies in Preview (Ctrl+click), **Take**: 2-up on stream. Four: the quad. **Back** undoes
  the last switch.

### T+9 to T+13 · Player cams from the Stream Deck

* [ ] Hosts start real games in at least two lobbies.
* [ ] **Caster**, mid-game on the lobby on stream: a Stream Deck **Player cam** slot key puts **TT
  Player Cam** on stream, following that player; next and previous change who; **Wide** goes back to
  the whole map. The picture is sharp and keeps up with the game.
* [ ] **During a meeting**, press a player's camera key: the key and the Live desk say **ARMED**
  (amber) and nothing cuts. When the meeting ends, the camera goes on stream by itself.
* [ ] Each lobby's camera key follows the lobby it says (the numbers from T+0).

### T+13 to T+16 · Replays

* [ ] After a kill in the lobby on stream: **Replay that** (Stream Deck or the Live desk). The last
  seconds play within a few seconds.
* [ ] **Slow-mo**: the speed key steps 100 / 50 / 25 %. **Back to live** returns to the lobby.
* [ ] **Play last** plays the newest replay again. If the player cam was on, **P** switches angle.

### T+16 to T+18 · Panic, Undo and Hold

* [ ] **Panic** (P twice, or the Panic key): it fades to Be right back, mutes every lobby voice,
  clears the graphics and holds switching. The key says **UNDO PANIC**.
* [ ] **Undo** (the key, or Back): what was on comes back, with the voice and the graphics.
* [ ] **Hold** (H, or the AUTO pill): nothing switches by itself (a held-back switch shows as a
  warning); your own switches still go through. Release it.

### T+18 to T+21 · Intermission and a sponsor break

* [ ] **Intermission** (I): the intermission screen with the standings. Type 5 in **Next round**
  and press **Set**: the countdown shows on stream. Lobby voice is silent. I again: back to the
  lobbies.
* [ ] **Sponsor break**: the lobby on one side, the sponsor on the other; it ends by itself.

### T+21 to T+22 · Mute all

* [ ] **Mute all** from the Stream Deck, then from the Live desk (U), then the mute-all key
  (Ctrl+Shift+M unless it was changed) with another window in front. The AUDIO pill says **All
  muted** and the watcher hears no lobby. Each one again: the voice comes back.

### T+22 to T+30 · Failure drill (on purpose)

**A. A host closes Among Us mid-game** (T+22 to T+25). Put that lobby on stream first, with auto
switch on (Lobby health).

* [ ] Within about 10 seconds Red Alert shows the lobby **down** (red in Lobby health, a warning on
  the Live desk), and auto switch moves the stream off it (**Undo / keep** is offered).
* [ ] **In Discord**, everyone the bot had muted in that lobby is **unmuted** by the host's Button
  within about 30 seconds (once it's sure the game has gone): the players check they can talk to
  each other.
* [ ] The host's Button says why the game disconnected, if it shows it: write it in Notes.
* [ ] The host starts Among Us again and hosts. The live data comes back by itself, but **the
  picture doesn't**: sharing the Among Us window ended when the window closed. On the VDO.Ninja send
  page the host presses **Pick again** and picks the new **Among Us** window. If that page says
  *This tab isn't sending any more* (The Button turns sending off once the picture has stopped for
  20 seconds), the host turns **Send my game to the caster** on again in The Button (Home → Tools)
  and picks the **Among Us** window in the page that opens.
* [ ] Only then does the lobby come back green, with the **same number**. A game that stayed down
  shows under **Interrupted** in Lobby health, to count, void or replay.

**B. OBS restarts** (T+25 to T+27). Close OBS completely, then open it again.

* [ ] While it's closed: the status bar says **OBS not connected** in red, and switching keys on
  the Stream Deck show a triangle.
* [ ] Open OBS: Red Alert **reconnects by itself** within about 10 seconds, nothing to click. The
  TT scenes are there, Setup check is green, and a Take works. Replay that works again after about
  30 seconds (the replay buffers start over).

**C. Red Alert restarts** (T+27 to T+30). Close Red Alert's window and start it again.

* [ ] While it's closed, the stream keeps showing what was on (the hosts' video goes straight to
  OBS), the on-stream graphics may freeze or go, and the Stream Deck keys say OFFLINE.
* [ ] It opens unlocked, reconnects to OBS by itself, and every lobby comes back with the **same
  lobby number**. The graphics come back, and the Stream Deck keys come back by themselves and
  still mean the same lobbies.
* [ ] Mute all, volumes and Listen are as they were.

---

## Sign-off

Tick every line before the event. Anything not ticked needs an owner and a fix date in Notes.

* [ ] Every host passed the upload check, and no host's mod is in safe mode.
* [ ] Every lobby went on stream through Preview and Take, and its number matched the Stream Deck.
* [ ] Every host's voice was heard on stream, only while their lobby was on, and the Sent meters
  moved.
* [ ] Player cams worked from the Stream Deck, including an armed pick during a meeting.
* [ ] Replay that, Play last and slow-mo worked.
* [ ] Panic and Undo put everything back; Hold, intermission, the sponsor break and Mute all worked.
* [ ] Failure drill: the dropped lobby was handled and its players unmuted, its host picked the
  Among Us window again and it came back green with the same number, OBS came back by itself, and
  Red Alert came back with the same lobby numbers.
* [ ] The recording or test stream looked and sounded right (watch a minute of it back).
* [ ] No red on Red Alert's status bar and no Discord problem when finished.

Signed: Caster ____________ · Organiser ____________ · Hosts ____________ · Date ____________

---

## What to send if something fails

Save a support bundle **from both apps** as soon after the problem as you can (logs roll over as
they grow):

* **Red Alert** (the caster): Settings → Help → **Save support bundle**. A zip called
  `RedAlert-support-<date>-<time>.zip` appears on the Desktop and the folder opens. It works on the
  lock screen too, before the administration code is in. It holds Red Alert's log, its settings, OBS's
  newest log and crash report, the versions, the Live desk's warnings and what the desk, OBS, the
  lobby voice and the lobbies' health were doing.
* **The Button** (each host involved): Settings → Help → **Save support bundle**. A zip called
  `TournamentTracker-support-<date>-<time>.zip` appears on the Desktop and the folder opens. It holds
  The Button's and the mod's logs, Among Us's own log, safe mode and the last disconnects, and the
  last connection check.
* If **OBS crashed**, save Red Alert's bundle **before** opening OBS again (the newest OBS log is
  then still the one from the crash), or send the log from that time from OBS → Help → Log Files →
  Show Log Files.

Send the zips as they are, with a few lines:

* [ ] What you were doing, and what happened instead of what you expected.
* [ ] Roughly when, by the PC's clock (the logs use it).
* [ ] Which lobby and which host.
* [ ] A screenshot or a phone video, if you have one.

The bundles are safe to send: setup and administration codes, bot tokens, webhooks, VDO.Ninja and
OBS passwords, stream keys, the Stream Deck key and Twitch sign-ins are taken out of every file
(they show as `[removed]`). Don't send screenshots of a Settings page with a code on it.

---

## Notes

| Time | Who | What happened | Bundle saved? | Owner / fix |
|---|---|---|---|---|
| | | | | |
| | | | | |
| | | | | |
