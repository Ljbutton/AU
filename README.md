# Among Us Tournament Tracker

A **host-only** Among Us mod for running tournaments. Only the host installs it; players
use the normal game. It:

* **Tracks stats** for every game: kills, deaths, ejections, meetings, body reports, votes
  (correct, wrong, skipped, missed), tasks, sabotages, disconnects, wins and losses by team.
* **Keeps a tournament leaderboard** with configurable points, saved between sessions.
* **Posts to Discord** through a webhook: a report after each game (player table, impostors,
  MVP, full timeline) and the updated leaderboard. You can also turn on a live play-by-play.
* **Automutes Discord voice**: alive players are muted (and deafened) during tasks, everyone
  alive can talk in meetings, and dead players talk among themselves during tasks. It works
  like AutoMuteUs, but the host's game drives it directly, so no capture app is needed.

The host's client runs the game, so it sees every kill, vote and role. That is why only the
host needs the mod.

---

## Install (host only)

1. Install **BepInEx 6 bleeding-edge build 735 (IL2CPP)**, the build Among Us mods use:
   download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.735` from
   <https://builds.bepinex.dev/projects/bepinex_be>, extract it into the Among Us folder, and
   start the game once so BepInEx sets itself up.
2. Download `TournamentTracker-*.zip` from this repo's Releases (or from the latest
   **Build** run under Actions). Extract it into the Among Us folder so
   `BepInEx/plugins/TournamentTracker.dll` lands in place.
3. Start the game once, then close it. It creates
   `BepInEx/config/com.ljbutton.tournamenttracker.cfg`; fill that in (see below).

`BUILT-AGAINST.txt` in the ZIP names the Among Us version the DLL was compiled for. After
Among Us updates, rebuild against the new version (see *Building*).

## Discord setup

### Stats (webhook, no bot needed)

Channel settings → Integrations → Webhooks → New Webhook → Copy URL. Paste the URL into
`StatsWebhookUrl`. If you want a live feed of kills and meetings, make a second webhook in a
**staff-only** channel (the feed reveals the impostors) and use it for `LiveFeedWebhookUrl`.

### Automute (bot)

1. Create an application at <https://discord.com/developers/applications>. Under **Bot**,
   reset the token and copy it. The bot needs no privileged intents.
2. Invite the bot with the Mute Members and Deafen Members permissions:
   `https://discord.com/oauth2/authorize?client_id=YOUR_APP_ID&scope=bot&permissions=12582912`
3. Turn on Developer Mode in Discord, right-click your server → **Copy Server ID**.
4. In the config, set `[AutoMute] Enabled = true`, `BotTokens = <token>` and `GuildId = <server id>`.

Discord rate-limits member edits for each bot. With 10–15 linked players, one bot can take a
few seconds to mute everyone. To make that faster, invite two or three bots and list every
token, comma separated. The work is spread across all of them.

**Keep the config file private.** Anyone with the bot token can control that bot.

### Linking players to Discord

Automute only touches players linked to a Discord account. Links are keyed by friend code,
so a link survives name and colour changes, and they're saved in
`BepInEx/config/TournamentTracker/links.json`.

* Players type **`!link their_discord_username`** (or their numeric user ID, or
  `<@id>`) in the lobby chat.
* The host can link anyone: **`!link red coolbean`** or **`!link CoolBean 1234…`**.
* Or fill in `links.json` before the event (see `links.example.json`). A player's key is their
  friend code in lower case, e.g. `coolbean#1234`.

Unlinked players, casters and spectators in the voice channel are never muted.

## Chat commands

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
| `!leaderboard` | host | Post the leaderboard to Discord now |
| `!resetstats confirm` | host | Archive the stats file and start a new leaderboard |

\* unless `AllowSelfLink = false`.

When the game closes, the mod unmutes everyone it muted before it exits.

## Configuration

`BepInEx/config/com.ljbutton.tournamenttracker.cfg`:

| Section | Setting | Default | |
| --- | --- | --- | --- |
| General | `TournamentName` | Among Us Tournament | A new name starts a new leaderboard. The old one is kept. |
| General | `CommandPrefix` | `!` | |
| General | `AllowSelfLink` | true | |
| Discord | `StatsWebhookUrl` | | Game reports and leaderboard |
| Discord | `LiveFeedWebhookUrl` | | Optional play-by-play |
| Discord | `PostLeaderboardAfterEachGame` | true | |
| Discord | `LeaderboardSize` | 15 | |
| AutoMute | `Enabled` | false | |
| AutoMute | `BotTokens` | | Comma separated |
| AutoMute | `GuildId` | | |
| AutoMute | `DeafenAliveDuringTasks` | true | Alive players can't hear the dead |
| AutoMute | `DeadCanTalkDuringTasks` | true | |
| AutoMute | `MuteDeadDuringMeetings` | true | |
| Scoring | *(see below)* | | Every point value on the tournament sheet |

## Scoring

The defaults follow the **Point Sheet Template** tab of the tournament spreadsheet
(`Tournament_Points.xlsx`). Each value is a line under `[Scoring]` in the config. Halves are
allowed, penalties are negative, and 0 switches a rule off.

| Impostor | Points | Config key |
| --- | --- | --- |
| Kill | +1 each | `Kill` |
| First blood (on top of the kill) | +1 | `FirstBlood` |
| Voted for a crewmate who got ejected | +1 each | `VotedCrewmateOut` |
| Win by sabotage | +5 | `ImpostorSabotageWin` |
| Win by vote | +3 | `ImpostorVoteWin` |
| Win by kills | +3 | `ImpostorKillWin` |
| Lose to tasks | −3 | `ImpostorTaskLoss` |
| Lose to vote | −2 | `ImpostorVoteLoss` |
| First impostor voted out | −2 | `VotedOutFirst` |
| Later impostor voted out | −1 | `VotedOutLast` |

| Crewmate | Points | Config key |
| --- | --- | --- |
| Finished every task | +3 | `CompletedTasks` |
| Voted for an impostor who got ejected | +2 each | `CorrectVoteOut` |
| Called the meeting where an impostor got ejected | +1 | `CaughtKiller` |
| First player killed | +1 | `DiedFirst` |
| Killed (not first) | +0.5 | `GotKilled` |
| Voted for a crewmate who got ejected | −2 each | `IncorrectVoteOut` |
| Win by tasks | +5 | `CrewTaskWin` |
| Win by vote | +3 | `CrewVoteWin` |
| Alive when the team loses to sabotage | −5 | `CrewSabotageLossAlive` |
| Any other loss | −1 | `CrewOtherLoss` |

Votes only score when they put someone out: a vote for an impostor who survives the meeting
earns nothing. Win and loss points go to the whole team, dead or alive, but not to anyone
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

A game the host leaves before it ends is saved as `-abandoned` and is not counted.

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
