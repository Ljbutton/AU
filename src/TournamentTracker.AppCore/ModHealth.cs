using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace TournamentTracker.App
{
    /// <summary>
    /// What the mod says about its own health, for Home: safe mode (the mod switched its in-game changes off
    /// because this Among Us isn't the version it was built for) and the last time Among Us was disconnected,
    /// with why in plain words. Both come in the mod's status; a mod from before them sends neither, and
    /// then there's nothing to show.
    /// </summary>
    public static class ModHealth
    {
        /// <summary>A disconnect is shown on Home for this long.</summary>
        public static readonly TimeSpan DisconnectShownFor = TimeSpan.FromHours(1);

        public sealed class SafeModeView
        {
            public bool On { get; set; }
            /// <summary>Running normally on a game version safe mode would be on for: the host chose that ("safemode off").</summary>
            public bool Overridden { get; set; }
            public string? Reason { get; set; }
            public string? GameVersion { get; set; }
            public string? BuiltFor { get; set; }
            /// <summary>"Safe mode: …" or "Running normally on …".</summary>
            public string Title { get; set; } = "";
            public string Say { get; set; } = "";
        }

        public sealed class DisconnectView
        {
            public DateTime At { get; set; }
            public string Reason { get; set; } = "";
            public string? Detail { get; set; }
            public bool InGame { get; set; }
            /// <summary>In the last hour, and not the host leaving on purpose: Home shows it.</summary>
            public bool Show { get; set; }
            public int MinutesAgo { get; set; }
            /// <summary>"You were disconnected: the connection timed out".</summary>
            public string Title { get; set; } = "";
            /// <summary>What it means and what to do.</summary>
            public string Explain { get; set; } = "";
        }

        /// <summary>The mod's safe mode from its status, or null (an older mod, or no status).</summary>
        public static SafeModeView? SafeMode(JsonElement? status)
        {
            if (status is not JsonElement s || s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("safeMode", out var m) || m.ValueKind != JsonValueKind.Object) return null;
            var view = new SafeModeView
            {
                On = m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True,
                Overridden = m.TryGetProperty("overridden", out var ov) && ov.ValueKind == JsonValueKind.True,
                Reason = Text(m, "reason"),
                GameVersion = Text(m, "gameVersion"),
                BuiltFor = Text(m, "builtFor"),
            };
            string versions = view.GameVersion != null && view.BuiltFor != null ? $"This Among Us is {view.GameVersion}; the mod was built for {view.BuiltFor}."
                : view.GameVersion != null ? $"This Among Us is {view.GameVersion}." : "";
            if (view.On)
            {
                view.Title = "Safe mode: " + (view.Reason ?? "the mod turned its in-game changes off");
                view.Say = (versions + " The mod keeps Among Us stable by not changing the game: update the mod (Settings) when a new version is out. "
                    + "Run normally anyway only if the organiser says this game version is fine; if Among Us then crashes or misbehaves, go back to automatic.").Trim();
            }
            else if (view.Overridden)
            {
                view.Title = "Running normally on a game version the mod wasn't built for";
                view.Say = (versions + " You chose this (Run normally anyway). If Among Us crashes or misbehaves, go back to automatic: safe mode comes back on for this version.").Trim();
            }
            return view;
        }

        /// <summary>The last disconnect from the mod's status, or null (none, an older mod, or unreadable).</summary>
        public static DisconnectView? LastDisconnect(JsonElement? status, DateTime utcNow)
        {
            if (status is not JsonElement s || s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("lastDisconnect", out var d) || d.ValueKind != JsonValueKind.Object) return null;
            string? atText = Text(d, "at");
            if (atText == null || !DateTime.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
            string reason = Text(d, "reason") ?? "Unknown";
            string? detail = Text(d, "detail");
            var ago = utcNow - at;
            var view = new DisconnectView
            {
                At = at,
                Reason = reason,
                Detail = detail,
                InGame = d.TryGetProperty("inGame", out var ig) && ig.ValueKind == JsonValueKind.True,
                MinutesAgo = (int)Math.Max(0, Math.Floor(ago.TotalMinutes)),
                Show = ago >= TimeSpan.FromMinutes(-5) && ago <= DisconnectShownFor && !OnPurpose(reason),
            };
            var (what, todo) = Explain(reason);
            // A custom reason (from the server or the host) is the reason itself.
            if (reason == "Custom" && detail != null) { what = detail; detail = null; }
            view.Title = "You were disconnected: " + what;
            view.Explain = string.Join(" ", new[] { detail, todo, view.InGame ? "It happened during a game: check the Referee tab before that game's points go to Discord." : null }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            return view;
        }

        /// <summary>The host left (to the main menu, closed the game): not a problem to show.</summary>
        public static bool OnPurpose(string reason) => reason is "ExitGame" or "IntentionalLeaving";

        private const string Connection = "Check your internet (a wired connection is steadier than Wi-Fi) and close big uploads or downloads; Settings → Check my connection tests it.";

        /// <summary>
        /// Among Us's DisconnectReasons, in plain words: what happened, and what to do about it. Unknown
        /// names (a newer game) get a general answer.
        /// </summary>
        public static (string What, string? Todo) Explain(string reason) => reason switch
        {
            "ExitGame" or "IntentionalLeaving" => ("you left the game", null),
            "GameFull" => ("the lobby was full", "Ask the host to make room, or check the lobby's player limit."),
            "GameStarted" => ("the game had already started", "Wait for the lobby to come back after the game."),
            "GameNotFound" => ("the lobby wasn't found", "Check the code and the server region: the lobby may have closed."),
            "IncorrectVersion" or "MismatchedVersion" => ("your Among Us version doesn't match the server's or the lobby's", "Update Among Us (Steam or Epic), then the mod (Settings → Update), and try again."),
            "Banned" => ("you were banned from that lobby", "Talk to that lobby's host."),
            "Kicked" => ("the host kicked you", null),
            "Custom" => ("the server sent its own reason", null),
            "InvalidName" => ("Among Us didn't accept your name", "Change your name in Among Us and try again."),
            "Hacking" => ("Among Us's anti-cheat disconnected you", "This can happen when the game and a mod disagree. Save a support bundle (Settings → Help) and send it to the organiser."),
            "NotAuthorized" or "ErrorAuthNonceFailure" or "InternalConnectionToken" => ("Among Us couldn't confirm your account with the server", "Restart Among Us and sign in again."),
            "ConnectionLimit" => ("too many Among Us connections from your network", "Close other copies of Among Us on this network, or wait a few minutes."),
            "Destroy" => ("the server closed the lobby", "Host a new lobby."),
            "Error" or "ServerError" => ("the Among Us server had a problem", "Wait a minute and try again, or try another region."),
            "IncorrectGame" => ("that lobby is on another server", "Switch to the lobby's server region."),
            "ServerRequest" => ("the server asked you to leave", "Wait a minute and try again."),
            "ServerFull" or "NoServersAvailable" or "MatchmakerFull" => ("the Among Us servers were full", "Wait a minute and try again, or try another region."),
            "ServerNotFound" => ("the Among Us server couldn't be reached", Connection),
            "ClientTimeout" => ("the connection to the Among Us server timed out", Connection),
            "InternalPlayerMissing" or "InternalNonceFailure" => ("Among Us lost its place on the server (a connection hiccup)", Connection),
            "FocusLost" or "FocusLostBackground" => ("Among Us lost its connection while it was in the background", "Keep Among Us open on screen (not minimised), and turn off Windows' battery saver."),
            "NewConnection" or "DuplicateConnectionDetected" => ("your account connected again from somewhere else", "Make sure only one copy of Among Us is open with your account."),
            "LobbyInactivity" or "MatchmakerInactivity" => ("the lobby was closed for being idle too long", "Host again; start games sooner or keep someone active in the lobby."),
            "InvalidGameOptions" => ("the server didn't accept the lobby settings", "Check the lobby settings (the settings lock, roles); tell the organiser if it keeps happening."),
            "PlatformLock" or "SelfPlatformLock" => ("cross-play settings block that lobby", "Check Among Us's cross-play setting."),
            "PlatformParentalControlsBlock" or "PlatformUserBlock" or "PlatformFailedToGetUserBlock" => ("your platform's account settings blocked it", "Check the parental controls or blocks on your Steam, Epic or console account."),
            "QuickmatchDisabled" or "QuickchatLock" or "Sanctions" => ("your Among Us account has a restriction on it", "Check your account in Among Us; tell the organiser."),
            "TooManyGames" or "TooManyRequests" => ("too many games or requests in a short time", "Wait a minute and try again."),
            _ => ("Among Us didn't say why (" + reason + ")", Connection),
        };

        private static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
    }
}
