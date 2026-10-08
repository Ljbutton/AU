using System;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.PlayerCam
{
    /// <summary>
    /// The player camera's pictures on their way out of the host's game: the game hands over a
    /// frame's pixels (when this isn't still busy with the last one), a thread of its own turns them
    /// into a JPEG, and The Button asks for the newest one (GET /api/pov on the mod's connection).
    /// The game only draws the camera while someone has asked in the last few seconds.
    /// </summary>
    public sealed class PlayerCamFeed : IDisposable
    {
        /// <summary>The picture's size: 720p, sharp on a full-screen stream.</summary>
        public const int Width = 1280, Height = 720;
        public const int Fps = 30;
        public const int Quality = 82;
        private static readonly TimeSpan WantedFor = TimeSpan.FromSeconds(3);

        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly AutoResetEvent _work = new AutoResetEvent(false);
        private readonly JpegEncoder _encoder = new JpegEncoder();
        private readonly bool _inline;
        private Thread? _thread;
        private volatile bool _stop;
        private byte[]? _buffer;
        private bool _busy;
        private int _w, _h;
        private bool _bottomUp;
        private DateTime _asked = DateTime.MinValue;
        private long _seq;
        private byte[]? _latest;
        private TaskCompletionSource<bool> _next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="inline">Encode on the caller's thread (tests).</param>
        public PlayerCamFeed(Func<DateTime>? clock = null, bool inline = false)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
            _inline = inline;
        }

        /// <summary>Someone (The Button's send page) wants pictures.</summary>
        public void Asked() { lock (_lock) _asked = _clock(); }

        /// <summary>Whether the game should draw the camera at all.</summary>
        public bool Wanted { get { lock (_lock) return _clock() - _asked < WantedFor; } }

        /// <summary>Pictures made so far (the newest one's number).</summary>
        public long Seq { get { lock (_lock) return _seq; } }

        /// <summary>
        /// A buffer for the next frame's pixels, or null while the last frame is still being made
        /// (the game then skips this one).
        /// </summary>
        public byte[]? Take(int bytes)
        {
            lock (_lock)
            {
                if (_busy) return null;
                if (_buffer == null || _buffer.Length != bytes) _buffer = new byte[bytes];
                return _buffer;
            }
        }

        /// <summary>The pixels in the buffer from <see cref="Take"/> are a frame: make it a JPEG.</summary>
        public void Submit(int width, int height, bool bottomUp)
        {
            lock (_lock)
            {
                if (_buffer == null || _busy) return;
                _busy = true;
                _w = width;
                _h = height;
                _bottomUp = bottomUp;
            }
            if (_inline) { Encode(); return; }
            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "TT player cam", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            _work.Set();
        }

        private void Loop()
        {
            while (!_stop)
            {
                _work.WaitOne(500);
                if (_stop) return;
                bool busy;
                lock (_lock) busy = _busy;
                if (busy) Encode();
            }
        }

        private void Encode()
        {
            byte[]? jpeg = null;
            try { jpeg = _encoder.Encode(_buffer!, _w, _h, Quality, _bottomUp); }
            catch (Exception) { }
            TaskCompletionSource<bool> done;
            lock (_lock)
            {
                if (jpeg != null) { _latest = jpeg; _seq++; }
                _busy = false;
                done = _next;
                _next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            done.TrySetResult(true);
        }

        /// <summary>The newest picture after number <paramref name="after"/>, waiting up to <paramref name="wait"/> for one. Null: none yet.</summary>
        public async Task<(long Seq, byte[]? Jpeg)> NextAsync(long after, TimeSpan wait)
        {
            Task next;
            lock (_lock)
            {
                if (after > _seq) after = 0;     // Among Us restarted: its numbers start again
                if (_seq > after && _latest != null) return (_seq, _latest);
                next = _next.Task;
            }
            await Task.WhenAny(next, Task.Delay(wait)).ConfigureAwait(false);
            if (after > Seq) after = 0;
            lock (_lock) return _seq > after && _latest != null ? (_seq, _latest) : (_seq, null);
        }

        /// <summary>The picture with its number in front (8 bytes, little-endian), as sent to The Button.</summary>
        public static byte[] Frame(long seq, byte[] jpeg)
        {
            var bytes = new byte[8 + jpeg.Length];
            BitConverter.GetBytes(seq).CopyTo(bytes, 0);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes, 0, 8);
            Buffer.BlockCopy(jpeg, 0, bytes, 8, jpeg.Length);
            return bytes;
        }

        public void Dispose()
        {
            _stop = true;
            _work.Set();
        }
    }
}
