# What's left to do

What needs doing, or could be done, to improve the tournament system, as of The Button and the mod **v0.1.67** and Red Alert **0.3.19** (20 October 2026; first written for v0.1.64). Each item says why, where the evidence is (file and line as of v0.1.64), and a rough size: **S** is under half a day, **M** is 1–2 days, **L** is 3 days or more.

What each app already does is in [features-the-button.md](features-the-button.md), [features-mod.md](features-mod.md) and [features-red-alert.md](features-red-alert.md).

**Why the list starts where it does:** 23 releases went out between 0.1.41 (9 October) and 0.1.64 (13 October), adding about 20,000 lines, and none of the 346 checks in [test-list.md](test-list.md) is ticked. 0.1.64 reworked the code that turns games into points and Discord posts. So the first job is proving what exists in real games, not adding more.

---

## Must do before the next event

### Everyone (process)

1. **Freeze one build, and every version, for the event.**
   - **Why:** The Button updates itself and the mod by default. A release published during event week reaches a host the next time Among Us closes, for example after a crash mid-event. Lobbies then run different versions, and nothing has tested the new one.
   - **Evidence:**
     - `AppServer.cs:26-29`: both auto-updates default to on.
     - `AppServer.cs:475-481`: GitHub is checked every 20 min and The Button installs itself.
     - `AppServer.cs:888-901`: the mod installs itself whenever Among Us is closed.
     - Three releases went out on 12–13 Oct.
     - The build floats: `GameLibsVersion *-*` and `BepInEx.IL2CPP.MSBuild 2.*-*` (Plugin csproj:15, :30). v0.1.64 was built with MSBuild 2.1.0-rc.1, per its release notes.
     - VDO.Ninja is used unversioned (`CasterFeed.cs:16`).
   - **Do:**
     - Pick the event release and publish nothing from T-48h.
     - After the dry run, hosts switch **Update automatically** off.
     - Any fix release is built with an explicit `game_libs_version`.
     - Freeze OBS, Source Record, the Stream Deck app and the plugin on the caster PC as well.
   - **Size:** S.

2. **Cut the test list down to a one-night critical path, and play it.**
   - **Why:** Nothing from 0.1.56 to 0.1.64 has been confirmed in a real game. The 0.1.64 refactor touched exactly the code that turns games into points and Discord posts.
   - **Evidence:**
     - docs/test-list.md: 346 unticked, 0 ticked. Its "Before you start" still names 0.1.41 / 0.3.9 (lines 7-8).
     - `git show --stat HEAD` touches `Verify.cs`, `Voids.cs`, `Standings.cs`, `PrelimLeaderboards.cs`, `SlashLinks.cs` and `TournamentSession.cs`.
     - Neither test-list.md nor dry-run.md mentions Epic Games or a fresh install on 64-bit Among Us (grep finds no "Epic" or "64-bit").
   - **Do:**
     - Choose about 30 items: items 3–5 below, F10 lag with the player cam, settings lock with the real code, automute through meetings with 10 players, and the 0.1.64 "Both apps" block.
     - Add one fresh install with `Install-TheButton.bat` on a clean PC.
     - Add one Epic host if any host uses Epic.
   - **Size:** M.

### The mod (in Among Us)

3. **Prove host authority and the referee ghost with players who don't have the mod (ban risk).**
   - **Why:** The +25 flag is on by default. The last two ways of making the referee dead for other players got the host kicked, then banned from their own room. Nothing has confirmed the new way in a live lobby.
   - **Evidence:**
     - `Patches/HostAuthority.cs`.
     - README.md:399 still calls the referee ghost slot "(experimental)"; README.md:409-410 records the ban.
     - Unticked: test-list.md:242 (votes count), :258 (crossed-out card on a phone), :273-275 (no kick over 3+ games, console join, `hostauth off`).
   - **Do (with an alt account as host first):**
     - The log shows `owner -2` and "host authority (modded host): True".
     - With `[Debug] RefereeRecordSync = true`, lines read "written for sending … IsDead=True", with no "reset seen".
     - A phone joins by code, and a console too if possible.
     - Play 3 or more full games with no kick.
     - A vote for the referee counts as a skip.
     - Fallback if it fails: `hostauth off`, re-hosted from the main menu.
   - **Size:** M.

### The Button and Discord

4. **Rehearse the scoring path end to end, with real Discord.**
   - **Why:** Points are what the tournament is judged on, and the dry run doesn't touch them.
   - **Evidence:**
     - dry-run.md has no Submit, Void, points change, standings or round-start step (grep for submit / void / standings / adjust finds only a "count, void or replay" mention).
     - test-list.md:38-50, 170-181 and 445-470 are all unticked.
   - **Do:** add a 5-minute "Scoring and Discord" block to dry-run.md, then run it. It covers:
     - Referee tab: + / −, and Void with a reason.
     - Submit and Submit all.
     - The report, standings and round scores each land in the right channel. The public channel gets only results and the timeline.
     - A points change, a void and an unvoid from the Referee tab's **Results channel** card (administration code), each answered with ✅.
     - The preliminary Count.
     - Organiser tab → **Rounds**: **Build its lobbies** for round 2, then **Start round**.
     - Restart Among Us while games are waiting.
   - **Size:** S (doc), plus the session.

5. **Crash and drop drills for automute, plus a written manual unmute.**
   - **Why:** The crash unmute only runs when The Button is reopened on that same PC. If the host's PC or internet is gone, the lobby's players stay server-muted and deafened.
   - **Evidence:**
     - `DiscordBridge.cs:537-576` and :670 (`button-muted.json`).
     - Unticked: test-list.md:308-316 (gateway drop, crash of both apps, Among Us crash).
     - No unmute action anywhere in `TournamentTracker.Common` or `TournamentTracker.Broadcast` (grep).
   - **Do:**
     - Run those drills.
     - Add one line to dry-run.md: "Host PC gone: a moderator with Mute/Deafen Members right-clicks each player in that voice channel and turns off Server Mute and Server Deafen."
   - **Size:** S.

