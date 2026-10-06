using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker
{
    public sealed partial class TournamentSession
    {
        public const string VoiceNoticeText = "🎙️ Heads up: voice in this channel may be recorded and played on the tournament broadcast while games are on.";
        private readonly Dictionary<string, DateTime> _voiceNoticeAt = new Dictionary<string, DateTime>();

        /// <summary>
        /// Lobby voice on the broadcast (Part 11): a notice in the game's voice channel chat that its voice
        /// may be recorded. The referee's Button sends this when it starts sending the lobby's voice;
        /// at most once every 30 minutes per channel.
        /// </summary>
        private void VoiceNoticeCommand()
        {
            string? token = _settings.AutoMute.BotTokens.FirstOrDefault();
            string? channel = token == null ? null : GameVoiceChannel(Players);
            if (token == null || channel == null)
            {
                Reply("No voice channel found for this lobby (automute with a bot, and players in voice, are needed).", false);
                return;
            }
            var now = _clock();
            lock (_voiceNoticeAt)
            {
                if (_voiceNoticeAt.TryGetValue(channel, out var at) && now - at < TimeSpan.FromMinutes(30))
                {
                    Reply("The voice notice is already up in this lobby's voice channel.", false);
                    return;
                }
                _voiceNoticeAt[channel] = now;
            }
            Chain(async () =>
            {
                var result = await _rest.PostMessageAsync(token, channel, VoiceNoticeText).ConfigureAwait(false);
                if (!result.Ok) lock (_voiceNoticeAt) _voiceNoticeAt.Remove(channel);
            });
            Reply("Posted the voice notice in this lobby's voice channel.", false);
        }
    }
}
