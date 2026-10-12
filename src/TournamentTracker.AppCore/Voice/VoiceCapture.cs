using System;

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
        /// <summary>
        /// When one program is capturing but sends nothing for this long (Windows only delivers sound a
        /// program actually plays), the other goes out on its own instead of waiting. Below the send
        /// page's 0.12 s start buffer, so a lone source arrives in small steady pieces, not long bursts.
        /// </summary>
        private const int StallFrames = ProcessAudio.SampleRate / 12;
        private readonly object _lock = new object();
        private readonly Source _voice = new Source(), _game = new Source();
        private ProcessAudio? _voiceCapture, _gameCapture;
        private readonly Func<DateTime> _clock;
        private DateTime _lastRead, _lastAsked;

        private sealed class Source
        {
            public readonly SampleRing Samples = new SampleRing(MaxBacklog * ProcessAudio.Channels);
            public int Peak;
            public DateTime PeakAt;
        }

        /// <summary>0–1.5; the lobby voice and the game's own sound.</summary>
        public double VoiceLevel { get; set; } = 1.0;
        public double GameLevel { get; set; } = 0.5;

        public VoiceCapture(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

        public bool Running { get { lock (_lock) return _voiceCapture != null; } }

        /// <summary>Starts capturing (on Windows 10 2004 or later; elsewhere it says it can't).</summary>
        public void Start()
        {
            ProcessAudio v, g;
            lock (_lock)
            {
                if (_voiceCapture != null) return;
                _lastAsked = _clock();          // started for a page that's about to read: not idle yet
                v = _voiceCapture = new ProcessAudio(DiscordNames, (b, n) => Add(_voice, b, n));
                g = _gameCapture = new ProcessAudio(GameNames, (b, n) => Add(_game, b, n));
            }
            v.Start();
            g.Start();
        }

        public void Stop()
        {
            ProcessAudio? v, g;
            lock (_lock) { v = _voiceCapture; g = _gameCapture; _voiceCapture = _gameCapture = null; _voice.Samples.Clear(); _game.Samples.Clear(); }
            v?.Dispose();
            g?.Dispose();
        }

        /// <summary>The send page asked something (it's open), even if it isn't reading the sound yet.</summary>
        public void Touch() { lock (_lock) _lastAsked = _clock(); }

        /// <summary>
        /// The send page closed (nothing read or asked for <paramref name="idle"/>): stops capturing Discord
        /// and Among Us, so two capture threads don't run all evening for nothing. The page's next ask starts
        /// it again (AppServer). True when it stopped.
        /// </summary>
        public bool StopIfIdle(TimeSpan idle)
        {
            lock (_lock)
            {
                var last = _lastRead > _lastAsked ? _lastRead : _lastAsked;
                if (_voiceCapture == null || _clock() - last < idle) return false;
            }
            Stop();
            return true;
        }

        /// <summary>Takes captured frames (also used by tests and simulation).</summary>
        public void AddVoice(short[] interleaved, int frames) => Add(_voice, interleaved, frames);
        public void AddGame(short[] interleaved, int frames) => Add(_game, interleaved, frames);

        private void Add(Source s, short[] buffer, int frames)
        {
            int count = Math.Min(frames * ProcessAudio.Channels, buffer.Length), peak = 0;
            for (int i = 0; i < count; i++)
            {
                int a = Math.Abs((int)buffer[i]);
                if (a > peak) peak = a;
            }
            lock (_lock)
            {
                // Nobody's reading (the send page is closed): don't pile up.
                if ((_clock() - _lastRead).TotalSeconds > 2) { s.Samples.Clear(); }
                s.Samples.Write(buffer, count);                 // keeps the newest half second
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
            short[] voice, game;
            int n;
            double voiceLevel = VoiceLevel, gameLevel = GameLevel;
            // Only the copying out happens under the lock: the capture threads aren't held up by the mixing.
            lock (_lock)
            {
                _lastRead = _clock();
                bool v = _voiceCapture?.State == "capturing" || _voice.Samples.Count > 0;
                bool g = _gameCapture?.State == "capturing" || _game.Samples.Count > 0;
                n = v && g ? Math.Min(_voice.Samples.Count, _game.Samples.Count) : Math.Max(_voice.Samples.Count, _game.Samples.Count);
                // A program that stopped sending sound (e.g. Discord with nobody talking still sends silence; a closed one doesn't) mustn't hold the mix up.
                if (v && g && n == 0 && Math.Max(_voice.Samples.Count, _game.Samples.Count) > StallFrames * ProcessAudio.Channels)
                    n = Math.Max(_voice.Samples.Count, _game.Samples.Count);
                n -= n % ProcessAudio.Channels;
                voice = new short[n];
                game = new short[n];
                _voice.Samples.Read(voice, n);                  // fewer than n waiting: the rest stays silent
                _game.Samples.Read(game, n);
            }
            var bytes = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                double mixed = voice[i] * voiceLevel + game[i] * gameLevel;
                short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(mixed)));
                bytes[i * 2] = (byte)(s & 0xff);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            return bytes;
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
                    Running = _voiceCapture != null,
                    Voice = new { State = _voiceCapture?.State ?? "off", Level = Db(_voice) },
                    Game = new { State = _gameCapture?.State ?? "off", Level = Db(_game) },
                    VoiceLevel, GameLevel,
                };
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// A fixed-size ring of samples, oldest first: writing more than it holds drops the oldest. Copies
    /// whole runs at a time, not one sample at a time. Not thread-safe: VoiceCapture locks round it.
    /// </summary>
    public sealed class SampleRing
    {
        private readonly short[] _buffer;
        private int _start, _count;

        public SampleRing(int capacity) => _buffer = new short[Math.Max(1, capacity)];

        public int Count => _count;
        public int Capacity => _buffer.Length;

        public void Clear() { _start = 0; _count = 0; }

        /// <summary>Adds the first <paramref name="count"/> samples of <paramref name="source"/>, dropping the oldest when full.</summary>
        public void Write(short[] source, int count)
        {
            count = Math.Max(0, Math.Min(count, source.Length));
            int from = 0;
            if (count >= _buffer.Length)
            {
                // More than fits at once: only the newest samples are kept.
                from = count - _buffer.Length;
                Clear();
            }
            int n = count - from;
            int drop = Math.Max(0, _count + n - _buffer.Length);
            if (drop > 0) { _start = (_start + drop) % _buffer.Length; _count -= drop; }
            int end = (_start + _count) % _buffer.Length;
            int first = Math.Min(n, _buffer.Length - end);
            Array.Copy(source, from, _buffer, end, first);
            if (n > first) Array.Copy(source, from + first, _buffer, 0, n - first);
            _count += n;
        }

        /// <summary>Moves up to <paramref name="count"/> of the oldest samples into <paramref name="target"/>; returns how many.</summary>
        public int Read(short[] target, int count)
        {
            int n = Math.Max(0, Math.Min(Math.Min(count, _count), target.Length));
            int first = Math.Min(n, _buffer.Length - _start);
            Array.Copy(_buffer, _start, target, 0, first);
            if (n > first) Array.Copy(_buffer, 0, target, first, n - first);
            _start = (_start + n) % _buffer.Length;
            _count -= n;
            if (_count == 0) _start = 0;
            return n;
        }
    }
}
