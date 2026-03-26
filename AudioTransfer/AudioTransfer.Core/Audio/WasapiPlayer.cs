using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// WASAPI Shared-Mode Low-Latency audio player.
    /// Uses event-driven buffer filling with MMCSS "Pro Audio" thread priority.
    /// 
    /// Key latency improvements over the old waveOut implementation:
    ///   - Event-driven: WASAPI signals exactly when buffer space is available (no polling/sleeping)
    ///   - MMCSS scheduling: Windows schedules this thread with real-time priority via avrt.dll
    ///   - Minimal buffer: Uses the device's default period (~10ms) instead of 8×40ms=320ms
    ///   - Zero-copy ring→WASAPI: Copies directly from ring buffer to WASAPI buffer via unsafe pointers
    /// 
    /// Thread-safe ring buffer for feeding audio data from any thread.
    /// </summary>
    public sealed unsafe class WasapiPlayer : IDisposable
    {
        private const int SAMPLE_RATE = 48000;
        private const int CHANNELS = 2; // Stereo for compatibility
        private const int BITS_PER_SAMPLE = 16;
        private const int BYTES_PER_FRAME = CHANNELS * BITS_PER_SAMPLE / 8; // 4 bytes

        // WASAPI objects
        private IAudioClient? audioClient;
        private IAudioRenderClient? renderClient;
        private uint bufferFrameCount;
        private AutoResetEvent? bufferEvent;
        private Thread? playbackThread;
        private volatile bool isPlaying;
        private string? deviceId;

        // Ring buffer for incoming audio
        private readonly byte[] ringBuffer;
        private int ringWritePos;
        private int ringReadPos;
        private int ringDataAvailable;
        private readonly object ringLock = new object();

        public AudioFormat Format { get; }

        /// <summary>
        /// Create a WasapiPlayer with WASAPI device ID.
        /// </summary>
        /// <param name="ringBufferMs">Ring buffer size in milliseconds (default 1000ms).</param>
        /// <param name="deviceId">WASAPI device ID string, or null for default device.</param>
        public WasapiPlayer(int ringBufferMs = 1000, string? deviceId = null)
        {
            this.deviceId = deviceId;
            Format = new AudioFormat(SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE);
            int ringSize = SAMPLE_RATE * BYTES_PER_FRAME * ringBufferMs / 1000;
            ringBuffer = new byte[ringSize];
        }

        /// <summary>
        /// Backward-compatible constructor: maps waveOut-style uint device index to WASAPI device ID.
        /// Pass 0xFFFFFFFF (WAVE_MAPPER / uint.MaxValue) for default device.
        /// </summary>
        public WasapiPlayer(int ringBufferMs, uint deviceIndex)
            : this(ringBufferMs, ResolveDeviceId(deviceIndex))
        {
        }

        private static string? ResolveDeviceId(uint deviceIndex)
        {
            if (deviceIndex == 0xFFFFFFFF) return null; // WAVE_MAPPER → default device
            try
            {
                var devices = GetRenderDeviceList();
                if ((int)deviceIndex < devices.Count)
                    return devices[(int)deviceIndex].Id;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Initialize (prints info, kept for API compatibility).
        /// </summary>
        public void Initialize()
        {
            Logging.CoreLogger.Instance.Log($"[WasapiPlayer] Ready: WASAPI Shared Low-Latency, " +
                $"{SAMPLE_RATE}Hz {CHANNELS}ch {BITS_PER_SAMPLE}-bit");
        }

        /// <summary>
        /// Open the WASAPI render device and start event-driven playback.
        /// </summary>
        public void Start()
        {
            if (isPlaying) return;

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;

            if (!string.IsNullOrEmpty(deviceId))
            {
                enumerator.GetDevice(deviceId, out device);
                Logging.CoreLogger.Instance.Log($"[WasapiPlayer] Using specific device: {deviceId}");
            }
            else
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
                Logging.CoreLogger.Instance.Log("[WasapiPlayer] Using default render device");
            }

            // Activate IAudioClient on the render device
            var iid = typeof(IAudioClient).GUID;
            device.Activate(ref iid, 0, IntPtr.Zero, out IntPtr clientPtr);
            audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);
            Marshal.Release(clientPtr);

            // Get mix format and override to our target PCM format
            audioClient.GetMixFormat(out WAVEFORMATEX* mixFormat);
            mixFormat->wFormatTag = 1; // WAVE_FORMAT_PCM
            mixFormat->nChannels = CHANNELS;
            mixFormat->nSamplesPerSec = SAMPLE_RATE;
            mixFormat->wBitsPerSample = BITS_PER_SAMPLE;
            mixFormat->nBlockAlign = (ushort)BYTES_PER_FRAME;
            mixFormat->nAvgBytesPerSec = (uint)(SAMPLE_RATE * BYTES_PER_FRAME);
            mixFormat->cbSize = 0;

            // Query device period – in shared mode, the event fires at defaultPeriod intervals
            audioClient.GetDevicePeriod(out long defaultPeriod, out long minPeriod);

            // In shared mode, bufferDuration must be >= defaultPeriod.
            // Using defaultPeriod gives the smallest possible WASAPI buffer.
            long bufferDuration = defaultPeriod;

            // Create event handle. WASAPI will signal this each time buffer space becomes available.
            bufferEvent = new AutoResetEvent(false);

            // Initialize WASAPI in shared mode with event-driven callback
            audioClient.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                bufferDuration,
                0, // Must be 0 for shared mode
                mixFormat,
                Guid.Empty);

            // Set the event handle that WASAPI will signal
            audioClient.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle());

            // Get actual buffer size allocated by WASAPI (may be larger than requested)
            audioClient.GetBufferSize(out bufferFrameCount);

            // Get render client for writing audio data into WASAPI buffer
            var renderIid = typeof(IAudioRenderClient).GUID;
            audioClient.GetService(ref renderIid, out IntPtr renderPtr);
            renderClient = (IAudioRenderClient)Marshal.GetObjectForIUnknown(renderPtr);
            Marshal.Release(renderPtr);

            Marshal.FreeCoTaskMem((IntPtr)mixFormat);

            // Pre-fill entire WASAPI buffer with silence to prevent initial glitches
            renderClient.GetBuffer(bufferFrameCount, out IntPtr silencePtr);
            new Span<byte>((void*)silencePtr, (int)(bufferFrameCount * BYTES_PER_FRAME)).Clear();
            renderClient.ReleaseBuffer(bufferFrameCount, (uint)AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT);

            isPlaying = true;

            // Start WASAPI audio client – begins event signaling
            audioClient.Start();

            // Start playback thread with high priority
            playbackThread = new Thread(PlaybackLoop)
            {
                Name = "WasapiPlayer",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            playbackThread.Start();

            double latencyMs = bufferFrameCount * 1000.0 / SAMPLE_RATE;
            Logging.CoreLogger.Instance.Log($"[WasapiPlayer] Started: WASAPI buffer={bufferFrameCount} frames ({latencyMs:F1}ms), " +
                $"device period={defaultPeriod / 10000.0:F1}ms (min={minPeriod / 10000.0:F1}ms)");
        }

        /// <summary>
        /// Event-driven playback loop.
        /// WASAPI signals when buffer space is available → we fill it from the ring buffer.
        /// MMCSS "Pro Audio" gives this thread real-time scheduling priority.
        /// </summary>
        private void PlaybackLoop()
        {
            // Request MMCSS "Pro Audio" real-time thread scheduling
            uint taskIndex = 0;
            IntPtr mmcssHandle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);

            int statsCounter = 0;

            try
            {
                while (isPlaying)
                {
                    // Block until WASAPI signals that buffer space is available.
                    // Timeout at 200ms as safety against hangs during shutdown.
                    bool signaled = bufferEvent!.WaitOne(200);
                    if (!isPlaying) break;
                    if (!signaled) continue;

                    // Get current padding = frames still queued for playback
                    audioClient!.GetCurrentPadding(out uint padding);
                    uint framesToWrite = bufferFrameCount - padding;
                    if (framesToWrite == 0) continue;

                    // Request a writable region of the WASAPI buffer
                    renderClient!.GetBuffer(framesToWrite, out IntPtr dataPtr);
                    int bytesToWrite = (int)(framesToWrite * BYTES_PER_FRAME);
                    int bytesRead = 0;

                    lock (ringLock)
                    {
                        bytesRead = Math.Min(bytesToWrite, ringDataAvailable);
                        if (bytesRead > 0)
                        {
                            // Zero-copy path: ring buffer → WASAPI buffer via unsafe pointers
                            byte* dest = (byte*)dataPtr;
                            int firstChunk = Math.Min(bytesRead, ringBuffer.Length - ringReadPos);

                            fixed (byte* src = &ringBuffer[ringReadPos])
                                Buffer.MemoryCopy(src, dest, bytesToWrite, firstChunk);

                            if (bytesRead > firstChunk)
                            {
                                fixed (byte* src = &ringBuffer[0])
                                    Buffer.MemoryCopy(src, dest + firstChunk, bytesToWrite - firstChunk, bytesRead - firstChunk);
                            }

                            ringReadPos = (ringReadPos + bytesRead) % ringBuffer.Length;
                            ringDataAvailable -= bytesRead;
                        }
                    }

                    // Pad remainder with silence if ring buffer didn't have enough data
                    if (bytesRead < bytesToWrite)
                    {
                        byte* dest = (byte*)dataPtr;
                        new Span<byte>(dest + bytesRead, bytesToWrite - bytesRead).Clear();
                    }

                    // Release buffer back to WASAPI. Mark as silent if no real data was written.
                    uint flags = (bytesRead == 0) ? (uint)AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT : 0;
                    renderClient.ReleaseBuffer(framesToWrite, flags);

                    // Log stats every ~10 seconds (assuming ~100 events/sec at 10ms period)
                    statsCounter++;
                    if (statsCounter % 1000 == 0)
                    {
                        int buffered;
                        lock (ringLock) { buffered = ringDataAvailable; }
                        double bufferedMs = buffered / (double)(SAMPLE_RATE * BYTES_PER_FRAME) * 1000;
                        Logging.CoreLogger.Instance.Log($"[WasapiPlayer] Buffered: {bufferedMs:F0}ms ({buffered} bytes)");
                    }
                }
            }
            catch (Exception ex)
            {
                if (isPlaying)
                    Logging.CoreLogger.Instance.Log($"[WasapiPlayer] Playback error: {ex.Message}");
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                    AvRevertMmThreadCharacteristics(mmcssHandle);
                Logging.CoreLogger.Instance.Log("[WasapiPlayer] Playback loop ended");
            }
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

                // Write with Mono → Stereo expansion
                for (int i = 0; i < sampleCount; i++)
                {
                    short val = monoData[offset + i];
                    byte low = (byte)(val & 0xFF);
                    byte high = (byte)((val >> 8) & 0xFF);

                    // Left channel
                    ringBuffer[ringWritePos] = low;
                    ringBuffer[(ringWritePos + 1) % ringBuffer.Length] = high;
                    // Right channel (duplicate)
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
        /// How many bytes are currently buffered.
        /// </summary>
        public int BufferedBytes
        {
            get { lock (ringLock) { return ringDataAvailable; } }
        }

        #endregion

        /// <summary>
        /// Stop playback and release WASAPI resources.
        /// </summary>
        public void Stop()
        {
            if (!isPlaying) return;
            isPlaying = false;

            // Signal the event to unblock the playback thread immediately
            bufferEvent?.Set();

            if (playbackThread != null && playbackThread.IsAlive)
                playbackThread.Join(2000);

            // Stop and reset WASAPI audio client
            try { audioClient?.Stop(); } catch { }
            try { audioClient?.Reset(); } catch { }

            // Release COM objects
            if (renderClient != null)
            {
                try { Marshal.ReleaseComObject(renderClient); } catch { }
                renderClient = null;
            }

            if (audioClient != null)
            {
                try { Marshal.ReleaseComObject(audioClient); } catch { }
                audioClient = null;
            }

            bufferEvent?.Dispose();
            bufferEvent = null;

            Logging.CoreLogger.Instance.Log("[WasapiPlayer] Stopped");
        }

        public void Dispose() => Stop();

        #region Device Enumeration (Backward-Compatible)

        /// <summary>
        /// Get available render (output) devices as Dictionary&lt;uint, string&gt;.
        /// The uint key is a zero-based index matching WASAPI device enumeration order.
        /// Backward-compatible with old waveOut-based implementation.
        /// </summary>
        public static Dictionary<uint, string> GetAvailableDevices()
        {
            var devices = new Dictionary<uint, string>();
            var list = GetRenderDeviceList();
            for (int i = 0; i < list.Count; i++)
            {
                devices[(uint)i] = list[i].Name;
            }
            return devices;
        }

        /// <summary>
        /// Internal: Enumerate render devices via WASAPI/MMDevice API.
        /// Returns (DeviceId, FriendlyName) tuples.
        /// </summary>
        public static List<(string Id, string Name)> GetRenderDeviceList()
        {
            var result = new List<(string Id, string Name)>();
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                enumerator.EnumAudioEndpoints(EDataFlow.eRender, 0x00000001 /* DEVICE_STATE_ACTIVE */, out var collectionPtr);
                var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collectionPtr);
                Marshal.Release(collectionPtr);

                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out var devPtr);
                    var dev = (IMMDevice)Marshal.GetObjectForIUnknown(devPtr);
                    Marshal.Release(devPtr);

                    dev.GetId(out IntPtr idPtr);
                    string id = Marshal.PtrToStringUni(idPtr) ?? "";
                    Marshal.FreeCoTaskMem(idPtr);

                    string name = id; // fallback
                    try
                    {
                        dev.OpenPropertyStore(0 /* STGM_READ */, out var propsPtr);
                        var props = (IPropertyStore)Marshal.GetObjectForIUnknown(propsPtr);
                        Marshal.Release(propsPtr);

                        // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
                        var pkey = new PROPERTYKEY
                        {
                            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                            pid = 14
                        };
                        props.GetValue(ref pkey, out var propVariant);
                        if (propVariant.pwszVal != IntPtr.Zero)
                        {
                            name = Marshal.PtrToStringUni(propVariant.pwszVal) ?? id;
                        }
                        PropVariantClear(ref propVariant);
                        Marshal.ReleaseComObject(props);
                    }
                    catch { }

                    result.Add((id, name));
                    Marshal.ReleaseComObject(dev);
                }
                Marshal.ReleaseComObject(collection);
            }
            catch (Exception ex)
            {
                Logging.CoreLogger.Instance.Log($"[WasapiPlayer] GetRenderDeviceList error: {ex.Message}");
            }
            return result;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        #endregion

        #region MMCSS P/Invoke (avrt.dll)

        [DllImport("avrt.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

        #endregion
    }

    #region IAudioRenderClient COM Interface

    /// <summary>
    /// WASAPI render client for writing audio data to the output buffer.
    /// Used by WasapiPlayer for low-latency audio playback.
    /// </summary>
    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        void GetBuffer(uint NumFramesRequested, out IntPtr ppData);
        void ReleaseBuffer(uint NumFramesWritten, uint dwFlags);
    }

    #endregion
}
