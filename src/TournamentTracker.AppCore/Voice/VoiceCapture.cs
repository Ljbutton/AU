using System;
using System.Collections.Generic;
using System.Linq;

namespace TournamentTracker.App.Voice
{
    /// <summary>
    /// The lobby's sound for the broadcast, on the referee's PC (Part 11): Discord's sound (the lobby
    /// voice as the referee hears it, which never includes the referee's own voice) and Among Us's
    /// sound, each captured on its own, mixed with a level for each. The send page pulls the mix,
    /// adds the referee's microphone if they want it, and sends it to the caster as its own stream.
    /// Nothing here plays anything back.
    /// </summary>
    public sealed class VoiceCapture : IDisposable
    {
        public static readonly string[] DiscordNames = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };
        public static readonly string[] GameNames = { "Among Us" };
        private const int MaxBacklog = ProcessAudio.SampleRate / 2;      // half a second: older sound is dropped
        private readonly object _lock = new object();
        private readonly Source _voice = new Source(), _game = new Source();
        private ProcessAudio? _voiceCapture, _gameCapture;
        private readonly Func<DateTime> _clock;
        private DateTime _lastRead;

        private sealed class Source
        {
            public readonly Queue<short> Samples = new Queue<short>();
            public int Peak;
            public DateTime PeakAt;
        }

        /// <summary>0–1.5; the lobby voice and the game's own sound.</summary>
        public double VoiceLevel { get; set; } = 1.0;
        public double GameLevel { get; set; } = 0.5;

        public VoiceCapture(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

        public bool Running => _voiceCapture != null;

        /// <summary>Starts capturing (on Windows 10 2004 or later; elsewhere it says it can't).</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_voiceCapture != null) return;
                _voiceCapture = new ProcessAudio(DiscordNames, (b, n) => Add(_voice, b, n));
                _gameCapture = new ProcessAudio(GameNames, (b, n) => Add(_game, b, n));
            }
            _voiceCapture.Start();
            _gameCapture!.Start();
        }

        public void Stop()
        {
            ProcessAudio? v, g;
            lock (_lock) { v = _voiceCapture; g = _gameCapture; _voiceCapture = _gameCapture = null; _voice.Samples.Clear(); _game.Samples.Clear(); }
            v?.Dispose();
            g?.Dispose();
        }

        /// <summary>Takes captured frames (also used by tests and simulation).</summary>
        public void AddVoice(short[] interleaved, int frames) => Add(_voice, interleaved, frames);
        public void AddGame(short[] interleaved, int frames) => Add(_game, interleaved, frames);

        private void Add(Source s, short[] buffer, int frames)
        {
            int peak = 0;
            lock (_lock)
            {
                // Nobody's reading (the send page is closed): don't pile up.
                if ((_clock() - _lastRead).TotalSeconds > 2) { s.Samples.Clear(); }
                for (int i = 0; i < frames * ProcessAudio.Channels; i++)
                {
                    s.Samples.Enqueue(buffer[i]);
                    int a = Math.Abs((int)buffer[i]);
                    if (a > peak) peak = a;
                }
                while (s.Samples.Count > MaxBacklog * ProcessAudio.Channels) s.Samples.Dequeue();
                var now = _clock();
                if (peak >= s.Peak || (now - s.PeakAt).TotalMilliseconds > 300) { s.Peak = peak; s.PeakAt = now; }
            }
        }

        /// <summary>
        /// The mix since the last read: interleaved 48 kHz stereo 16-bit samples, little-endian. When
        /// both programs are sending, as much as both have; when one is quiet or closed, the other alone.
        /// </summary>
        public byte[] Read()
        {
            lock (_lock)
            {
                _lastRead = _clock();
                bool v = _voiceCapture?.State == "capturing" || _voice.Samples.Count > 0;
                bool g = _gameCapture?.State == "capturing" || _game.Samples.Count > 0;
                int n = v && g ? Math.Min(_voice.Samples.Count, _game.Samples.Count) : Math.Max(_voice.Samples.Count, _game.Samples.Count);
                // A program that stopped sending sound (e.g. Discord with nobody talking still sends silence; a closed one doesn't) mustn't hold the mix up.
                if (v && g && n == 0 && Math.Max(_voice.Samples.Count, _game.Samples.Count) > ProcessAudio.SampleRate / 5 * ProcessAudio.Channels)
                    n = Math.Max(_voice.Samples.Count, _game.Samples.Count);
                n -= n % ProcessAudio.Channels;
                var bytes = new byte[n * 2];
                for (int i = 0; i < n; i++)
                {
                    double mixed = (_voice.Samples.Count > 0 ? _voice.Samples.Dequeue() * VoiceLevel : 0)
                                 + (_game.Samples.Count > 0 ? _game.Samples.Dequeue() * GameLevel : 0);
                    short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(mixed)));
                    bytes[i * 2] = (byte)(s & 0xff);
                    bytes[i * 2 + 1] = (byte)((s >> 8) & 0xff);
                }
                return bytes;
            }
        }

        /// <summary>Level in dBFS over the last moment (−60 when quiet).</summary>
        private double Db(Source s)
        {
            if ((_clock() - s.PeakAt).TotalSeconds > 1 || s.Peak <= 0) return -60;
            return Math.Max(-60, Math.Round(20 * Math.Log10(s.Peak / 32768.0), 1));
        }

        public object State()
        {
            lock (_lock)
                return new
                {
                    Supported = ProcessAudio.Supported,
                    Running,
                    Voice = new { State = _voiceCapture?.State ?? "off", Level = Db(_voice) },
                    Game = new { State = _gameCapture?.State ?? "off", Level = Db(_game) },
                    VoiceLevel, GameLevel,
                };
        }

        public void Dispose() => Stop();
    }
}
