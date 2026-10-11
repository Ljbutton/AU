using System;
using System.Threading;
using System.Threading.Tasks;

namespace TournamentTracker.PlayerCam
{
    /// <summary>
    /// The player camera's pictures on their way out of the host's game: the game hands over a
    /// frame's pixels, one of two threads of their own turns them into a JPEG (two, so a picture
    /// is never dropped just because the last one is still being made), and The Button asks for
    /// the newest one (GET /api/pov on the mod's connection). The game only draws the camera while
    /// someone has asked in the last few seconds.
    /// </summary>
    public sealed class PlayerCamFeed : IDisposable
    {
        /// <summary>The picture's size: 540p (half the pixels of 720p, so more pictures a second for the same work).</summary>
        public const int Width = 960, Height = 540;
        public const int Fps = 30;
        public const int Quality = 82;
        private static readonly TimeSpan WantedFor = TimeSpan.FromSeconds(3);

        private sealed class Slot
        {
            public byte[]? Buffer;
            public bool Busy;
            public int W, H;
            public bool BottomUp;
            public long Frame;
            public readonly JpegEncoder Encoder = new JpegEncoder();
            public readonly AutoResetEvent Work = new AutoResetEvent(false);
            public Thread? Thread;
        }

        private readonly Func<DateTime> _clock;
        private readonly object _lock = new object();
        private readonly Slot[] _slots = { new Slot(), new Slot() };
        private readonly bool _inline;
        private volatile bool _stop;
        private Slot? _taken;
        private long _submitted, _published;
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
        /// A buffer for the next frame's pixels, or null while both threads are still making
        /// pictures (the game then skips this one).
        /// </summary>
        public byte[]? Take(int bytes)
        {
            lock (_lock)
            {
                foreach (var slot in _slots)
                {
                    if (slot.Busy) continue;
                    if (slot.Buffer == null || slot.Buffer.Length != bytes) slot.Buffer = new byte[bytes];
                    _taken = slot;
                    return slot.Buffer;
                }
                return null;
            }
        }

        /// <summary>The pixels in the buffer from <see cref="Take"/> are a frame: make it a JPEG.</summary>
        public void Submit(int width, int height, bool bottomUp)
        {
            Slot slot;
            lock (_lock)
            {
                if (_taken == null || _taken.Busy || _taken.Buffer == null) return;
                slot = _taken;
                _taken = null;
                slot.Busy = true;
                slot.W = width;
                slot.H = height;
                slot.BottomUp = bottomUp;
                slot.Frame = ++_submitted;
            }
            if (_inline) { Encode(slot); return; }
            if (slot.Thread == null)
            {
                slot.Thread = new Thread(() => Loop(slot)) { IsBackground = true, Name = "TT player cam", Priority = ThreadPriority.Normal };
                slot.Thread.Start();
            }
            slot.Work.Set();
        }

        private void Loop(Slot slot)
        {
            while (!_stop)
            {
                slot.Work.WaitOne(500);
                if (_stop) return;
                bool busy;
                lock (_lock) busy = slot.Busy;
                if (busy) Encode(slot);
            }
        }

        private void Encode(Slot slot)
        {
            byte[]? jpeg = null;
            try { jpeg = slot.Encoder.Encode(slot.Buffer!, slot.W, slot.H, Quality, slot.BottomUp); }
            catch (Exception) { }
            TaskCompletionSource<bool> done;
            lock (_lock)
            {
                // The two threads can finish out of order: an older picture never replaces a newer one.
                if (jpeg != null && slot.Frame > _published) { _published = slot.Frame; _latest = jpeg; _seq++; }
                slot.Busy = false;
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
            foreach (var slot in _slots) slot.Work.Set();
        }
    }
}
