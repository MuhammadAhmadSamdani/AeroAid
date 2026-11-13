using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using log4net;

namespace MissionPlanner.AeroAid.Audio
{
    /// <summary>
    /// Minimal Windows waveIn (winmm) microphone capture: 16 kHz, 16-bit, mono PCM (the format used by the
    /// speech recogniser). Runs its own capture thread; no external dependencies.
    /// </summary>
    public sealed class WaveInCapture : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const int SampleRate = 16000;
        private const int BufferCount = 4;
        private const int BufferMs = 100;
        private const int WAVE_MAPPER = -1;
        private const int CALLBACK_EVENT = 0x50000;
        private const int WHDR_DONE = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEINCAPS
        {
            public short wMid;
            public short wPid;
            public int vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public int dwFormats;
            public short wChannels;
            public short wReserved1;
        }

        [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] private static extern int waveInGetDevCaps(IntPtr id, ref WAVEINCAPS caps, int size);
        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr h, int deviceId, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr h);

        /// <summary>Capture devices: (-1 = system default, then device ids).</summary>
        public static List<(int id, string name)> Devices()
        {
            var list = new List<(int, string)> { (WAVE_MAPPER, "System default microphone") };
            try
            {
                var n = waveInGetNumDevs();
                for (var i = 0; i < n; i++)
                {
                    var caps = new WAVEINCAPS();
                    if (waveInGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0)
                        list.Add((i, caps.szPname));
                }
            }
            catch (Exception ex)
            {
                log.Error("Enumerating microphones failed", ex);
            }

            return list;
        }

        private readonly int deviceId;
        private IntPtr handle;
        private readonly IntPtr[] headers = new IntPtr[BufferCount];
        private readonly IntPtr[] data = new IntPtr[BufferCount];
        private EventWaitHandle bufferEvent;
        private Thread thread;
        private volatile bool running;

        /// <summary>16-bit mono PCM chunks (capture thread).</summary>
        public event Action<byte[]> DataAvailable;

        /// <summary>Raised if capture fails while running (capture thread).</summary>
        public event Action<string> Failed;

        public WaveInCapture(int deviceId)
        {
            this.deviceId = deviceId;
        }

        public static string ErrorText(int code)
        {
            switch (code)
            {
                case 2: return "Microphone not found (it may have been unplugged).";
                case 4: return "Microphone is in use by another application.";
                case 6: return "No audio driver for this microphone.";
                case 32: return "The microphone does not support 16 kHz mono recording.";
                default: return "Microphone error (winmm code " + code + ").";
            }
        }

        /// <summary>Opens the device and starts capturing. Throws InvalidOperationException with a readable message.</summary>
        public void Start()
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = SampleRate * 2,
                cbSize = 0
            };
            bufferEvent = new AutoResetEvent(false);
            var rc = waveInOpen(out handle, deviceId, ref fmt, bufferEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (rc != 0)
            {
                bufferEvent.Dispose();
                bufferEvent = null;
                handle = IntPtr.Zero;
                throw new InvalidOperationException(ErrorText(rc));
            }

            var bytes = SampleRate * 2 * BufferMs / 1000;
            var hsize = Marshal.SizeOf(typeof(WAVEHDR));
            for (var i = 0; i < BufferCount; i++)
            {
                data[i] = Marshal.AllocHGlobal(bytes);
                headers[i] = Marshal.AllocHGlobal(hsize);
                var h = new WAVEHDR { lpData = data[i], dwBufferLength = bytes };
                Marshal.StructureToPtr(h, headers[i], false);
                waveInPrepareHeader(handle, headers[i], hsize);
                waveInAddBuffer(handle, headers[i], hsize);
            }

            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "AeroAid mic capture" };
            thread.Start();
            rc = waveInStart(handle);
            if (rc != 0)
            {
                Stop();
                throw new InvalidOperationException(ErrorText(rc));
            }
        }

        private void Loop()
        {
            var hsize = Marshal.SizeOf(typeof(WAVEHDR));
            try
            {
                while (running)
                {
                    bufferEvent.WaitOne(500);
                    for (var i = 0; i < BufferCount && running; i++)
                    {
                        var h = (WAVEHDR)Marshal.PtrToStructure(headers[i], typeof(WAVEHDR));
                        if ((h.dwFlags & WHDR_DONE) == 0)
                            continue;
                        if (h.dwBytesRecorded > 0)
                        {
                            var chunk = new byte[h.dwBytesRecorded];
                            Marshal.Copy(h.lpData, chunk, 0, chunk.Length);
                            try
                            {
                                DataAvailable?.Invoke(chunk);
                            }
                            catch (Exception ex)
                            {
                                log.Error("Microphone consumer failed", ex);
                            }
                        }

                        // recycle the buffer
                        h.dwFlags = 0;
                        h.dwBytesRecorded = 0;
                        Marshal.StructureToPtr(h, headers[i], false);
                        waveInPrepareHeader(handle, headers[i], hsize);
                        var rc = waveInAddBuffer(handle, headers[i], hsize);
                        if (rc != 0 && running)
                        {
                            running = false;
                            Failed?.Invoke(ErrorText(rc));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error("Microphone capture loop failed", ex);
                Failed?.Invoke("Microphone capture failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            running = false;
            if (handle != IntPtr.Zero)
            {
                waveInStop(handle);
                waveInReset(handle);
            }

            thread?.Join(1500);
            thread = null;
            if (handle != IntPtr.Zero)
            {
                var hsize = Marshal.SizeOf(typeof(WAVEHDR));
                for (var i = 0; i < BufferCount; i++)
                {
                    if (headers[i] != IntPtr.Zero)
                    {
                        waveInUnprepareHeader(handle, headers[i], hsize);
                        Marshal.FreeHGlobal(headers[i]);
                        headers[i] = IntPtr.Zero;
                    }

                    if (data[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(data[i]);
                        data[i] = IntPtr.Zero;
                    }
                }

                waveInClose(handle);
                handle = IntPtr.Zero;
            }

            bufferEvent?.Dispose();
            bufferEvent = null;
        }

        public void Dispose() => Stop();
    }
}
