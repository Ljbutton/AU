using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TournamentTracker.App.Voice
{
    /// <summary>
    /// Captures what one program plays (and its child processes), nothing else: Windows' application
    /// loopback (Windows 10 2004 or later, the same thing OBS's Application Audio Capture uses).
    /// Delivers 48 kHz stereo 16-bit frames. Follows the program: when it starts, stops or restarts,
    /// capture picks it up again. Does nothing on other systems.
    /// </summary>
    public sealed class ProcessAudio : IDisposable
    {
        public const int SampleRate = 48000, Channels = 2;
        private readonly string[] _names;
        private readonly Action<short[], int> _frames;
        private Thread? _thread;
        private volatile bool _stop;

        /// <summary>"capturing", "not running" (the program isn't open), "unsupported" or an error.</summary>
        public string State { get; private set; } = "starting";
        public int? ProcessId { get; private set; }

        /// <param name="names">Process names (without .exe) to follow; the earliest started one is captured, with its children.</param>
        /// <param name="frames">Gets interleaved stereo samples and the number of frames, on the capture thread.</param>
        public ProcessAudio(string[] names, Action<short[], int> frames)
        {
            _names = names;
            _frames = frames;
        }

        [System.Runtime.Versioning.SupportedOSPlatformGuard("windows10.0.19041")]
        public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

        public void Start()
        {
            if (_thread != null) return;
            if (!Supported) { State = "unsupported"; return; }
            _thread = new Thread(Run) { IsBackground = true, Name = "Audio " + _names[0] };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>The program's main process: the earliest started of its processes (Discord runs several; its sound comes from a child).</summary>
        private int? FindProcess()
        {
            Process? best = null;
            DateTime bestStart = DateTime.MaxValue;
            foreach (var name in _names)
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var start = p.StartTime;
                        if (start < bestStart) { best = p; bestStart = start; }
                    }
                    catch (Exception) { }
                }
            return best?.Id;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
        private void Run()
        {
            while (!_stop)
            {
                int? pid = FindProcess();
                ProcessId = pid;
                if (pid == null) { State = "not running"; Sleep(3000); continue; }
                try { Capture(pid.Value); }
                catch (Exception e) { State = "error: " + e.Message; Sleep(3000); }
            }
        }

        private void Sleep(int ms)
        {
            for (int i = 0; i < ms / 100 && !_stop; i++) Thread.Sleep(100);
        }

        private bool Alive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; } catch (Exception) { return false; }
        }

        // ---- Application loopback --------------------------------------------------------------

        [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
        private void Capture(int pid)
        {
            var client = Activate(pid);
            IAudioCaptureClient? capture = null;
            using var ready = new AutoResetEvent(false);
            var format = new WaveFormatEx { wFormatTag = 1, nChannels = Channels, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = Channels * 2, nAvgBytesPerSec = SampleRate * Channels * 2, cbSize = 0 };
            try
            {
                const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvertPcm = 0x80000000, SrcDefaultQuality = 0x08000000;
                Check(client.Initialize(0, Loopback | EventCallback | AutoConvertPcm | SrcDefaultQuality, 200 * 10000, 0, ref format, IntPtr.Zero), "Initialize");
                Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                var iid = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref iid, out var service), "GetService");
                capture = (IAudioCaptureClient)service;
                Check(client.Start(), "Start");
                State = "capturing";
                var buffer = new short[SampleRate * Channels];
                var lastCheck = DateTime.UtcNow;
                while (!_stop)
                {
                    ready.WaitOne(200);
                    if ((DateTime.UtcNow - lastCheck).TotalSeconds > 3)
                    {
                        lastCheck = DateTime.UtcNow;
                        // The program closed (or a newer copy started): find it again.
                        if (!Alive(pid) || FindProcess() != pid) break;
                    }
                    while (true)
                    {
                        Check(capture.GetNextPacketSize(out uint packet), "GetNextPacketSize");
                        if (packet == 0) break;
                        Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "GetBuffer");
                        int n = (int)Math.Min(frames, (uint)(buffer.Length / Channels));
                        const uint Silent = 0x2;
                        if ((flags & Silent) != 0 || data == IntPtr.Zero) Array.Clear(buffer, 0, n * Channels);
                        else Marshal.Copy(data, buffer, 0, n * Channels);
                        Check(capture.ReleaseBuffer(frames), "ReleaseBuffer");
                        if (n > 0) _frames(buffer, n);
                    }
                }
                client.Stop();
            }
            finally
            {
                if (capture != null) Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(client);
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
        }

        private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";

        /// <summary>An audio client for one process tree, through ActivateAudioInterfaceAsync.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
        private static IAudioClient Activate(int pid)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS: type = process loopback, the process, include its tree.
            var parameters = new ActivationParams { ActivationType = 1, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0 };
            IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
            IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
            try
            {
                Marshal.StructureToPtr(parameters, paramsPtr, false);
                var variant = new BlobVariant { vt = 0x41 /* VT_BLOB */, cbSize = (uint)Marshal.SizeOf<ActivationParams>(), pBlobData = paramsPtr };
                Marshal.StructureToPtr(variant, variantPtr, false);
                var handler = new ActivationHandler();
                var iid = typeof(IAudioClient).GUID;
                int hr = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, variantPtr, handler, out var operation);
                Check(hr, "ActivateAudioInterfaceAsync");
                if (!handler.Done.WaitOne(5000)) throw new TimeoutException("Windows didn't open the program's audio.");
                Check(operation.GetActivateResult(out int result, out object iface), "GetActivateResult");
                Check(result, "Activation");
                Marshal.ReleaseComObject(operation);
                return (IAudioClient)iface;
            }
            finally
            {
                Marshal.FreeHGlobal(variantPtr);
                Marshal.FreeHGlobal(paramsPtr);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(1000);
        }

        // ---- Interop ----------------------------------------------------------------------------

        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
            IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);

        [StructLayout(LayoutKind.Sequential)]
        private struct ActivationParams
        {
            public int ActivationType;
            public uint TargetProcessId;
            public int ProcessLoopbackMode;
        }

        /// <summary>A PROPVARIANT holding a BLOB (vt, three reserved words, then the size and pointer).</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct BlobVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public uint cbSize;
            [FieldOffset(16)] public IntPtr pBlobData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        /// <summary>Signals when Windows has the audio client ready (called on another thread; .NET's COM wrappers are agile).</summary>
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Done.Set();
        }

        [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject { }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
