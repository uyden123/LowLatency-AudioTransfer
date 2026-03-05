using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AudioTransfer.Core.Audio
{
    /// <summary>
    /// WASAPI Loopback Capture using Win32 API with precise 20ms timer-based buffer reading.
    /// This eliminates the variable buffer size problem from NAudio's event-driven approach.
    /// </summary>
    public sealed unsafe class WasapiTimedCapture : IDisposable
    {
        private const int BUFFER_DURATION_MS = 60; // atleast 20ms for low latency, lower leads to underruns
        private const int SAMPLE_RATE = 48000;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16; // Target 16-bit PCM for efficiency and compatibility

        private IAudioClient? audioClient;
        private IAudioCaptureClient? captureClient;
        private Thread? captureThread;
        private CancellationTokenSource? cts;
        private volatile bool isCapturing;
        private string? currentDeviceId;
        private bool isDefaultDeviceMode;
        private DeviceChangeHandler? deviceChangeHandler;
        
        private const uint AUDCLNT_E_DEVICE_INVALIDATED = 0x88890004;

        private int bufferSizeBytes;
        private int bytesPerFrame; // cached: Channels * (BitsPerSample / 8)

        /// <summary>
        /// When set, WASAPI data is written DIRECTLY into this buffer via unsafe pointers.
        /// Bypasses all intermediate buffers, events, and managed allocations.
        /// Set this BEFORE calling Start().
        /// </summary>
        public UnsafeCircularBuffer? DirectOutputBuffer { get; set; }

        public event EventHandler? DefaultDeviceChanged;
        public event EventHandler<AudioDataEventArgs>? DataAvailable;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public AudioFormat Format { get; private set; }

        public WasapiTimedCapture()
        {
            Format = new AudioFormat(SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE);
        }

        /// <summary>
        /// Initialize WASAPI loopback capture with default audio device.
        /// </summary>
        public void Initialize() => Initialize(null);

        /// <summary>
        /// Initialize WASAPI loopback capture with a specific device.
        /// </summary>
        /// <param name="deviceId">COM device ID from GetRenderDevices(), or null for default.</param>
        public void Initialize(string? deviceId)
        {
            currentDeviceId = deviceId;
            isDefaultDeviceMode = string.IsNullOrEmpty(deviceId);
            
            // Cleanup existing COM resources if re-initializing
            ReleaseResources();

            try
            {
                var deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice device;

                if (!string.IsNullOrEmpty(deviceId))
                {
                    deviceEnumerator.GetDevice(deviceId, out device);
                    CoreLogger.Instance.Log($"[WasapiTimedCapture] Using specific device: {deviceId}");
                }
                else
                {
                    deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
                    CoreLogger.Instance.Log($"[WasapiTimedCapture] Using default device");
                }

                // Activate audio client - use IntPtr to avoid marshaling issues
                var iid = typeof(IAudioClient).GUID;
                IntPtr audioClientPtr;
                device.Activate(ref iid, 0, IntPtr.Zero, out audioClientPtr);
                audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(audioClientPtr);
                Marshal.Release(audioClientPtr);

                // Get mix format
                audioClient.GetMixFormat(out var waveFormat);

                // Modify format to requested sample rate and 16-bit PCM. 
                // WASAPI Shared Mode will convert/resample if 
                // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM is used.
                waveFormat->wFormatTag = 1; // WAVE_FORMAT_PCM
                waveFormat->nSamplesPerSec = SAMPLE_RATE;
                waveFormat->wBitsPerSample = 16;
                waveFormat->nBlockAlign = (ushort)(waveFormat->nChannels * 16 / 8);
                waveFormat->nAvgBytesPerSec = waveFormat->nSamplesPerSec * waveFormat->nBlockAlign;
                waveFormat->cbSize = 0;

                // Use smaller WASAPI buffer when DirectOutputBuffer is set (low-latency path)
                // Otherwise use the standard 120ms buffer for event-based mode
                bool directMode = DirectOutputBuffer != null;
                long bufferDuration = directMode ? 40 * 10000 : 120 * 10000;

                audioClient.Initialize(
                    AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_LOOPBACK |
                    AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                    AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    bufferDuration,
                    0,
                    waveFormat,
                    Guid.Empty);

                // Get capture client - use IntPtr to avoid marshaling issues
                var captureIid = typeof(IAudioCaptureClient).GUID;
                IntPtr captureClientPtr;
                audioClient.GetService(ref captureIid, out captureClientPtr);
                captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureClientPtr);
                Marshal.Release(captureClientPtr);

                // Update format from actual device
                Format = new AudioFormat(
                    (int)waveFormat->nSamplesPerSec,
                    waveFormat->nChannels,
                    waveFormat->wBitsPerSample);

                bytesPerFrame = Format.Channels * (Format.BitsPerSample / 8);

                // Calculate required buffer size for our fixed duration
                bufferSizeBytes = Format.SampleRate * BUFFER_DURATION_MS / 1000 * bytesPerFrame;

                // Register for device changes if in default mode
                if (isDefaultDeviceMode)
                {
                    try
                    {
                        deviceChangeHandler = new DeviceChangeHandler(this);
                        deviceEnumerator.RegisterEndpointNotificationCallback(Marshal.GetComInterfaceForObject(deviceChangeHandler, typeof(IMMNotificationClient)));
                        CoreLogger.Instance.Log("[WasapiTimedCapture] Registered for default device change notifications");
                    }
                    catch (Exception ex)
                    {
                        CoreLogger.Instance.Log($"[WasapiTimedCapture] Failed to register notifications: {ex.Message}");
                    }
                }

                Marshal.FreeCoTaskMem((IntPtr)waveFormat);

                CoreLogger.Instance.Log($"[WasapiTimedCapture] Initialized: {(directMode ? "DIRECT" : "EVENT")} mode, " +
                                  $"WASAPI buffer={bufferDuration / 10000}ms");
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Initialization error: {ex.Message}");
                throw;
            }
        }

        [ComVisible(true)]
        private class DeviceChangeHandler : IMMNotificationClient
        {
            private readonly WasapiTimedCapture parent;
            public DeviceChangeHandler(WasapiTimedCapture parent) => this.parent = parent;

            public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
            {
                if (flow == EDataFlow.eRender && role == ERole.eMultimedia)
                {
                    CoreLogger.Instance.Log($"[WasapiTimedCapture] Default device changed to: {pwstrDefaultDeviceId}");
                    parent.DefaultDeviceChanged?.Invoke(parent, EventArgs.Empty);
                }
            }

            public void OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string pwstrDeviceId) { }
            public void OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) { }
        }

        /// <summary>
        /// Enumerate all active audio render (output) devices.
        /// Returns list of (DeviceId, FriendlyName) tuples.
        /// </summary>
        public static System.Collections.Generic.List<(string Id, string Name)> GetRenderDevices()
        {
            var result = new System.Collections.Generic.List<(string Id, string Name)>();
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
                CoreLogger.Instance.Log($"[WasapiTimedCapture] GetRenderDevices error: {ex.Message}");
            }
            return result;
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        /// <summary>
        /// Start capturing audio
        /// </summary>
        public void Start()
        {
            if (isCapturing)
                return;

            if (audioClient == null || captureClient == null)
                throw new InvalidOperationException("Not initialized. Call Initialize() first.");

            isCapturing = true;
            cts = new CancellationTokenSource();

            // Start WASAPI capture
            audioClient.Start();

            // Choose capture loop based on mode
            ThreadStart loopMethod = DirectOutputBuffer != null ? DirectCaptureLoop : CaptureLoop;

            captureThread = new Thread(loopMethod)
            {
                Name = "WasapiTimedCapture",
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            captureThread.Start();

            AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Started ({(DirectOutputBuffer != null ? "direct zero-copy" : "event-based")})");
        }

        /// <summary>
        /// DIRECT capture loop: reads WASAPI ? writes directly to UnsafeCircularBuffer.
        /// Zero managed allocation, zero intermediate copy. Maximum throughput, minimum latency.
        /// </summary>
        private void DirectCaptureLoop()
        {
            var directBuffer = DirectOutputBuffer!;
            var spinner = new SpinWait();
            long totalBytesWritten = 0;
            long packetsRead = 0;
            var statsTimer = System.Diagnostics.Stopwatch.StartNew();

            // Allocate silence buffer ONCE outside the loop to prevent StackOverflow (0x80131506)
            byte* zeros = stackalloc byte[4096];
            new Span<byte>(zeros, 4096).Clear();

            try
            {
                while (isCapturing && !cts!.Token.IsCancellationRequested)
                {
                    bool didWork = false;

                    // Drain all available WASAPI packets
                    captureClient!.GetNextPacketSize(out var packetSize);

                    while (packetSize > 0)
                    {
                        captureClient.GetBuffer(
                            out var dataPointer,
                            out var numFrames,
                            out var flags,
                            out var devicePosition,
                            out var qpcPosition);

                        if (numFrames > 0)
                        {
                            int byteCount = (int)(numFrames * bytesPerFrame);

                            if ((flags & AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                            {
                                // Silent: write zeros directly into circular buffer
                                int remaining = byteCount;
                                while (remaining > 0)
                                {
                                    int chunk = Math.Min(remaining, 4096);
                                    directBuffer.Write(zeros, chunk);
                                    remaining -= chunk;
                                }
                            }
                            else
                            {
                                // Hot path: WASAPI COM pointer ? UnsafeCircularBuffer (single memcpy)
                                directBuffer.Write((byte*)dataPointer, byteCount);
                            }

                            totalBytesWritten += byteCount;
                            packetsRead++;
                            didWork = true;
                        }

                        captureClient.ReleaseBuffer(numFrames);
                        captureClient.GetNextPacketSize(out packetSize);
                    }

                    // Stats every 10 seconds
                    if (statsTimer.ElapsedMilliseconds >= 10000)
                    {
                        double elapsed = statsTimer.Elapsed.TotalSeconds;
                        int bufferedMs = directBuffer.Available * 1000 / (Format.SampleRate * bytesPerFrame);
                        CoreLogger.Instance.Log($"[WasapiDirect] {packetsRead / elapsed:F0} WASAPI pkts/s, " +
                                          $"{totalBytesWritten / elapsed / 1024:F0} KB/s, " +
                                          $"buffer={bufferedMs}ms/{directBuffer.Available}/{directBuffer.Capacity}");
                        totalBytesWritten = 0;
                        packetsRead = 0;
                        statsTimer.Restart();
                    }

                    if (!didWork)
                    {
                        // No data available � spin briefly then yield
                        spinner.SpinOnce();
                    }
                    else
                    {
                        spinner.Reset();
                    }
                }

                AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[WasapiDirect] Capture loop ended.");
            }
            catch (Exception ex)
            {
                if (ex is COMException cex && (uint)cex.HResult == AUDCLNT_E_DEVICE_INVALIDATED)
                {
                    AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[WasapiDirect] Device invalidated! Signaling restart.");
                    DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
                }

                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiDirect] Capture error: {ex.Message}");
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex));
            }
        }

        /// <summary>
        /// Legacy capture loop with timer-based event firing (for broadcast mode)
        /// </summary>
        private void CaptureLoop()
        {
            var timer = new System.Diagnostics.Stopwatch();
            timer.Start();

            long nextEventTime = BUFFER_DURATION_MS;
            byte[] accumulationBuffer = new byte[bufferSizeBytes * 4]; // Larger buffer for safety
            int bufferOffset = 0;

            int eventCount = 0;
            int underrunCount = 0;

            try
            {
                while (isCapturing && !cts!.Token.IsCancellationRequested)
                {
                    long currentTime = timer.ElapsedMilliseconds;

                    // Continuously read available data from WASAPI
                    int bytesRead = ReadAudioData(accumulationBuffer, bufferOffset);
                    bufferOffset += bytesRead;

                    // Check if it's time to fire the next event
                    if (currentTime >= nextEventTime)
                    {
                        eventCount++;

                        // Fire event with exactly bufferSizeBytes
                        byte[] eventBuffer = new byte[bufferSizeBytes];

                        if (bufferOffset >= bufferSizeBytes)
                        {
                            // We have enough data - use it
                            Array.Copy(accumulationBuffer, 0, eventBuffer, 0, bufferSizeBytes);

                            // Move remaining data to beginning
                            int remaining = bufferOffset - bufferSizeBytes;
                            if (remaining > 0)
                            {
                                Array.Copy(accumulationBuffer, bufferSizeBytes, accumulationBuffer, 0, remaining);
                            }
                            bufferOffset = remaining;
                        }
                        else
                        {
                            // Underrun - not enough data accumulated
                            // Copy what we have and fill rest with silence
                            if (bufferOffset > 0)
                            {
                                Array.Copy(accumulationBuffer, 0, eventBuffer, 0, bufferOffset);
                            }
                            Array.Clear(eventBuffer, bufferOffset, bufferSizeBytes - bufferOffset);

                            underrunCount++;
                            if (underrunCount % 100 == 1)
                            {
                                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Buffer underrun #{underrunCount} (had {bufferOffset}/{bufferSizeBytes} bytes)");
                            }

                            bufferOffset = 0;
                        }

                        // Fire event at precise intervals
                        DataAvailable?.Invoke(this, new AudioDataEventArgs(eventBuffer, bufferSizeBytes, Format));

                        // Schedule next event
                        nextEventTime += BUFFER_DURATION_MS;

                        // Prevent drift - reset if we're too far behind
                        if (nextEventTime < currentTime - 100)
                        {
                            AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Timer drift detected ({currentTime - nextEventTime}ms behind), resetting");
                            nextEventTime = currentTime + BUFFER_DURATION_MS;
                        }
                    }
                    else
                    {
                        // Sleep a bit to avoid busy-waiting, but not too long to keep resolution
                        long sleepTime = Math.Min(2, nextEventTime - currentTime);
                        if (sleepTime > 0)
                        {
                            Thread.Sleep((int)sleepTime);
                        }
                    }
                }

                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Capture loop ended. Events fired: {eventCount}, Underruns: {underrunCount}");
            }
            catch (Exception ex)
            {
                if (ex is COMException cex && (uint)cex.HResult == AUDCLNT_E_DEVICE_INVALIDATED)
                {
                    AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[WasapiTimedCapture] Device invalidated! Signaling restart.");
                    DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
                }

                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Capture error: {ex.Message}");
                ErrorOccurred?.Invoke(this, new ErrorEventArgs(ex));
            }
        }

        /// <summary>
        /// Read available audio data from WASAPI (used by legacy CaptureLoop)
        /// </summary>
        private int ReadAudioData(byte[] buffer, int offset)
        {
            if (captureClient == null)
                return 0;

            int totalBytesRead = 0;

            try
            {
                // Get next packet size
                captureClient.GetNextPacketSize(out var packetSize);

                while (packetSize > 0)
                {
                    // Get buffer
                    captureClient.GetBuffer(
                        out var dataPointer,
                        out var numFrames,
                        out var flags,
                        out var devicePosition,
                        out var qpcPosition);

                    if (numFrames > 0)
                    {
                        int bytesToCopy = (int)(numFrames * bytesPerFrame);

                        // Check if we have space
                        if (offset + totalBytesRead + bytesToCopy <= buffer.Length)
                        {
                            // Copy audio data
                            if ((flags & AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                            {
                                // Silent buffer - write zeros
                                Array.Clear(buffer, offset + totalBytesRead, bytesToCopy);
                            }
                            else
                            {
                                // Copy actual data
                                Marshal.Copy(dataPointer, buffer, offset + totalBytesRead, bytesToCopy);
                            }

                            totalBytesRead += bytesToCopy;
                        }
                    }

                    // Release buffer
                    captureClient.ReleaseBuffer(numFrames);

                    // Get next packet
                    captureClient.GetNextPacketSize(out packetSize);
                }
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Read error: {ex.Message}");
            }

            return totalBytesRead;
        }

        /// <summary>
        /// Stop capturing
        /// </summary>
        public void Stop()
        {
            if (!isCapturing)
                return;

            isCapturing = false;
            cts?.Cancel();

            // Wait for thread to finish
            if (captureThread != null && captureThread.IsAlive)
            {
                captureThread.Join(1000);
            }

            // Stop WASAPI
            try { audioClient?.Stop(); } catch { }

            AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[WasapiTimedCapture] Stopped");
        }

        private void ReleaseResources()
        {
            if (captureClient != null)
            {
                try { Marshal.ReleaseComObject(captureClient); } catch { }
                captureClient = null;
            }

            if (audioClient != null)
            {
                try { Marshal.ReleaseComObject(audioClient); } catch { }
                audioClient = null;
            }
        }

        public void Dispose()
        {
            Stop();
            ReleaseResources();

            if (deviceChangeHandler != null)
            {
                try
                {
                    var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                    enumerator.UnregisterEndpointNotificationCallback(Marshal.GetComInterfaceForObject(deviceChangeHandler, typeof(IMMNotificationClient)));
                }
                catch { }
                deviceChangeHandler = null;
            }

            cts?.Dispose();
        }
    }

    #region Event Args

    public class AudioDataEventArgs : EventArgs
    {
        public byte[] Buffer { get; }
        public int BytesRecorded { get; }
        public AudioFormat Format { get; }

        public AudioDataEventArgs(byte[] buffer, int bytesRecorded, AudioFormat format)
        {
            Buffer = buffer;
            BytesRecorded = bytesRecorded;
            Format = format;
        }
    }

    public class ErrorEventArgs : EventArgs
    {
        public Exception Exception { get; }

        public ErrorEventArgs(Exception exception)
        {
            Exception = exception;
        }
    }

    #endregion

    #region COM Interfaces and Enums

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        void RegisterEndpointNotificationCallback(IntPtr pClient);
        void UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("7991ECD0-BCE1-4B41-949B-100B320973C3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, uint dwNewState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PROPERTYKEY key);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        void GetCount(out uint pcDevices);
        void Item(uint nDevice, out IntPtr ppDevice);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal;
        public IntPtr padding;
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        void OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        void GetId(out IntPtr ppstrId);
        void GetState(out int pdwState);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe interface IAudioClient
    {
        void Initialize(AUDCLNT_SHAREMODE ShareMode, AUDCLNT_STREAMFLAGS StreamFlags,
            long hnsBufferDuration, long hnsPeriodicity, [In] WAVEFORMATEX* pFormat, [In] Guid AudioSessionGuid);
        void GetBufferSize(out uint pNumBufferFrames);
        void GetStreamLatency(out long phnsLatency);
        void GetCurrentPadding(out uint pNumPaddingFrames);
        void IsFormatSupported(AUDCLNT_SHAREMODE ShareMode, [In] WAVEFORMATEX* pFormat, out IntPtr ppClosestMatch);
        void GetMixFormat(out WAVEFORMATEX* ppDeviceFormat);
        void GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid riid, out IntPtr ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out AUDCLNT_BUFFERFLAGS pdwFlags,
            out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        void ReleaseBuffer(uint NumFramesRead);
        void GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal unsafe struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    internal enum EDataFlow
    {
        eRender,
        eCapture,
        eAll
    }

    internal enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }

    internal enum AUDCLNT_SHAREMODE
    {
        AUDCLNT_SHAREMODE_SHARED,
        AUDCLNT_SHAREMODE_EXCLUSIVE
    }

    [Flags]
    internal enum AUDCLNT_STREAMFLAGS : uint
    {
        AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000,
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000,
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000,
        AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000
    }

    [Flags]
    internal enum AUDCLNT_BUFFERFLAGS
    {
        AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr pNotify);
        void UnregisterControlChangeNotify(IntPtr pNotify);
        void GetChannelCount(out int pnChannelCount);
        void SetMasterVolumeLevel(float fLevelDB, [In] ref Guid pguidEventContext);
        void SetMasterVolumeLevelScalar(float fLevel, [In] ref Guid pguidEventContext);
        void GetMasterVolumeLevel(out float pfLevelDB);
        void GetMasterVolumeLevelScalar(out float pfLevel);
        void SetChannelVolumeLevel(uint nChannel, float fLevelDB, [In] ref Guid pguidEventContext);
        void SetChannelVolumeLevelScalar(uint nChannel, float fLevel, [In] ref Guid pguidEventContext);
        void GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        void GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, [In] ref Guid pguidEventContext);
        void GetMute(out bool pbMute);
        void GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        void VolumeStepUp([In] ref Guid pguidEventContext);
        void VolumeStepDown([In] ref Guid pguidEventContext);
        void QueryHardwareSupport(out uint pdwHardwareSupportMask);
        void GetVolumeRange(out float pfLevelMinDB, out float pfLevelMaxDB, out float pfLevelStepDB);
    }

    #endregion
}
