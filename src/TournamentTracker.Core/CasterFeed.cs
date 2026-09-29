using System;
using System.Linq;
using System.Security.Cryptography;

namespace TournamentTracker
{
    /// <summary>
    /// "Send my game to the caster": the host shares their Among Us window through VDO.Ninja
    /// (a free, private browser-to-browser video link), and the caster's copy of The Button
    /// shows it. The link's name and password are random, kept for the tournament, and only
    /// ever written in the "Live data" message in the private results channel, where the
    /// organiser's view reads them.
    /// </summary>
    public sealed partial class TournamentSession
    {
        public const string VdoNinja = "https://vdo.ninja/";

        private string? _feedId, _feedKey;
        private bool _feedOn;

        /// <summary>Only tournament hosts with a bot (whose live data the organiser reads) can send their game.</summary>
        public bool FeedAvailable => LivePublishOn;

        /// <summary>The page the host opens in their browser to share the game. Public for tests.</summary>
        public string FeedPushUrl
        {
            get
            {
                EnsureFeedId();
                string label = LobbyLabel();
                return $"{VdoNinja}?push={_feedId}&password={_feedKey}&screenshare&label={Uri.EscapeDataString(label.Length > 0 ? label : "Lobby")}";
            }
        }

        /// <summary>"id:password" for the live data while the host is sending their game.</summary>
        private string? FeedForLive => _feedOn && FeedAvailable && _feedId != null ? _feedId + ":" + _feedKey : null;

        private void EnsureFeedId()
        {
            if (_feedId != null && _feedKey != null) return;
            _feedId = "tt" + RandomText(14);
            _feedKey = RandomText(16);
            SaveState();
        }

        private static string RandomText(int length)
        {
            const string letters = "abcdefghijkmnpqrstuvwxyz23456789";
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return new string(bytes.Select(b => letters[b % letters.Length]).ToArray());
        }

        /// <summary>feed on / feed off (from The Button's "Send my game to the caster" switch).</summary>
        private void FeedCommand(string[] args)
        {
            string arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (!FeedAvailable)
            {
                Reply("Sending your game to the caster needs a tournament host code with a bot and a private results channel.", false);
                return;
            }
            if (arg == "on") { EnsureFeedId(); _feedOn = true; SaveState(); }
            else if (arg == "off") { _feedOn = false; SaveState(); }
            Reply(_feedOn
                ? "Sending your game to the caster: in the browser page that opened, pick the Among Us window and leave the tab open while you play."
                : "Not sending your game to the caster. Close the VDO.Ninja tab in your browser.", false);
        }
    }
}