6. **Write an Among Us update-day runbook (and check the feed today).**
   - **Why:** Safe mode is the right reaction to an update, but it turns off the referee slot, impostor rotation, settings lock and host authority, and nobody has a written plan for that day.
   - **Evidence:**
     - `SafeMode.cs:118-130`.
     - dry-run.md only says "stop and tell the organiser".
     - BepInEx is pinned to be.735 because newer builds break the interop generator (README.md:1197-1198).
     - The Build workflow already takes `game_libs_version` and `release_tag` (build.yml:12-20).
   - **Do, write down:**
     - Who watches the BepInEx feed for a new `AmongUs.GameLibs.Steam`.
     - Building with that version.
     - Checking "Self-check: every hook is in place" and "Safe mode off" in a private lobby before hosts update.
     - When "Run normally anyway" is acceptable.
     - Plan B if be.735 can't load the new game at all: play without the referee slot (10-player lobbies), or AutoMuteUs with manual scoring.
   - **Size:** S.

7. **Don't count on the "every 10 minutes" preliminary leaderboard.**
   - **Why:** GitHub runs the schedule about 4–5 times a day, so the combined board can be hours out of date during a preliminary session.
   - **Evidence:**
     - README.md:251 and prelim-leaderboards.yml:10 say every 10 minutes.
     - Actual scheduled runs on 14–15 Oct: 01:43, 06:44, 12:26, 17:27, 20:52, 23:35, 01:47, 06:32, 12:02 UTC. That is gaps of 2.5 to 6 hours over these two days, and up to 9 hours in the full history (Actions API).
   - **Do:**
     - For a preliminary event, press Actions → Preliminary leaderboards → Run workflow after each session.
     - The real fix is item 30.
   - **Size:** S.

---

## Should do soon

### Distribution and CI

8. **Pin and record what the mod is built against.**
   - **Why:** `*-*` takes prereleases too, and a rebuild in event week can silently target a different game version than the hosts run. This also confuses safe mode's "built for" comparison.
   - **Evidence:**
     - Plugin csproj:15 (`*-*`) and :30 (`2.*-*`).
     - The BepInEx loaders are downloaded from builds.bepinex.dev at release time (build.yml `fetch()`).
   - **Do:**
     - Commit the versions, for example in Directory.Build.props.
     - Have CI warn when the feed has a newer GameLibs than the pinned one.
   - **Size:** S.

9. **A CI check that every Harmony hook still fits the game.**
   - **Why:** The mod has about 5,000 lines and 28 `[HarmonyPatch]` hooks, and no tests. HookCheck only runs inside Among Us at start. Positional arguments have already broken once: CastVote changed from byte to PlayerId (HookCheck.cs:9-11), and `RpcVotingComplete` now takes 5 parameters.
   - **Do:** the interop assemblies are generated during the build. A test using `MetadataLoadContext` can resolve each patch's target and its `__N` and `__instance` types, so a game update shows up as a red build instead of a bad night.
   - **Size:** M.

10. **Check the installers' downloads too.**
    - **Why:** The Button's own update, the mod bundle and Red Alert's update are now checked against the release's size and SHA-256 (see [Done in 0.1.65](#done-in-0165)). The one-click installers aren't: they run whatever arrives.
    - **Evidence:** `install.ps1:50` and `install-redalert.ps1:72` download with `Invoke-WebRequest` and use the file as it is; `install-redalert.ps1` already reads the release list, which has each asset's `sha256:` digest.
    - **Do:** compare `Get-FileHash` with the API's digest before installing. It covers integrity; authenticity needs item 11.
    - **Size:** S.

11. **Code signing.**
    - **Why:** Hosts are told to click "More info → Run anyway" (README.md:47 and the release notes). A .bat that runs PowerShell to download an exe, and BepInEx's `winhttp.dll` proxy, are both typical antivirus false-positive triggers.
    - **Do:** sign `TheButton.exe` and `RedAlert.exe`. Note any antivirus hits during the dry run.
    - **Options found:**
      - Azure Artifact Signing: about $9.99 a month; organisations in the US, Canada, the EU or the UK, individuals in the US or Canada only.
      - An OV certificate on a cloud HSM: about $116–219 a year.
      - EV: not worth it.
      - SignPath Foundation (free for open source): likely not eligible (WebView2).
    - **Where:** in build.yml after `dotnet publish` and before the installers are built and uploaded (jsign on the Linux runner, or a Windows signing job), on releases only. Later the updater can check the Authenticode signer too.
    - **Size:** M–L.

12. **Let the organiser freeze updates in code.**
    - **Why:** Item 1 relies on every host remembering to turn updates off.
    - **Evidence:** everyone follows `releases/latest` (ModInstaller.cs:111). The only rollback is publishing a newer release.
    - **Do:** add a setup-code field, "hold updates until <date>" or "stay on vX".
    - **Size:** S–M.

