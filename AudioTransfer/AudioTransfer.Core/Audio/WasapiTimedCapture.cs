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
    public sealed unsafe class WasapiTimedCapture : IAudioCapture, IDisposable
    {
        private const int BUFFER_DURATION_MS = 20; // atleast 20ms for low latency, lower leads to underruns
        private const int SAMPLE_RATE = 48000;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16; // Target 16-bit PCM for efficiency and compatibility

        private IAudioClient? audioClient;
        private IAudioCaptureClient? captureClient;
        private Thread? captureThread;
        private CancellationTokenSource? cts;
        private AutoResetEvent? _bufferEvent;
        private volatile bool isCapturing;
        private string? currentDeviceId;
        private bool isDefaultDeviceMode;
        private DeviceChangeHandler? deviceChangeHandler;
        // WTC-4 FIX: Store the enumerator used for notification registration so we
        // can unregister with the same instance in Dispose(). Old code created a new
        // MMDeviceEnumerator in Dispose() which could fail and leak the COM object.
        private IMMDeviceEnumerator? _notificationEnumerator;
        
        private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
        private const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
        private const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
        private const int AUDCLNT_E_BUFFER_SIZE_ERROR = unchecked((int)0x88890006);

        private int bufferSizeBytes;
        private int bytesPerFrame; // cached: Channels * (BitsPerSample / 8)

        // Fade-out state (WTC-6 FIX)
        private short _lastL, _lastR;
        private bool _wasActiveSinceLastSilence;
        private byte[]? _fadeBuffer;
        private const int FADE_MS = 20;

        public IAudioCapture.AudioDataAvailableDelegate? OnDataAvailable { get; set; }

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

            // WTC-1 FIX: Track waveFormat pointer so we can free it even if an
            // exception is thrown after GetMixFormat() succeeds.
            WAVEFORMATEX* waveFormat = null;
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

                // Get mix format — waveFormat is now tracked for cleanup
                audioClient.GetMixFormat(out waveFormat);

                // Modify format to requested sample rate and 16-bit PCM.
                // WASAPI Shared Mode will convert/resample if
                // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM is used.
                waveFormat->wFormatTag = 1; // WAVE_FORMAT_PCM
                waveFormat->nSamplesPerSec = SAMPLE_RATE;
                waveFormat->wBitsPerSample = 16;
                waveFormat->nBlockAlign = (ushort)(waveFormat->nChannels * 16 / 8);
                waveFormat->nAvgBytesPerSec = waveFormat->nSamplesPerSec * waveFormat->nBlockAlign;
                waveFormat->cbSize = 0;

                // RE-INIT FOR ABSOLUTE MINIMUM LATENCY (EVENT-DRIVEN + RAW MODE + MIN PERIOD)
                long defaultP, minP;
                audioClient.GetDevicePeriod(out defaultP, out minP);
                CoreLogger.Instance.Log($"[WasapiTimedCapture] Hardware latency periods: Default={defaultP / 10000.0}ms, Min={minP / 10000.0}ms");
                
                // Use minimum possible buffer for direct mode, or default for legacy
                long hnsBufferDuration = OnDataAvailable != null ? minP : 40 * 10000;
                
                var sessionGuid = Guid.Empty;
                var flags = AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_LOOPBACK |
                            AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                            AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                            AUDCLNT_STREAMFLAGS.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

                // Attempt to enable RAW mode (Windows 10+) for further 10ms reduction
                flags |= (AUDCLNT_STREAMFLAGS)0x04000000; // AUDCLNT_STREAMFLAGS_RAW

                audioClient.Initialize(
                    AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                    flags,
                    hnsBufferDuration,
                    0,
                    waveFormat,
                    ref sessionGuid);

                // Setup event handle for ultra-low latency timing
                _bufferEvent = new AutoResetEvent(false);
                audioClient.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());

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

                CoreLogger.Instance.Log($"[WasapiTimedCapture] Initialized: EVENT mode, " +
                                  $"WASAPI buffer={hnsBufferDuration / 10000.0}ms");
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Initialization error: {ex.Message}");
                throw;
            }
            finally
            {
                // WTC-1 FIX: Always free waveFormat CoTaskMem, even if an exception was thrown.
                if (waveFormat != null)
                {
                    Marshal.FreeCoTaskMem((IntPtr)waveFormat);
                }
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
            ThreadStart loopMethod = OnDataAvailable != null ? DirectCaptureLoop : CaptureLoop;

            captureThread = new Thread(loopMethod)
            {
                Name = "WasapiTimedCapture",
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            captureThread.Start();

            AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Started ({(OnDataAvailable != null ? "direct zero-copy" : "event-based")})");
        }

        /// <summary>
        /// DIRECT capture loop: reads WASAPI ? writes directly via callback.
        /// Zero managed allocation, zero intermediate copy. Maximum throughput, minimum latency.
        /// </summary>
        private void DirectCaptureLoop()
        {
            var callback = OnDataAvailable!;
            var spinner = new SpinWait();
            long totalBytesWritten = 0;
            long packetsRead = 0;
            var statsTimer = System.Diagnostics.Stopwatch.StartNew();
            var watchdogTimer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                while (isCapturing && !cts!.Token.IsCancellationRequested)
                {
                    // Wait for WASAPI to tell us more data is ready (Event-Driven)
                    // We also wake up periodically as safety
                    _bufferEvent!.WaitOne(100); 

                    bool didWork = false;
                    captureClient!.GetNextPacketSize(out var packetSize);

                    while (packetSize > 0)
                    {
                        captureClient.GetBuffer(out var dataPointer, out var numFrames, out var flags, out var devicePosition, out var qpcPosition);

                        if (numFrames > 0)
                        {
                            int byteCount = (int)(numFrames * bytesPerFrame);
                            bool isSilent = (flags & AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                            
                            if (isSilent)
                            {
                                // Transition to silence: Inject fade-out if we were active (WTC-6)
                                if (_wasActiveSinceLastSilence)
                                {
                                    InjectFadeOut();
                                }
                            }
                            else
                            {
                                UpdateLastSamples((IntPtr)dataPointer, byteCount);
                            }

                            callback((IntPtr)dataPointer, byteCount, isSilent);

                            totalBytesWritten += byteCount;
                            packetsRead++;
                            didWork = true;
                        }

                        captureClient.ReleaseBuffer(numFrames);
                        captureClient.GetNextPacketSize(out packetSize);
                        watchdogTimer.Restart();
                    }

                    if (watchdogTimer.ElapsedMilliseconds > 30000)
                    {
                        CoreLogger.Instance.Log("[WasapiDirect] Watchdog: No packets for 30s. Likely hardware stall.");
                        throw new COMException("WASAPI capture stalled", AUDCLNT_E_DEVICE_INVALIDATED);
                    }

                    // Stats every 10 seconds
                    if (statsTimer.ElapsedMilliseconds >= 10000)
                    {
                        double elapsed = statsTimer.Elapsed.TotalSeconds;
                        CoreLogger.Instance.Log($"[WasapiDirect] {packetsRead / elapsed:F0} WASAPI pkts/s, " +
                                          $"{totalBytesWritten / elapsed / 1024:F0} KB/s, ZERO-LATENCY MODE");
                        totalBytesWritten = 0;
                        packetsRead = 0;
                        statsTimer.Restart();
                    }

                    if (!didWork)
                    {
                        // Starvation check: If we were active and now have no data, inject fade-out (WTC-6)
                        if (_wasActiveSinceLastSilence)
                        {
                            InjectFadeOut();
                        }
                        // No data available � spin briefly then yield
                        spinner.SpinOnce();
                    }
                    else
                    {
                        spinner.Reset();
                    }
                }

                // Final cleanup: if loop ended while active, fade out (WTC-6)
                if (_wasActiveSinceLastSilence) InjectFadeOut();

                AudioTransfer.Core.Logging.CoreLogger.Instance.Log("[WasapiDirect] Capture loop ended.");
            }
            catch (Exception ex)
            {
                if (ex is COMException cex && 
                    (cex.HResult == AUDCLNT_E_DEVICE_INVALIDATED || 
                     cex.HResult == AUDCLNT_E_RESOURCES_INVALIDATED ||
                     cex.HResult == AUDCLNT_E_SERVICE_NOT_RUNNING))
                {
                    AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiDirect] Device/Service error (0x{cex.HResult:X8})! Signaling restart.");
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

            // WTC-3 FIX: Pre-allocate the event buffer once to avoid per-event heap allocation.
            // Old code did `new byte[bufferSizeBytes]` every 60ms = ~16 allocations/sec.
            byte[] eventBuffer = new byte[bufferSizeBytes];

            int eventCount = 0;
            int underrunCount = 0;

            var watchdogTimer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                while (isCapturing && !cts!.Token.IsCancellationRequested)
                {
                    long currentTime = timer.ElapsedMilliseconds;

                    // Continuously read available data from WASAPI
                    int bytesRead = ReadAudioData(accumulationBuffer, bufferOffset);
                    if (bytesRead > 0)
                    {
                        watchdogTimer.Restart();
                    }
                    bufferOffset += bytesRead;

                    // WTC-2 FIX: Same as DirectCaptureLoop — WASAPI Loopback produces no
                    // packets when PC is silent. Increased from 10s to 30s to avoid
                    // spurious restarts during silence periods.
                    if (watchdogTimer.ElapsedMilliseconds > 30000)
                    {
                        CoreLogger.Instance.Log("[WasapiTimedCapture] Watchdog: No data for 30s. Likely hardware stall.");
                        throw new COMException("WASAPI capture stalled", AUDCLNT_E_DEVICE_INVALIDATED);
                    }

                    // Check if it's time to fire the next event
                    if (currentTime >= nextEventTime)
                    {
                        eventCount++;

                        // WTC-3 FIX: Reuse pre-allocated eventBuffer instead of allocating new
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
                if (ex is COMException cex && 
                    (cex.HResult == AUDCLNT_E_DEVICE_INVALIDATED || 
                     cex.HResult == AUDCLNT_E_RESOURCES_INVALIDATED ||
                     cex.HResult == AUDCLNT_E_SERVICE_NOT_RUNNING))
                {
                    AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[WasapiTimedCapture] Device/Service error (0x{cex.HResult:X8})! Signaling restart.");
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

        private void UpdateLastSamples(IntPtr data, int length)
        {
            if (length < bytesPerFrame || length % bytesPerFrame != 0) return;
            short* pIn = (short*)data;
            int lastFrameIdx = (length / bytesPerFrame) - 1;
            _lastL = pIn[lastFrameIdx * CHANNELS];
            _lastR = pIn[lastFrameIdx * CHANNELS + 1];
            _wasActiveSinceLastSilence = true;
        }

        private void InjectFadeOut()
        {
            if (!_wasActiveSinceLastSilence || OnDataAvailable == null) return;
            
            int fadeSamples = (SAMPLE_RATE * FADE_MS / 1000);
            int fadeBytes = fadeSamples * bytesPerFrame;
            if (_fadeBuffer == null || _fadeBuffer.Length < fadeBytes) _fadeBuffer = new byte[fadeBytes];
            
            fixed (byte* pBuf = _fadeBuffer)
            {
                short* pOut = (short*)pBuf;
                for (int i = 0; i < fadeSamples; i++)
                {
                    float ratio = 1.0f - (float)i / fadeSamples;
                    // Linear fade-out from last known samples
                    pOut[i * CHANNELS] = (short)(_lastL * ratio);
                    pOut[i * CHANNELS + 1] = (short)(_lastR * ratio);
                }
            }
            
            fixed (byte* pBuf = _fadeBuffer)
            {
                OnDataAvailable((IntPtr)pBuf, fadeBytes, false);
            }
            
            _wasActiveSinceLastSilence = false;
            _lastL = 0;
            _lastR = 0;
            CoreLogger.Instance.Log("[WasapiTimedCapture] Injected 20ms fade-out trail.");
        }

        /// <summary>
        /// Stop capturing
        /// </summary>
        public void Stop()
        {
            if (!isCapturing)
                return;

            isCapturing = false;

            // WTC-5 FIX: Cancel CTS BEFORE Join() so the capture thread's
            // cancellation check fires and the thread can exit cleanly.
            // Old code set isCapturing=false then called cts.Cancel() — the thread
            // checks cts.Token.IsCancellationRequested in the loop condition, so
            // cancelling first ensures the thread exits on the next iteration.
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

            _bufferEvent?.Dispose();
            _bufferEvent = null;
        }

        public void Dispose()
        {
            Stop();
            ReleaseResources();

            if (deviceChangeHandler != null)
            {
                try
                {
                    // WTC-4 FIX: Use the stored enumerator instance instead of creating a new one.
                    // Old code created a new MMDeviceEnumerator which could fail and leak the COM object.
                    if (_notificationEnumerator != null)
                    {
                        _notificationEnumerator.UnregisterEndpointNotificationCallback(
                            Marshal.GetComInterfaceForObject(deviceChangeHandler, typeof(IMMNotificationClient)));
                    }
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
            long hnsBufferDuration, long hnsPeriodicity, [In] WAVEFORMATEX* pFormat, [In] ref Guid AudioSessionGuid);
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
