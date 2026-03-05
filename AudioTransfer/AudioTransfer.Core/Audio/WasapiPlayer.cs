using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// Simple audio player using waveOut API (winmm.dll).
    /// Plays PCM audio through the default output device.
    /// Thread-safe ring buffer for feeding audio data from any thread.
    /// </summary>
    public sealed class WasapiPlayer : IDisposable
    {
        private const int SAMPLE_RATE = 48000;
        private const int CHANNELS = 2; // Stereo for better compatibility
        private const int BITS_PER_SAMPLE = 16;
        private const int BYTES_PER_FRAME = CHANNELS * BITS_PER_SAMPLE / 8; // 2 bytes per frame (Mono 16-bit)

        private const int NUM_BUFFERS = 8;
        private const int BUFFER_DURATION_MS = 40;
        private const int BUFFER_SIZE = SAMPLE_RATE * BYTES_PER_FRAME * BUFFER_DURATION_MS / 1000; // 7680 bytes

        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint WHDR_DONE = 0x00000001;
        private const uint WHDR_PREPARED = 0x00000002;

        private IntPtr hWaveOut;
        private volatile bool isPlaying;
        private Thread? playbackThread;

        // Unmanaged memory for waveOut buffers
        private readonly IntPtr[] dataPtrs = new IntPtr[NUM_BUFFERS];
        private readonly IntPtr[] headerPtrs = new IntPtr[NUM_BUFFERS];
        private static readonly int WAVEHDR_SIZE = Marshal.SizeOf<WAVEHDR>();

        // Ring buffer for incoming audio
        private readonly byte[] ringBuffer;
        private int ringWritePos;
        private int ringReadPos;
        private int ringDataAvailable;
        private readonly object ringLock = new object();
        private readonly byte[] _playbackFillBuffer;

        public AudioFormat Format { get; }

        private uint deviceId;

        public WasapiPlayer(int ringBufferMs = 1000, uint deviceId = WAVE_MAPPER)
        {
            this.deviceId = deviceId;
            Format = new AudioFormat(SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE);
            int ringSize = SAMPLE_RATE * BYTES_PER_FRAME * ringBufferMs / 1000;
            ringBuffer = new byte[ringSize];
            _playbackFillBuffer = new byte[BUFFER_SIZE];
        }

        /// <summary>
        /// Initialize (prints info, kept for API compatibility).
        /// </summary>
        public void Initialize()
        {
            CoreLogger.Instance.Log($"[AudioPlayer] Ready: {SAMPLE_RATE}Hz {CHANNELS}ch {BITS_PER_SAMPLE}-bit, " +
                              $"{NUM_BUFFERS}x{BUFFER_DURATION_MS}ms buffers");
        }

        /// <summary>
        /// Open the default audio output device and start playback.
        /// </summary>
        public void Start()
        {
            if (isPlaying) return;

            var wfx = new WAVEFORMATEX
            {
                wFormatTag = 1, // WAVE_FORMAT_PCM
                nChannels = CHANNELS,
                nSamplesPerSec = SAMPLE_RATE,
                wBitsPerSample = BITS_PER_SAMPLE,
                nBlockAlign = (ushort)BYTES_PER_FRAME,
                nAvgBytesPerSec = (uint)(SAMPLE_RATE * BYTES_PER_FRAME),
                cbSize = 0
            };

            int result = waveOutOpen(ref hWaveOut, deviceId, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
            if (result != 0)
                throw new Exception($"waveOutOpen failed: error {result}");

            // Allocate unmanaged memory for each buffer
            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                dataPtrs[i] = Marshal.AllocHGlobal(BUFFER_SIZE);
                headerPtrs[i] = Marshal.AllocHGlobal(WAVEHDR_SIZE);

                // Zero out header memory
                for (int b = 0; b < WAVEHDR_SIZE; b++)
                    Marshal.WriteByte(headerPtrs[i], b, 0);

                var hdr = new WAVEHDR
                {
                    lpData = dataPtrs[i],
                    dwBufferLength = (uint)BUFFER_SIZE,
                    dwFlags = 0,
                    dwBytesRecorded = 0,
                    dwUser = IntPtr.Zero,
                    dwLoops = 0,
                    lpNext = IntPtr.Zero,
                    reserved = IntPtr.Zero
                };
                Marshal.StructureToPtr(hdr, headerPtrs[i], false);
            }

            isPlaying = true;

            playbackThread = new Thread(PlaybackLoop)
            {
                Name = "AudioPlayer",
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true
            };
            playbackThread.Start();

            AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[AudioPlayer] Playback started");
        }

        /// <summary>
        /// Playback loop – polls waveOut buffers, refills completed ones from ring buffer.
        /// </summary>
        private void PlaybackLoop()
        {
            try
            {
                // Initial fill: prepare and write all buffers
                for (int i = 0; i < NUM_BUFFERS; i++)
                {
                    FillDataBuffer(i);
                    waveOutPrepareHeader(hWaveOut, headerPtrs[i], (uint)WAVEHDR_SIZE);
                    waveOutWrite(hWaveOut, headerPtrs[i], (uint)WAVEHDR_SIZE);
                }

                int statsCounter = 0;

                while (isPlaying)
                {
                    bool anyRefilled = false;

                    for (int i = 0; i < NUM_BUFFERS; i++)
                    {
                        var hdr = Marshal.PtrToStructure<WAVEHDR>(headerPtrs[i]);
                        if ((hdr.dwFlags & WHDR_DONE) != 0)
                        {
                            // Buffer completed – recycle it
                            waveOutUnprepareHeader(hWaveOut, headerPtrs[i], (uint)WAVEHDR_SIZE);
                            FillDataBuffer(i);
                            waveOutPrepareHeader(hWaveOut, headerPtrs[i], (uint)WAVEHDR_SIZE);
                            waveOutWrite(hWaveOut, headerPtrs[i], (uint)WAVEHDR_SIZE);
                            anyRefilled = true;
                        }
                    }

                    // Log stats every ~10 seconds
                    statsCounter++;
                    if (statsCounter % 2000 == 0)
                    {
                        int buffered = BufferedBytes;
                        double bufferedMs = buffered / (double)(SAMPLE_RATE * BYTES_PER_FRAME) * 1000;
                        AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[AudioPlayer] Buffered: {bufferedMs:F0}ms ({buffered} bytes)");
                    }

                    Thread.Sleep(anyRefilled ? 1 : 5);
                }
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[AudioPlayer] Playback error: {ex.Message}");
            }

            AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[AudioPlayer] Playback loop ended");
        }

        /// <summary>
        /// Fill a waveOut data buffer from the ring buffer. Pads with silence if not enough data.
        /// </summary>
        private void FillDataBuffer(int index)
        {
            int bytesRead = ReadSamples(_playbackFillBuffer, 0, BUFFER_SIZE);

            // Pad remainder with silence
            if (bytesRead < BUFFER_SIZE)
                Array.Clear(_playbackFillBuffer, bytesRead, BUFFER_SIZE - bytesRead);

            Marshal.Copy(_playbackFillBuffer, 0, dataPtrs[index], BUFFER_SIZE);
        }

        #region Ring Buffer (Thread-Safe)

        /// <summary>
        /// Feed PCM audio data into the ring buffer (thread-safe).
        /// If input is Mono but player is Stereo, it will be automatically expanded.
        /// </summary>
        public void AddSamples(byte[] data, int offset, int count)
        {
            // Backward compatibility
            if (count <= 0) return;
            lock (ringLock)
            {
                WriteToRing(data, offset, count);
            }
        }

        /// <summary>
        /// Optimized version for 16-bit Mono short[] input.
        /// Expands to Stereo directly into the ring buffer without allocations.
        /// </summary>
        public void AddSamples(short[] monoData, int offset, int sampleCount)
        {
            if (sampleCount <= 0) return;
            int bytesToWrite = sampleCount * 4; // Expansion to Stereo 16-bit

            lock (ringLock)
            {
                // Ensure space
                int space = ringBuffer.Length - ringDataAvailable;
                if (bytesToWrite > space)
                {
                    int overflow = bytesToWrite - space;
                    ringReadPos = (ringReadPos + overflow) % ringBuffer.Length;
                    ringDataAvailable -= overflow;
                }

                // Write with expansion
                for (int i = 0; i < sampleCount; i++)
                {
                    short val = monoData[offset + i];
                    byte low = (byte)(val & 0xFF);
                    byte high = (byte)((val >> 8) & 0xFF);

                    // Left
                    ringBuffer[ringWritePos] = low;
                    ringBuffer[(ringWritePos + 1) % ringBuffer.Length] = high;
                    // Right
                    ringBuffer[(ringWritePos + 2) % ringBuffer.Length] = low;
                    ringBuffer[(ringWritePos + 3) % ringBuffer.Length] = high;

                    ringWritePos = (ringWritePos + 4) % ringBuffer.Length;
                }
                ringDataAvailable += bytesToWrite;
            }
        }

        private void WriteToRing(byte[] data, int offset, int count)
        {
            int space = ringBuffer.Length - ringDataAvailable;
            if (count > space)
            {
                int overflow = count - space;
                ringReadPos = (ringReadPos + overflow) % ringBuffer.Length;
                ringDataAvailable -= overflow;
            }

            int firstChunk = Math.Min(count, ringBuffer.Length - ringWritePos);
            Buffer.BlockCopy(data, offset, ringBuffer, ringWritePos, firstChunk);
            if (count > firstChunk)
                Buffer.BlockCopy(data, offset + firstChunk, ringBuffer, 0, count - firstChunk);

            ringWritePos = (ringWritePos + count) % ringBuffer.Length;
            ringDataAvailable += count;
        }

        /// <summary>
        /// Read up to 'count' bytes from ring buffer. Returns actual bytes read.
        /// </summary>
        private int ReadSamples(byte[] dest, int offset, int count)
        {
            lock (ringLock)
            {
                int toRead = Math.Min(count, ringDataAvailable);
                if (toRead == 0) return 0;

                int firstChunk = Math.Min(toRead, ringBuffer.Length - ringReadPos);
                Buffer.BlockCopy(ringBuffer, ringReadPos, dest, offset, firstChunk);
                if (toRead > firstChunk)
                    Buffer.BlockCopy(ringBuffer, 0, dest, offset + firstChunk, toRead - firstChunk);

                ringReadPos = (ringReadPos + toRead) % ringBuffer.Length;
                ringDataAvailable -= toRead;
                return toRead;
            }
        }

        /// <summary>
        /// How many bytes are currently buffered.
        /// </summary>
        public int BufferedBytes
        {
            get { lock (ringLock) { return ringDataAvailable; } }
        }

        #endregion

        /// <summary>
        /// Stop playback and release resources.
        /// </summary>
        public void Stop()
        {
            if (!isPlaying) return;
            isPlaying = false;

            if (playbackThread != null && playbackThread.IsAlive)
                playbackThread.Join(2000);

            // Stop all playback
            waveOutReset(hWaveOut);

            // Unprepare all buffers
            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                if (headerPtrs[i] != IntPtr.Zero)
                {
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(headerPtrs[i]);
                    if ((hdr.dwFlags & WHDR_PREPARED) != 0)
                        waveOutUnprepareHeader(hWaveOut, headerPtrs[i], (uint)WAVEHDR_SIZE);
                }
            }

            waveOutClose(hWaveOut);
            hWaveOut = IntPtr.Zero;

            // Free unmanaged memory
            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                if (dataPtrs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(dataPtrs[i]); dataPtrs[i] = IntPtr.Zero; }
                if (headerPtrs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(headerPtrs[i]); headerPtrs[i] = IntPtr.Zero; }
            }

            AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[AudioPlayer] Stopped");
        }

        public void Dispose() => Stop();

        #region waveOut P/Invoke (winmm.dll)

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct WAVEOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        [DllImport("winmm.dll")]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        public static extern int waveOutGetDevCaps(uint uDeviceID, ref WAVEOUTCAPS pwoc, uint cbwoc);

        public static System.Collections.Generic.Dictionary<uint, string> GetAvailableDevices()
        {
            var devices = new System.Collections.Generic.Dictionary<uint, string>();
            uint count = waveOutGetNumDevs();
            var caps = new WAVEOUTCAPS();
            uint capsSize = (uint)Marshal.SizeOf(typeof(WAVEOUTCAPS));

            for (uint i = 0; i < count; i++)
            {
                if (waveOutGetDevCaps(i, ref caps, capsSize) == 0)
                {
                    devices[i] = caps.szPname;
                }
            }
            return devices;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(ref IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX lpFormat,
            IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        #endregion
    }
}