*13 is done: see [Done in 0.1.65](#done-in-0165).*

14. **Run the embedded page scripts in CI.**
    - **Why:** A syntax slip leaves a blank Live desk while CI stays green.
    - **Evidence:**
      - caster.html is 2,913 lines, index.html 1,321, plus the send page's inline JS.
      - The tests only search the source text (`CasterPageScriptTests.cs:16-28`). Nothing parses or runs the JS (no Jint or node in the tests).
    - **Do:**
      - Run `node --check` on the extracted scripts (S).
      - Add a Playwright smoke run against Red Alert in Simulation, and The Button with a fake mod (M).
    - **Size:** S, then M.

### The Button and the mod

*15 and 16 are done: see [Done in 0.1.65](#done-in-0165).*

17. **Protect secrets at rest, and write a rotation runbook.**
    - **Why:** Any other BepInEx plugin, or anyone who can read the folder, can take the bots. A leaked code needs an exact recovery procedure.
    - **Evidence, all plain JSON:**
      - bot tokens in `setup-code.txt` and `host-discord.json` in the Among Us folder (`SetupCode.cs:240`, `HostDiscord.cs:95`)
      - The Button's `settings.json` (`AppServer.cs:32-41`)
      - Red Alert's `settings.json`, `obs.json` and `twitch-token.json`
      - Codes are only encoded (`SetupCode.cs:19`), and the admin code, which holds a bot token, goes to every referee and caster.
    - **Do:**
      - Use DPAPI (CurrentUser).
      - Write "code leaked: reset the token, make new codes, hosts paste them".
    - **Size:** M.

18. **An emergency unmute that doesn't need the host's PC.**
    - **Why:** This is the code fix behind item 5. Today neither the Organiser tab nor Red Alert can help, because the live data has no voice channel or Discord IDs (`LivePublish.cs:61-95`).
    - **Do:** add them to the live data, then add "Unmute LJ's players" with the admin bot. It needs Mute and Deafen Members.
    - **Size:** M.

19. **Host authority moves anti-cheat to the host.**
    - **Why:** With +25 the server no longer checks kills. A cheat in a screened tournament is unlikely, but if one happens, the host's stats and the stream silently disagree with what players saw.
    - **Evidence:**
      - The `CheckMurder` prefix only protects the referee (`GamePatches.cs:95-115`), with no distance or cooldown check.
      - `HostOnlyMessageGuard` only turns messages down on the host's own game (`HostAuthority.cs:101-129`). Home itself says other games may still have acted on them (`ControlApi.cs:233`).
    - **Do:** add a cooldown and distance check, and an optional kick after repeats.
    - **Size:** M.

### Red Alert

20. **The desk's data receivers in a second, hidden WebView2.**
    - **Why:** Every host's data receiver is a hidden iframe in the desk page (`caster.html`). The page now comes back by itself after a crash or hang, and lobbies aren't counted as down while it does (see [Done in 0.1.66](#done-in-0166)), but every data link still drops and reconnects with it.
    - **Do:** move the receivers into a second, hidden WebView2, so a desk page crash leaves the data links up.
    - **Size:** M.

*21 is done: see [Done in 0.1.66](#done-in-0166).*

22. **Impostors on stream.**
    - **Decided:** there is no OBS stream delay, and impostor information stays visible on stream. The docs no longer ask for a 2–3 minute or 90 s delay. Red Alert's own broadcast delay (0.3.18) is off since 0.3.19, so nothing is held back at all (see [Done in 0.1.67](#done-in-0167)); bringing it back is item 73.
    - **What shows impostors to anyone watching:** impostor tags, kill cards and alerts naming the killer, kill replays, player cams marking impostors and the referee ghost's screen (impostors' names in red), as it happens.
    - **Do:** nothing unless the decision changes.
    - **Size:** none.

*23 is done: see [Done in 0.1.65](#done-in-0165).*

*24 and 25 are done: see [Done in 0.1.66](#done-in-0166).*

26. **One upload budget, and desk tiles that don't cost hosts upload.**
    - **Why:** The numbers disagree, and the desk tiles cost each host an extra encode.
    - **Evidence:**
      - OBS asks every host for 8 Mbps (`ObsDirector.cs:38`).
      - Desk tiles are a second VDO.Ninja video viewer per host (`&scale=25`, `BroadcastServer.cs:293`), which the host's PC encodes separately while it plays.
      - The connection check budgets 4–6 Mbps for video and recommends 10 Mbps in all (`ConnectionCheck.cs:77-78`).
      - (The docs now all give the connection check's figure, about 10 Mbps.)
    - **Do:**
      - Default to about 6,000 kbps.
      - Build the tiles from OBS `GetSourceScreenshot` while OBS is connected (already used for Program, `ObsOutputs.cs:198`), instead of a second VDO.Ninja viewer per host. The owner is deciding on this one.
      - The Watch page now does this for the team (0.3.19): `WatchCapture.cs` takes a small picture of each TT Lobby source from OBS in one shared loop while someone watches, and serves the newest one. The desk tiles could reuse it (see item 74 for the OBS load).
    - **Size:** S, then M.

27. **Pin VDO.Ninja.**
    - **Why:** An upstream release before the event can break the caster feed. Everything depends on VDO.Ninja's iframe API: `dataReceived`, `getStats` and `_framesEncoded` (`SendPage.cs:261-277`).
    - **Evidence:** `https://vdo.ninja/` is used unversioned (`CasterFeed.cs:16`, `Organizer.cs:330`, `ConnectionCheck.cs:95`).
    - **Do:** pin a versioned path that passed the dry run, with a settings override.
    - **Size:** S.

28. **Organiser polling shares one bot.**
    - **Why:** Red Alert and each referee's Button with the admin code all poll the results channel with the same token. With several referees, 429s slow lobby discovery.
    - **Evidence:** every 3 s ±0.4 (`Organizer.cs:22`, :121-124), while The Button has an admin code in, whichever tab is open.
    - **Do:** poll only while the Organiser tab is open, or use the gateway.
    - **Size:** S.

29. **Plan for the caster PC failing.**
    - **Why:** Red Alert, OBS and the Stream Deck all run on one PC.
    - **Do:**
      - Export the OBS scene collection and profile, and `%LOCALAPPDATA%\RedAlert`, to a second PC.
      - Test whether a second Red Alert can join the same lobbies. Every host's data link resends until Red Alert acknowledges, and two receivers acknowledging is untested.
    - **Size:** S (runbook), plus a test.

### Preliminaries and docs

30. **Move the combined preliminary leaderboard off GitHub's scheduler.**
    - **Why:** This is the fix behind item 7.
    - **Do:** the code is already in Core (`PrelimLeaderboards.cs`). Have the organiser's Red Alert or Button run it after each lobby's Count, or trigger `workflow_dispatch` from an outside scheduler.
    - **Size:** M.

31. **Docs that still disagree with the code.** Most are fixed (see [Done in 0.1.65](#done-in-0165)). Left:
    - test-list.md:7-8 ("Before you start") still name 0.1.41 / 0.3.9.
    - README.md still calls the referee ghost slot "(experimental)", though host authority is on by default.
    - **Size:** S.

*32 and 33 are done: see [Done in 0.1.65](#done-in-0165).*

34. **Which bots can post a change.**
    - **Why:** A command a person types in the private results channel now does nothing: the mods act only on bots' messages, and every points and round change is made in The Button (see [Done in 0.1.65](#done-in-0165)). But any bot's message counts: the administration bot (its token is in the administration code every referee and caster holds), any other bot that can post there, and a webhook into the channel (Discord marks its messages as a bot's).
    - **Evidence:** `ChannelCommands.cs:53` and `SharedResults.ReadAsync` (SharedResults.cs:158-170) check only `AuthorIsBot` (`DiscordRest.cs:602`), no bot ID or role.
    - **Do:** accept changes only from known bots (the administration bot's user ID could go in the host codes), or the earlier idea of a referee role; until then, keep the channel staff only, with no webhooks or other bots in it.
    - **Size:** S.

---

### Found in the 0.1.65 review (not fixed yet)

49. **Preliminary games: only the host can change points now.**
    - **Why:** typed `!void` / `!adjust` in the preliminary channels no longer count, and the Organiser tab only posts to the tournament's results channel. An organiser can't correct a preliminary game; its host does it in the Referee tab.
    - **Do (if wanted):** list the preliminary channels in the administration code and let the Organiser tab post changes there too.
    - **Size:** M.

50. **Upgrading during a running tournament.**
    - **Why:** changes typed in Discord before 0.1.65 (`!adjust`, `!void`, `!resetleaderboard`) stop counting after the update, and older mods still count them, so standings can differ between lobbies until everyone updates.
    - **Do:** update every host, referee and the caster before an event, and redo any typed change from The Button. If a tournament is already running, count typed commands older than the release (by message time).
    - **Size:** S.

51. **Only The Button's own bot should be able to post a change.**
    - **Why:** the mods take changes from any bot account in the channel (not webhooks, not people). A host's bot added to the private channel could post one.
    - **Do:** put the administration bot's user ID in tournament host codes and accept changes only from it.
    - **Size:** S–M.

52. **Backup details.**
    - "Newer" is decided by file time, so a PC clock that was wrong for a while can keep stale copies; after a reinstall the round's game count isn't rebuilt from the game files.
    - `.corrupt-…` copies are never removed (some are copies of files holding tokens); prune them after a while.
    - The backup holds the caster-feed link and password from `state-*.json` (same PC as The Button's own settings, never in a support bundle).
    - **Size:** S each.

53. **Auto grid small things.** Hold says "Held back: Auto grid" while the caster's own Preview is waiting, and a replay going on doesn't take auto grid's suggestion out of Preview. **Size:** S.

54. **Other automatic switches still don't wait for Take:** auto switch away from a dropped lobby (and swapping a dead tile, Be right back coming back), the sponsor break ending, the player camera going back to the map, an armed camera going on, the grid closing up after a drop, a montage going back to intermission. Which of them go through Preview is waiting for a decision: item 59. (Auto intermission, a MUST SHOW play ending a break and the grid changing for any other reason were removed: see [Done in 0.1.67](#done-in-0167).) **Size:** M.

55. **Windows CI: an occasional stall in Red Alert's OBS tests.**
    - **Why:** on the Windows runner, a test using the fake OBS (HttpListener) sometimes waited forever, a different test each time; Linux never did. Limits were added to the fake's sends and closes, and to Red Alert's own sends to OBS (10 s), and the runs since are green.
    - **Do:** if it comes back, the CI step's watchdog prints every thread's stack and the waiting async chains (`dumpasync`) after 2 minutes: read them in the job log.
    - **Size:** S–M.

### Found in 0.1.66 (not done yet)

56. **Set the caster's mic Sync Offset by itself.** (On hold: moot while the broadcast delay is off, item 73.)
    - **Why:** the broadcast delay holds the lobbies back, not the caster's mic, so commentary runs about 3 s ahead of the picture viewers see. The setup check only suggests a Sync Offset (Edit → Advanced Audio Properties).
    - **Do (if wanted):** set the Sync Offset on the duck-under mic to the broadcast delay. It changes a source that isn't one of Red Alert's own, so ask first.
    - **Size:** S.

57. **Twitch's delay following the broadcast delay.** (On hold: moot while the broadcast delay is off, item 73. Twitch's own **Stream delay** works as it is.)
    - **Why:** the Twitch card's **Stream delay** (`twitch.json DelaySeconds`) is a setting of its own; the broadcast delay isn't added to it, so predictions, polls and chat messages can come before viewers see the play unless the caster counts it in.
    - **Do:** add the broadcast delay to Twitch's wait by itself, or show the total on the Twitch card.
    - **Size:** S.

58. **Hear Preview for every host, and after Take.**
    - **Why:** a host on the old way (Share system audio, no voice stream) has nothing for **TT Cue** to play ("… sends no lobby voice: there's nothing to hear."). And Hear Preview plays Preview only, so the headphones go quiet after Take.
    - **Do (if wanted):** cue old-way hosts from their screen share's sound; fall back to the lobby on stream when Preview is empty.
    - **Size:** S–M.

70. **A new Stream Deck plugin with an older Red Alert.**
    - **Why:** a new Player cam key set to Preview sends `?to=pvw`, which an older Red Alert ignores, so it cuts straight to stream while its border is green; Best replay answers 404.
    - **Do:** have the plugin check `/deck/state` for what's new (`preview.replay`, `voice.cue`) and fall back to Arm, or warn on the key. Until then: update Red Alert and its plugin together.
    - **Size:** S.

71. **Best replay's "missed" with the broadcast delay.** (On hold: moot while the broadcast delay is off, item 73.)
    - **Why:** a play counts as seen when its lobby goes on stream at the desk, which is live; with the delay the stream is a few seconds behind, so plays just before a cut-in count as missed, and plays just before a cut-away count as seen though they never aired.
    - **Do:** shift those times by the broadcast delay (`BestReplay.cs`, `MarkShown`).
    - **Size:** S.

*72 is done: see [Done in 0.1.67](#done-in-0167).*

### Found in 0.1.67 (not done yet)

74. **OBS load of the Watch page's pictures.**
    - **Why:** Team watch asks OBS for `GetSourceScreenshot` of every lobby, up to 5 a second each and 30 a second in all (`WatchCapture.cs`), while anyone has the page open. That rate hasn't been tried on a real OBS with real lobbies, and it isn't known whether a browser source that isn't in the live scene keeps drawing (if not, its pictures freeze).
    - **Do:** on the dry run, watch OBS's render and encode lag (the OBS pill) with the Watch page open and closed, and check that lobbies off stream keep moving; lower `MaxShotsPerSecond` if OBS suffers.
    - **Size:** S (the check), S (a lower rate).

75. **The Watch page is plain HTTP on the network.**
    - **Why:** the watch code, the sign-in's token and the pages (impostors included) travel unencrypted on the LAN, so anyone on the same network who can capture traffic can read them. The docs say to use it on a trusted network only.
    - **Do (if wanted):** HTTPS with a certificate made by Red Alert (the team accepts it once), or keep to trusted networks and say so on the Team watch card.
    - **Size:** M.

76. **The team signs in again after a restart.**
    - **Why:** sign-ins (and suggestions) are in memory only (`WatchServer.cs`, `Suggestions.cs`); the code is kept in `settings.json`, but after Red Alert restarts everyone on the Watch page goes back to the sign-in.
    - **Do (if wanted):** keep the sign-ins' tokens in a file of their own (never in a support bundle), cleared by **New code** and Team watch off.
    - **Size:** S.

77. **Top 3 over camera 1 with three or four player cams.**
    - **Why:** the Top 3 now steps aside wherever it would cover a picture (see [Done in 0.1.67](#done-in-0167)), but in the player cams layout with three or four cameras it still sits over camera 1: the graphics state has no camera boxes (`broadcast.html`, `top3At`).
    - **Do:** add the camera boxes to `/broadcast/state` (an added field) and let `top3At` check them, or hide the Top 3 in that layout.
    - **Size:** S.

### Red Alert's automatic actions (decisions pending)

From a read of everything Red Alert 0.3.17 does by itself (the file:lines are from that version). 60, 61, 62 and 68 were settled in 0.3.19 (see [Done in 0.1.67](#done-in-0167)); the rest haven't been changed yet.

59. **Which automatic switches go through Preview.** Auto switch and every other automatic switch go straight to Program; only auto grid uses Preview. Candidates for "into Preview, press Take": auto switch's destination, the camera going back to the map, the break ending, the grid closing up after a drop, the armed camera, a montage going back to intermission (see item 54). (Auto intermission and a MUST SHOW play ending a break are gone: see [Done in 0.1.67](#done-in-0167).) **Size:** M, once decided.

*60, 61 and 62 are done: see [Done in 0.1.67](#done-in-0167).*

63. **Graphics switches not obeyed:** player cards on replays ignore the **Player cards** switch (`BroadcastApp.cs:326-327`). (The win counter half no longer applies: the win counter was removed in 0.3.19.) **Size:** S.

64. **Swoosh off doesn't fully stop it.** Automatic switches carry no transition, and Red Alert sets OBS's transition to the TT Swoosh stinger whenever it exists, without checking Swoosh on or off, so scene changes from automatic switches can still swoosh. It also resets OBS's transition mid-show when a new lobby source is added (`ObsSwoosh.cs:66-77`; `ObsDirector.cs:397-401`). **Size:** S.

65. **The intermission offer's delay (45 s) has no UI.** (Auto grid is now kept across a restart: see [Done in 0.1.66](#done-in-0166).) **Size:** S.

66. **Auto clip saving and auto montages have no switch on screen** (obs.json only). **Size:** S.

67. **Hold doesn't stop the automatic actions that aren't switches:** Twitch polls, predictions and chat messages, auto clips, montage building, graphics, alerts, the after-game queue and audio. Decide which of them Hold should cover. **Size:** S–M.

*68 is done: see [Done in 0.1.67](#done-in-0167).*

69. **alerts.json's `max: 3` is unused:** banners show one at a time (the docs now say so). Use it or remove it. **Size:** S.

## Could do

### The mod

35. **Small referee ghost hardening.** Four separate small fixes:
    - (a) Home warns when the referee has a pet on. README.md:422 says to unequip it; no code checks.
    - (b) Hide the meeting card by player id, not by the name text (`RefSlot.cs:530`). Duplicate names or roster names can pick the wrong card.
    - (c) An in-bounds, unreachable parking spot per map, instead of 12 units below the map (`RefSlot.cs:208`), in case Innersloth adds position checks.
    - (d) Stop the game kicking the host itself, with a KickPlayer prefix.
    - **Size:** S each.

36. **Make the referee's only role CrewmateGhost**.
    - **Why:** It would remove the reliance on re-sending the referee's record every 5 s (`RefSlot.cs:147-148`).
    - **Only after:** item 3 passes, and only with host authority on.
    - **Size:** M, with alt-account testing.

37. **One limiter for everything the mod sends.**
    - **Why:** Innersloth says rate limits still apply under +25.
    - **Evidence:** NetLog records each send (`Safety.cs:117` on) but limits nothing.
    - **Size:** M.

### The Button

38. **"Host away" for the caster.**
    - **Why:** The tally is done (The Button's Home shows ON STREAM and NEXT to a host in the referee slot, see [Done in 0.1.66](#done-in-0166) and [Done in 0.1.67](#done-in-0167)), but the caster still can't tell when a host on stream has left the game window.
    - **Do:** The Button warns the caster, over the data link, when Among Us isn't the foreground window while the lobby is on stream.
    - **Size:** S.

39. **Onboarding.**
    - **Why:** README.md is 1,221 lines covering hosts, organisers and casters together. Preliminary hosts must create their own Discord bot in the Developer Portal. The new docs/features-*.md pages (added today) say what each app does, but not "what do I do, in order".
    - **Do:**
      - A one-page quick start per role.
      - A first-run checklist in The Button.
    - **Size:** M.

### Red Alert

*40 is done: see [Done in 0.1.66](#done-in-0166).*

41. **Notice a frozen or black lobby in OBS.**
    - **Why:** Health trusts the host's "frames encoded", so a source that froze in OBS stays green.
    - **Do:** compare successive `GetSourceScreenshot` images per TT Lobby source.
    - **Size:** M.

42. **Show-package graphics.**
    - **Do:** caster-name lower thirds, and Twitch stream markers at big plays. Neither exists in the code (grep). (The Starting soon screen is done: see [Done in 0.1.66](#done-in-0166).)
    - **Size:** S–M.

43. **Stream Deck or Companion from another PC, opt-in.**
    - **Why:** Red Alert's screen and Stream Deck links only listen on this PC (only the Watch page listens on the network, while Team watch is on).
    - **Do:** an opt-in LAN listener for `/deck/*`, with the key and a Host check.
    - **Size:** S–M.

44. **Remote co-caster.**
    - **Do:** VDO.Ninja guest sources in OBS, ducked under the host's mic.
    - **Size:** L.

73. **The broadcast delay, brought back later.**
    - **Why:** Red Alert 0.3.18 held every lobby's picture, voice and camera in OBS, and the graphics' game data, back a few seconds (0–4 s, 3 s by default), so the caster could cut to a play before it aired. The owner turned it off in 0.3.19 ("we'll come back to it later"; see [Done in 0.1.67](#done-in-0167)).
    - **Where it is:** the code is kept behind `BroadcastDelay.Enabled` (`false`, `ObsDelay.cs`), read through `ObsDirector.DelayAllowed`; its tests turn that on for their own director. Turning it on brings back the OBS card's setting, the setup check's delay line and its **Turn on a 3 s delay** fix, the graphics waiting (`BroadcastStream.cs`) and replays waiting for the play to reach OBS (`ReplayManager.cs`). `obs.json` may still hold 3 s from 0.3.18.
    - **When it comes back:** test it again ([test-list.md](test-list.md), New in 0.1.66, broadcast delay), and items 56, 57 and 71 apply again. The hand-added caster pages (`localhost:8767/` with `?full=1`, `/video`, `/multiview`) and the caster's mic aren't held back by it, and it's close rather than exact: Chromium treats VDO.Ninja's buffer as a hint.
    - **Size:** S to turn on and test; the open items with it S each.

### Stream Deck plugin

45. **A current runtime.**
    - (Lobby and Player cam keys now default to Preview: see [Done in 0.1.66](#done-in-0166).)
    - **Node:** the plugin runs on Node 20 (manifest `Nodejs 20`, package.json engines), which reached end of life in April 2026. Move when the Stream Deck app offers a newer runtime.
    - (The CI part is done: see [Done in 0.1.65](#done-in-0165).)
    - **Size:** S.

### Both apps

46. **Accessibility and language.**
    - **Accessibility:**
      - Status is mostly shown by colour (red / amber / green).
      - Red Alert's flashing pills ignore `prefers-reduced-motion` (0 matches in caster.html).
      - The send page has no aria attributes.
    - **Language:** every screen and every Discord post is English only.
    - **Size:** M (accessibility), L (translation).

47. **A Host check on the overlay ports.**
    - **Why:** It closes the remaining DNS-rebinding route to impostor roles; the app servers already check.
    - **Evidence:** `OverlayServer` reads only the request line (`OverlayServer.cs:44-60`). It serves `/state?full=1`, which includes roles, on 8765 (the mod) and 8767 (Red Alert's caster pages) with no Host check.
    - **Size:** S.

### The repository

48. **Line endings for Windows developers.**
    - **Why:** Git on Windows checks files out with CRLF line endings by default, and some tests fail on such a checkout. The Windows CI job sets `core.autocrlf false` to test the files as committed, so it doesn't show.
    - **Do:** decide whether to add a `.gitattributes` with `eol=lf`.
    - **Size:** S.

---

## Already done: don't redo

These ideas from earlier audits are in the code now, so they are not on the list:

- **Host authority and the referee:**
  - +25 host authority with the `hostauth on|off` command and the Home warning (`Patches/HostAuthority.cs`).
  - The host-only message guard (`HostAuthority.cs:101-129`).
  - `IsReferee` asks the game's own ghost-role list, so the spirit guide counts (`RefSlot.cs:450`).
- **Safe mode** against newer game versions and failed hooks, with "Run normally anyway" (`SafeMode.cs`).
- **Disconnects:** the reason and the mod's last 10 s of sends are logged and shown on Home (`DisconnectWatch.cs`, `Disconnects.cs`).
- **Discord:**
  - Unmute after a crash (`button-muted.json`, `DiscordBridge.cs:670`).
  - Gateway RESUME (`VoiceGateway.cs:628`; `GatewayResumeTests`).
- **Performance fixes:**
  - Buffered HTTP header parsing (`HttpRequest.cs:32-37`).
  - The `control.json` cache (`ModClient.cs:24-51`).
  - No per-frame `FindObjectOfType`; only `Drawing.MakeText` uses it.
  - Caster commands no longer echo into the host's chat or the mini chat (`SpectatorView.cs:157`).
- **The send page:** shares the Among Us window by default, the voice pump runs in a Worker, Lower quality, and Check my connection (`ConnectionCheck.cs`).
- **Red Alert's switching engine:**
  - Newest switch wins, sent as one batch, and source upkeep only runs when it's needed (`ObsSwitch.cs:10-95`).
  - Stream Deck status codes (401 / 404 / 409 / 503), camera guards with ARMED, and camera picks resent until the host has them.
  - Lobby numbers kept in `lobby-numbers.json`, and the port fallback to 8768–8771.
  - Host commands deduplicated per kind and capped at 200 (`BroadcastDeck.cs:34-58`).
- **Live desk:**
  - Preview, Take and Program, with the Program picture from OBS (`ObsOutputs.cs:198`).
  - OBS streaming, recording, dropped frames and disk warnings, and an "OBS not connected" warning.
  - Panic and Undo, Hold, Replay that, Play last, slow motion, and Cut / Swoosh / Fade.
  - Keyed list updates instead of rebuilding, and preview tiles that don't reconnect.
- **The Stream Deck plugin:** `/deck/state` long-poll, `/deck/img`, dials, and no impostor flags unless `&imp=1`.
- **Red Alert reliability:**
  - WebView2 anti-throttling flags and crash handlers (`BroadcastApp/Program.cs:26-28`, :53-55).
  - Logs roll over to `.1` instead of being deleted.
  - The sponsor log is cached by size and time (`Sponsors.cs:250-270`).
  - Game replays are stored as raw frames and streamed out (`GameReplays.cs:43`, :260).
  - Desk tick re-entry guard (`CasterDesk.cs:256-264`).
  - Replay clip clean-up by size, age and free disk.
- **Support bundles with redaction** in both apps, and the dry-run checklist.
- **The Referee tab on one screen,** with one void reason, the admin lobby list filled when the tab opens, and the "Not held for Submit" subtitle (`index.html:918`).

---

## Done in 0.1.67

Done in The Button and the mod 0.1.67 and Red Alert 0.3.19; the numbers are the items above.

- **22 (the broadcast delay off):** the owner's call, for now. Nothing is held back whatever `obs.json` says, the OBS card's **Broadcast delay** row is hidden, the setup check has no delay line (no **Turn on a 3 s delay**, no "No broadcast delay" warning), and asking for one answers "The broadcast delay is off in this version of Red Alert." The graphics and the lobby pictures are live again, and **Replay that** and the kill clips no longer wait for a delay. No OBS stream delay either: impostor information stays visible on stream. The code is kept behind `BroadcastDelay.Enabled` (`ObsDelay.cs`); bringing it back is item 73.
- **60, 61, 62 and 68 (no automatic screen changes for breaks or other reasons):** the owner's call. Auto intermission is gone, start and end: when every lobby has been out of a game for 45 s the desk only offers intermission (**Intermission** / **Not now**), and a lobby going live during it is only said ("LJ IS LIVE"); a person goes back (61 and 62 no longer apply). A MUST SHOW play no longer ends a sponsor break (68). The grid only changes by itself when a lobby on it drops (down 45 s, or gone quiet): the rest close up in their order, and a lobby joining or going to its menu, or a grid you picked, is left alone (60). The Live desk's **Auto → Intermission** button and its Stream Deck link are gone; `auto/intermission/…` answers 410 "Automatic switching was removed: people switch the stream." and `/app/admin/intermission` `action=auto` answers the same with ok false; an old Toggle key set to it reads AUTO INTER, *removed*, and sends nothing; an old `settings.json` with `AutoIntermission` still loads and it's ignored (`DeskMemory.cs`, `DeckState.cs`). Kept: auto switch off a dropped lobby (with Be right back and the dead-tile swap), the camera going back to the map, the grid closing up after a drop, a montage going back to intermission, armed cameras and the break's own timer, all still stopped by Hold.
- **Sponsor breaks can't be interrupted** (`CasterDeskBreak.cs`): a lobby picked during a break (a cut, Take, a tile, the multiview pick, Stream Deck lobby, Take and camera keys, Undo to a lobby) goes into the break's picture and the break carries on with its sponsor, its time and its sponsor appearance. BRB, Intermission, Starting soon, another break, a replay or montage (`ReplayManager.PlayAsync`), **Replay that** and an Undo back to one of those are refused with "A sponsor break is on: N s left. End break first." (409 on the Stream Deck). It ends only when its time is up, with **End break** (`break/off`, `/app/admin/sponsor` `end`) or with Panic, and then goes full screen on the break's lobby as it is then. If the break's lobby drops, auto switch puts a healthy lobby (a hot one, else the top one) in the break; with none, the break stays with RECONNECTING. Switching scenes in OBS itself still ends a break.
- **Red Alert's visual fixes and the win counter removed:** the win counter is gone from the stream (the corner, Intermission, Be right back), the Graphics page and the intermission card's Today / Round; `/broadcast/state` has no `wins`, `POST /app/admin/wins` is gone (404), and an old `broadcast.json` with `winCounter` still loads and drops it (`BroadcastApp.cs`). The desk still keeps `WinScope` in `settings.json`, unused. Grid tiles and their status lines stop above the stats ticker while it shows (`broadcast.html`). The Live desk's action panel is the Replays row, then the Breaks rows, then the cards, and only the cards scroll (the break that's on has its card first); a sponsor read and the Starting soon or Intermission card fit at 1600×900 and 1920×1080. The Replays row shows only whole buttons (long titles end in "…"); the Best replay button reads **▶ Best**; the Starting soon card has two rows with a **Line** label; the Program line says "by hand" and "scene TT Full"; up to four lobbies get four wide tiles; the Twitch log says "Nothing yet." when empty; the Stream Deck + replay dial's hint is clear of its border (`svg.js`).
- **72 (decided):** the On stream light (ON STREAM and NEXT) shows only to a host in the referee slot (the referee ghost, not playing); a playing host never sees it, since it could tell them something happened in their game (`AppServer.cs`, `RefereeHost`). The mod's status has `refSlotActive` (the slot on and not in safe mode this game, `ControlApi.cs`); with an older mod, The Button goes by the referee slot on and safe mode off.
- **Team watch** (`WatchServer.cs`, `BroadcastWatch.cs`, `WatchCapture.cs`, `Suggestions.cs`, `LobbyEvents.cs`, `ui/watch.html`): a Watch page for the production team on their own PCs on the network, on port 8780 (or the next free one up to 8789), listening only while Settings → **Team watch** is on (off by default), behind an 8-character watch code. Each lobby's card has its picture (from OBS while it's connected, else the host's own video), state, players with impostors, last 8 events and **Show this**. Wrong codes are limited (5 a minute per address, 40 overall), **New code** or Team watch off signs everyone out, every request needs the sign-in's token, the code is never logged and is taken out of support bundles. On the Live desk, **Happening now** is replaced by **Suggestions** (the desk's own items, then the team's, with **Into Preview**, **Cut** and **×**, cleared on Program, after 2 minutes or when dismissed); plays move to the **Plays** drawer, with Save replay / ▶ REPLAY, Preview and Card; the side menu's number counts new suggestions. The Stream Deck gains `suggest/next`, a Lobby key option **Newest team suggestion**, and `suggestions` in `/deck/state` (its words only with `&imp=1`).
- **The Top 3 steps aside over pictures** (`broadcast.html`, `top3At`): it's hidden in the quad, a grid of two or more lobbies and the sponsor break, and shows as before over full screen, 2-up, a one-lobby grid and a player cam. Three or four player cams are item 77.
- **More below on the action panel** (`caster.html`): while the cards go on past the bottom, the panel fades out there with a **▾ More below** button that scrolls down, gone at the end; the cards are a little shorter, so Intermission with a prompt and a sponsor read fits at 1600×900.
- **Sponsor breaks ended early** (checked, no code change): a break ended with **End break**, or with its lobby changed by a cut or a camera, logs one appearance for its time on screen (`SponsorBreakTests.cs`); simulated breaks are never logged, which is why a rehearsal in Simulation counts none. The logged lobby is the one the break started with.

## Done in 0.1.66

Done in The Button and the mod 0.1.66 and Red Alert 0.3.18; the numbers are the items above.

- **20 (the handler):** a crashed or hung page (in Red Alert and The Button) is loaded again by itself, with a new browser if WebView2's own closed, at most 3 times a minute, and logged (`Common/PageRecovery.cs`, `MainForm.cs`); past that the window says so. While Red Alert's desk page is gone, or for 20 s after it's back, lobbies without data aren't counted as down, so auto switch, LOBBY DOWN cards and interrupted games wait (`CasterDeskPage.cs`). The receivers in a second WebView2 are still item 20.
- **21:** Red Alert keeps the lobbies and their links in `lobbies-cache.json` (no players or impostors; same results channel; lobbies seen in the last 18 hours; deleted on Lock; never in a support bundle, and its VDO.Ninja passwords are taken out of every file), so they're back at once after a restart; their tiles say CACHED until the results channel confirms them (`Common/OrganizerCache.cs`).
- **22 (decided):** no OBS stream delay; Red Alert's own broadcast delay (0–4 s, 3 s by default, `ObsDelay.cs`, `BroadcastStream.cs`) holds every lobby's picture, voice and camera in OBS and the graphics' game data back; switches, intermission, Be right back, Starting soon and replays stay live. The setup check's fix is **Turn on a 3 s delay**, and the docs no longer ask for an OBS delay. Impostor information stays visible on stream.
- **24:** "Jump to action" is now **Best replay**, one pick made by Red Alert for the desk's N, its button and the Stream Deck (`BestReplay.cs`): the highest rated replay from the last 3 minutes not played yet goes into Preview, and Take plays it. The old `lobby/action` and `action` links do the same.
- **25:** Hold, auto grid, auto intermission, the next-round countdown, Starting soon's countdown and line, and the win counter's and standings' scope are kept across a restart (`settings.json` `Desk`, `DeskMemory.cs`).
- **38 (the tally):** each host's The Button shows ON STREAM (and how) or NEXT on Home, sent by Red Alert over the lobby's own data link (`HostTally.cs`, `AppCore/StreamTally.cs`, `ttt` in broadcast-protocol.md). The "host away" warning is still item 38.
- **40:** Hear Preview: **HEAR** on the Live desk's Preview plays the lobby in Preview live in the caster's headphones through one Monitor Only **TT Cue** source, never on stream, held back while Desktop Audio captures; the setup check looks at OBS's monitoring device (`ObsCue.cs`).
- **42 (Starting soon):** the **TT Starting Soon** screen with a countdown, the tournament's name and logo, a line of the caster's own and sponsor turns; nothing switches by itself while it's on (`CasterDeskSoon.cs`, `BroadcastSoon.cs`).
- **45 (Lobby keys):** new Lobby and Player cam keys select into Preview and the Take key puts them on stream; keys set up with an older plugin keep cutting.
- **Found in review and fixed:** a Player cam key set to Preview no longer changes the shot on stream before Take when that camera is already on stream (and an armed camera for the lobby is taken back); TT Cue set to "Monitor and Output" by hand in OBS is muted at once and put back to Monitor Only; the Stream Deck's Best replay answer names no players (its plugin keeps a log) unless `&imp=1`; Lock waits for a write of `lobbies-cache.json` already under way before deleting it; Starting soon's countdown and line are kept across a restart; the setup check counts a Default monitoring device as OK (with a note).
- **Also fixed:** The Button's voice bot sends its join and leave messages to Discord one at a time, so a rejoin can no longer arrive after a leave and put the bot back in a host's voice channel (`VoiceGateway.cs`).

## Done in 0.1.65

Done in The Button and the mod 0.1.65 and Red Alert 0.3.17; the numbers are the items above.

- **10 (the apps):** The Button's own update, the mod bundle and Red Alert's update are used only when their size and SHA-256 match the release (`Common/ReleaseAsset.cs`); a mismatch is deleted and nothing changes. The installers are still item 10.
- **13:** a Windows CI job (build.yml `windows`) builds and tests everything on Windows, starts both apps, checks the installer scripts in Windows PowerShell 5.1 and runs both .bat installers from a folder with an apostrophe in its name, which found and fixed a real bug: such a folder stopped the installers.
- **15:** safe saving everywhere (`Core/SafeFile.cs`): each file is written to a side file and swapped in, and one that can't be read is kept as `<name>.corrupt-<time>`; Home shows "A damaged file was set aside".
- **16:** The Button backs up the mod's games, links, waiting list, rounds and totals to `%LOCALAPPDATA%\TheButton\backup` (`AppCore/ModBackup.cs`), with Settings → Help → **Restore backup** and a Home notice when the backup has games the mod's folder doesn't.
- **23:** auto grid only fills Preview and Take puts it on; it never replaces a Preview the caster picked. Other automatic switches still go on by themselves: auto intermission, auto switch off a dropped lobby, a break ending, a camera going back to the map, armed cameras, the grid closing up, a montage going back to intermission.
- **31 (most):** the README, the features pages, Red Alert's help texts, its release notes and the plugin's project file match the code (buttons that moved, the top bar, the player camera's size, every kill heard, the overlay's 360×720, the OBS scenes and source name, the replay viewer). What's left is item 31.
- **32:** F9 and Unmute all point to **Automute** on The Button's Home, `feed on` says to share the Among Us window, the Automute card mentions Your Discord, + / − shows "Referee change", and the overlay's reply says 360×720.
- **33:** an administration code works on a PC without Among Us (kept in The Button's own settings); Home says "Organiser PC" with **Open the Organiser tab**.
- **34 (most):** commands typed in Discord do nothing; every points and round change is made in The Button (Referee tab → **Results channel**, Organiser tab → **Rounds**, with **Start over…** for the combined standings). What's left is item 34.
- **45 (CI):** the Windows job runs `npm run validate`.
