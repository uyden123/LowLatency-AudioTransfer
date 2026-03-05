using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Buffers;
using AudioTransfer.Core.Codec;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Models;
using AudioTransfer.Core.Network;
using AudioTransfer.Core.Plugins;

namespace AudioTransfer.Core.Facade
{
    /// <summary>
    /// Unified audio engine supporting multiple streaming modes.
    /// All business logic lives here; the CLI is just a thin UI layer.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        private WasapiTimedCapture? _capture;
        private UnsafeCircularBuffer? _circularBuffer;
        private UdpClient? _udpServer;
        private OpusEncoderWrapper? _opusEncoder;
        private MicDriverSidebandAgent? _sidebandAgent;
        private MdnsAdvertiser? _mdnsAdvertiser;
        private TcpListener? _controlListener;
        private WasapiPlayer? _wasapiPlayer;

        private CancellationTokenSource? _cts;
        private Thread? _senderThread;
        private Task? _listenerTask;
        private Task? _cmdListenerTask;
        private readonly List<IAudioPlugin> _plugins = new();

        private IPEndPoint? _clientEp;
        private DateTime _lastUdpSeenUtc;
        private DateTime _lastKeepAliveSent;
        private readonly object _epLock = new();
        private volatile bool _running;

        private readonly ServerStatistics _stats = new();
        private readonly ServerConfig _config;
        private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
        private DateTime _startTime;

        // TCP Broadcast mode
        private TcpListener? _tcpListener;
        private readonly ConcurrentDictionary<IPEndPoint, SubscriberInfo> _subscribers = new();
        private Task? _reporterTask;
        private Task? _subscriberListenerTask;

        public enum StreamingMode
        {
            WasapiBroadcast = 1,
            AndroidMicListener = 2,
            WasapiToAndroid = 3
        }

        // Events for CLI to subscribe to
        public event EventHandler<string>? OnLog;
        public event EventHandler<string>? OnClientConnected;
        public event EventHandler<string>? OnClientDisconnected;
        public event EventHandler? OnStopped;

        public ServerStatistics Stats => _stats;
        public ServerConfig Config => _config;
        public bool IsRunning => _running;
        public bool IsMuted { get; set; } = false;
        public TimeSpan Uptime => _running ? DateTime.UtcNow - _startTime : TimeSpan.Zero;
        public StreamingMode CurrentMode { get; private set; }

        public IReadOnlyDictionary<IPEndPoint, SubscriberInfo> Subscribers => _subscribers;
        public IReadOnlyList<IAudioPlugin> Plugins => _plugins;



        // Mode 3 specific
        public IPEndPoint? ConnectedClient
        {
            get { lock (_epLock) return _clientEp; }
        }

        // Mode 2 specific
        public UdpMicReceiver? MicReceiver => _udpMicReceiver;

        public int CircularBufferAvailable => _circularBuffer?.Available ?? 0;
        public int CircularBufferCapacity => _circularBuffer?.Capacity ?? 0;

        public AudioEngine(ServerConfig? config = null)
        {
            _config = config ?? new ServerConfig();
            
            // Initialize plugins
            _plugins.Add(new DrcPlugin());
            _plugins.Add(new EqPlugin());
        }

        public void SetSystemMute(bool muted)
        {
            try
            {
                // Use local COM definitions from AudioTransfer.Core.Audio
                var enumerator = (AudioTransfer.Core.Audio.IMMDeviceEnumerator)new AudioTransfer.Core.Audio.MMDeviceEnumerator();
                enumerator.GetDefaultAudioEndpoint(AudioTransfer.Core.Audio.EDataFlow.eRender, AudioTransfer.Core.Audio.ERole.eMultimedia, out var device);

                var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"); // IAudioEndpointVolume
                IntPtr volumePtr;
                device.Activate(ref iid, 0, IntPtr.Zero, out volumePtr);

                try
                {
                    var volume = (AudioTransfer.Core.Audio.IAudioEndpointVolume)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(volumePtr);
                    Guid context = Guid.Empty;
                    volume.SetMute(muted, ref context);
                    Log($"System mute set to: {muted}");
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.Release(volumePtr);
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to set system mute via internal API: {ex.Message}");
            }
        }

        public void TogglePlugin(string name, bool enabled)
        {
            var plugin = _plugins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (plugin != null)
            {
                plugin.IsEnabled = enabled;
                Log($"Plugin {name} {(enabled ? "enabled" : "disabled")}");
            }
        }

        private void ApplyPlugins(short[] buffer, int sampleRate, int channels)
        {
            foreach (var plugin in _plugins)
            {
                if (plugin.IsEnabled)
                {
                    plugin.Process(buffer, buffer.Length, sampleRate, channels);
                }
            }
        }

        #region Mode 3: WASAPI → Android (Low-Latency UDP)

        private string? _currentCaptureDeviceId;

        public void StartWasapiToAndroid(int listenPort, string? captureDeviceId = null)
        {
            if (_running) throw new InvalidOperationException("Already running.");
            _running = true;
            _startTime = DateTime.UtcNow;
            CurrentMode = StreamingMode.WasapiToAndroid;
            _cts = new CancellationTokenSource();
            _currentCaptureDeviceId = captureDeviceId;

            // Firewall
            FirewallHelper.AllowPort(listenPort, "AudioOverLAN_WasapiToAndroid", "UDP");

            _udpServer = new UdpClient(listenPort);
            _udpServer.Client.SendBufferSize = 1024 * 1024;
            _udpServer.Client.ReceiveBufferSize = 1024 * 1024;

            Log($"UDP Server listening on port {listenPort}");

            _capture = new WasapiTimedCapture();

            const int PACKET_DURATION_MS = 20;
            int bytesPerFrame = 2 * (16 / 8);
            int framesPerPacket = 48000 * PACKET_DURATION_MS / 1000;
            int packetSizeBytes = framesPerPacket * bytesPerFrame;

            _circularBuffer = new UnsafeCircularBuffer(packetSizeBytes * 25);
            _capture.DirectOutputBuffer = _circularBuffer;
            
            // Use a semaphore and debounce timer to prevent rapid-fire restart loops
            SemaphoreSlim restartLock = new SemaphoreSlim(1, 1);
            DateTime lastRestartTime = DateTime.MinValue;

            _capture.DefaultDeviceChanged += (s, e) => {
                if (!_running) return;
                
                // Debounce: ignore changes within 500ms of each other
                if ((DateTime.UtcNow - lastRestartTime).TotalMilliseconds < 500) return;
                lastRestartTime = DateTime.UtcNow;

                Log("[AudioEngine] Device change detected. Restarting WASAPI...");
                
                Task.Run(async () => {
                    if (!await restartLock.WaitAsync(0)) return; // Only one restart at a time
                    try {
                        await Task.Delay(1000); // Give Windows time to stabilize the new device
                        _capture?.Stop();
                        _capture?.Initialize(_currentCaptureDeviceId);
                        _capture?.Start();
                        Log("[AudioEngine] WASAPI restarted successfully.");
                    } catch (Exception ex) {
                        Log($"[AudioEngine] Failed to restart WASAPI: {ex.Message}");
                    } finally {
                        restartLock.Release();
                        lastRestartTime = DateTime.UtcNow; // Update again to prevent immediate trigger after success
                    }
                });
            };

            _capture.Initialize(captureDeviceId);

            _opusEncoder = new OpusEncoderWrapper(2, 128000, PACKET_DURATION_MS, 10, true, false);
            _capture.Start();

            // Start mDNS/DNS-SD advertisement so Android can auto-discover
            _mdnsAdvertiser = new MdnsAdvertiser(
                "AudioOverLAN",
                "_audiooverlan._udp",
                listenPort,
                new Dictionary<string, string> { { "mode", "3" } });
            _mdnsAdvertiser.Start();

            // Listener for SUBSCRIBE / HEARTBEAT messages
            _listenerTask = Task.Run(async () =>
            {
                while (_running)
                {
                    try
                    {
                        var result = await _udpServer.ReceiveAsync();
                        string msg = Encoding.UTF8.GetString(result.Buffer).Trim();
                        bool isSubscribe = string.Equals(msg, "SUBSCRIBE", StringComparison.OrdinalIgnoreCase);
                        bool isHeartbeat = string.Equals(msg, "HEARTBEAT", StringComparison.OrdinalIgnoreCase);
                        bool isUnsubscribe = string.Equals(msg, "UNSUBSCRIBE", StringComparison.OrdinalIgnoreCase) || 
                                           string.Equals(msg, "DISCONNECT", StringComparison.OrdinalIgnoreCase);

                        if (isUnsubscribe)
                        {
                            lock (_epLock)
                            {
                                if (_clientEp != null && Equals(_clientEp, result.RemoteEndPoint))
                                {
                                    Log($"Android {result.RemoteEndPoint} unsubscribed.");
                                    OnClientDisconnected?.Invoke(this, result.RemoteEndPoint.ToString());
                                    _clientEp = null;
                                }
                            }
                        }
                        else if (isSubscribe || isHeartbeat)
                        {
                            bool isNew = false;
                            lock (_epLock)
                            {
                                _lastUdpSeenUtc = DateTime.UtcNow;
                                if (_clientEp == null || !Equals(_clientEp, result.RemoteEndPoint))
                                {
                                    _clientEp = result.RemoteEndPoint;
                                    isNew = true;
                                }
                            }

                            if (isNew)
                            {
                                string verb = isSubscribe ? "subscribed" : "detected via heartbeat";
                                Log($"Android {result.RemoteEndPoint} {verb}.");
                                OnClientConnected?.Invoke(this, result.RemoteEndPoint.ToString());
                            }

                            // Always send ACK so client knows server is alive
                            try
                            {
                                byte[] ack = Encoding.UTF8.GetBytes(isSubscribe ? "SUBSCRIBE_ACK" : "HEARTBEAT_ACK");
                                _udpServer.Send(ack, ack.Length, result.RemoteEndPoint);
                            }
                            catch { }
                        }
                    }
                    catch
                    {
                        if (!_running) break;
                    }
                }
            });
            
            _cmdListenerTask = StartTcpControlListener(listenPort);

            _senderThread = new Thread(() => UdpSenderLoop(packetSizeBytes, framesPerPacket))
            {
                Name = "AudioSenderThread",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _senderThread.Start();
        }

        public async Task SwitchCaptureDevice(string? deviceId)
        {
            if (!_running || CurrentMode != StreamingMode.WasapiToAndroid || _capture == null) return;

            _currentCaptureDeviceId = deviceId;
            Log($"[AudioEngine] Switching capture device to: {deviceId ?? "Default"}");

            // Execute switch on a background thread to avoid blocking the UI
            _ = Task.Run(() =>
            {
                try
                {
                    _capture.Stop();
                    _capture.Initialize(deviceId);
                    _capture.Start();
                    Log("[AudioEngine] Device switch successful.");
                }
                catch (Exception ex)
                {
                    Log($"[AudioEngine] Failed to switch device: {ex.Message}");
                }
            });
        }

        private void UdpSenderLoop(int packetSizeBytes, int framesPerPacket)
        {
            byte[] packetData = new byte[packetSizeBytes];
            short[] packetPcm = new short[packetSizeBytes / 2];
            byte[] sendBuffer = new byte[8192];
            var spinner = new SpinWait();

            Log("Sender thread started.");
            ushort seqNum = 0;
            long timestampSamples = 0;

            try
            {
                while (_running && !_cts!.Token.IsCancellationRequested)
                {
                    IPEndPoint? targetEp;
                    lock (_epLock)
                    {
                        targetEp = _clientEp;
                        if (targetEp != null && (DateTime.UtcNow - _lastUdpSeenUtc).TotalSeconds > 5)
                        {
                            Log($"Android {targetEp} disconnected (heartbeat timeout).");
                            OnClientDisconnected?.Invoke(this, targetEp.ToString());
                            _clientEp = null;
                            targetEp = null;
                        }
                    }

                    if (targetEp == null)
                    {
                        _circularBuffer!.Clear();
                        spinner.SpinOnce();
                        continue;
                    }

                    // Check if audio data is available
                    if (_circularBuffer!.PeekAvailable() < packetSizeBytes)
                    {
                        // No audio data — send keepalive every ~2 seconds so client knows we're alive
                        if ((DateTime.UtcNow - _lastKeepAliveSent).TotalMilliseconds > 2000)
                        {
                            try
                            {
                                byte[] keepAlive = Encoding.UTF8.GetBytes("HEARTBEAT_ACK");
                                _udpServer!.Send(keepAlive, keepAlive.Length, targetEp);
                                _lastKeepAliveSent = DateTime.UtcNow;
                            }
                            catch { }
                        }
                        spinner.SpinOnce();
                        continue;
                    }
                    spinner.Reset();

                    int bytesRead = _circularBuffer.Read(packetData, 0, packetSizeBytes);
                    if (bytesRead < packetSizeBytes) continue;

                    Buffer.BlockCopy(packetData, 0, packetPcm, 0, bytesRead);
                    
                    ApplyPlugins(packetPcm, 48000, _config.Audio.Channels);

                    var encodedPackets = _opusEncoder!.Encode(packetPcm);

                    foreach (var opusData in encodedPackets)
                    {
                        sendBuffer[0] = (byte)(seqNum >> 8);
                        sendBuffer[1] = (byte)(seqNum & 0xFF);
                        sendBuffer[2] = 1;
                        BitConverter.TryWriteBytes(new Span<byte>(sendBuffer, 3, 8), timestampSamples);
                        long wallClock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        BitConverter.TryWriteBytes(new Span<byte>(sendBuffer, 11, 8), wallClock);
                        Buffer.BlockCopy(opusData, 0, sendBuffer, 19, opusData.Length);

                        int totalPacketSize = 19 + opusData.Length;

                        try
                        {
                            _udpServer!.Send(sendBuffer, totalPacketSize, targetEp);
                            _stats.IncrementPacketsSent();
                            _stats.IncrementBytesSent(totalPacketSize);
                        }
                        catch { }

                        seqNum++;
                        timestampSamples += framesPerPacket;
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running) CoreLogger.Instance.LogError("Sender fatal error", ex);
            }
            finally
            {
                Log("Sender thread stopped.");
            }
        }

        private void SendUdpControlMessage(string msg)
        {
            if (_udpServer == null) return;
            IPEndPoint? targetEp;
            lock (_epLock) { targetEp = _clientEp; }
            if (targetEp == null) return;

            byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
            byte[] sendBuffer = new byte[19 + msgBytes.Length];
            // Header: Seq=0, Codec=255
            sendBuffer[2] = 255;
            Buffer.BlockCopy(msgBytes, 0, sendBuffer, 19, msgBytes.Length);

            try { _udpServer.Send(sendBuffer, sendBuffer.Length, targetEp); } catch { }
        }

        #endregion

        #region TCP Command Listener (JSON)

        private async Task StartTcpControlListener(int port)
        {
            _controlListener = new TcpListener(IPAddress.Any, port);
            _controlListener.Start();
            Log($"TCP Control Listener started on port {port}");

            try
            {
                while (_running)
                {
                    using var client = await _controlListener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("command", out var cmdProp))
                        {
                            string cmd = cmdProp.GetString() ?? "";
                            if (cmd == "set_bitrate" && root.TryGetProperty("value", out var valProp))
                            {
                                int newBitrate = valProp.GetInt32();
                                if (_opusEncoder != null)
                                {
                                    _opusEncoder.Bitrate = newBitrate;
                                }
                            }
                            else if (cmd == "set_mute" && root.TryGetProperty("value", out var muteProp))
                            {
                                bool muted = muteProp.GetBoolean();
                                IsMuted = muted;
                                SetSystemMute(muted);
                            }
                            else if (cmd == "set_drc" && root.TryGetProperty("value", out var drcProp))
                            {
                                bool enabled = drcProp.GetBoolean();
                                TogglePlugin("DRC", enabled);
                            }
                            else if (cmd == "set_eq" && root.TryGetProperty("value", out var eqProp))
                            {
                                string preset = eqProp.GetString() ?? "None";
                                var eqPlugin = _plugins.OfType<EqPlugin>().FirstOrDefault();
                                eqPlugin?.SetPreset(preset);
                                Log($"EQ Preset set to: {preset}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Control JSON error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running) Log($"TCP Control fatal error: {ex.Message}");
            }
            finally
            {
                _controlListener?.Stop();
            }
        }

        #endregion

        #region Mode 2: Android Mic → PC (UDP with Jitter/PLC/Resample)

        private UdpMicReceiver? _udpMicReceiver;
        private MicJitterBuffer? _micJitterBuffer;
        private OpusDecoderWrapper? _opusDecoder;
        private Thread? _micPlaybackThread;
        private MdnsDiscoveryClient? _micDiscoveryClient;

        // Clock drift resampling state (mirrors Android AudioService)
        private long _baseMinDelay = -1;
        private double _currentRatio = 1.0;
        private double _currentPhase = 0.0;
        private short[] _micPcmBuffer = new short[48000 * 60 / 1000]; // Max 60ms Opus frame
        private short[] _micResampledBuffer = new short[48000 * 120 / 1000]; // Space for expansion

        public MicJitterBuffer? MicJitter => _micJitterBuffer;

        public async Task<bool> StartAndroidMicListenerAsync(string androidIp, int androidPort = 5003, int playbackDeviceId = -1)
        {
            if (_running) throw new InvalidOperationException("Already running.");

            // 1. Proactively probe Android device if it's a specific IP
            if (!string.IsNullOrEmpty(androidIp) && androidIp != "0.0.0.0" && androidIp != "Any")
            {
                Log($"[Player] Probing Android device at {androidIp}...");
                // Start a background task for verification but don't block the UI
                _ = Task.Run(async () => {
                    bool alive = await VerifyDeviceAsync(androidIp, androidPort);
                    if (!alive)
                    {
                        Log($"[Player] Warning: Android device at {androidIp} is not currently responding. Will continue to try in background.");
                    }
                });
            }

            _running = true;
            _startTime = DateTime.UtcNow;
            CurrentMode = StreamingMode.AndroidMicListener;
            _cts = new CancellationTokenSource();

            // Setup sideband to our custom driver if possible
            _sidebandAgent = new MicDriverSidebandAgent();
            if (!_sidebandAgent.Open())
            {
                Log("INFO: Sideband agent not found (Normal if not using custom VirtualMic driver).");
            }

            // Setup Wasapi Player if device requested
            if (playbackDeviceId >= 0)
            {
                _wasapiPlayer = new WasapiPlayer(1000, (uint)playbackDeviceId);
                _wasapiPlayer.Initialize();
                _wasapiPlayer.Start();
                Log($"Forwarding audio to playback device ID: {playbackDeviceId}");
            }

            // Firewall
            FirewallHelper.AllowPort(androidPort, "AudioOverLAN_MicReceiver", "UDP");

            // Initialize jitter buffer, Opus decoder, UDP receiver
            _micJitterBuffer = new MicJitterBuffer();
            _opusDecoder = new OpusDecoderWrapper(48000, 1, 20); // Android sends 48kHz mono Opus

            System.Net.IPEndPoint? targetEp = null;
            if (!string.IsNullOrEmpty(androidIp) && androidIp != "0.0.0.0" && androidIp != "Any")
            {
                if (System.Net.IPAddress.TryParse(androidIp, out var addr))
                {
                    targetEp = new System.Net.IPEndPoint(addr, androidPort);
                }
            }

            _udpMicReceiver = new UdpMicReceiver(androidPort, _micJitterBuffer, targetEp);
            
            _udpMicReceiver.OnAndroidConnected += (s, e) =>
            {
                Log($"Android mic connected via UDP from {_udpMicReceiver.ConnectedEndpoint}");
                OnClientConnected?.Invoke(this, _udpMicReceiver.ConnectedEndpoint?.ToString() ?? "Android");
            };

            _udpMicReceiver.OnAndroidDisconnected += (s, e) =>
            {
                Log("Android mic disconnected!");
                OnClientDisconnected?.Invoke(this, "Android");
            };

            _udpMicReceiver.OnControlMessage += (s, msg) =>
            {
                Log($"[MicReceiver] Control: {msg}");
            };

            _udpMicReceiver.Start();
            Log($"[MicReceiver] Listening on port {androidPort}. Proactive target: {targetEp?.ToString() ?? "None (Discovery)"}");

            // If no specific IP provided (or "0.0.0.0"), start discovery to find and ping Android
            if (targetEp == null)
            {
                _micDiscoveryClient = new MdnsDiscoveryClient("_audiooverlan-mic._udp");
                _micDiscoveryClient.OnServiceDiscovered += async (s, service) =>
                {
                    Log($"[Auto-Discovery] Found Android mic at {service.IPAddress}:{service.Port}. Updating receiver target...");
                    if (System.Net.IPAddress.TryParse(service.IPAddress, out var addr))
                    {
                        _udpMicReceiver.TargetEndpoint = new System.Net.IPEndPoint(addr, service.Port);
                    }
                };
                _micDiscoveryClient.Start();
                Log("Started mDNS discovery for Android mic...");
            }

            // Start playback thread (pulls from jitter buffer, decodes, resamples, outputs)
            _micPlaybackThread = new Thread(MicPlaybackLoop)
            {
                Name = "MicPlaybackThread",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _micPlaybackThread.Start();
            return true;
        }

        private async Task<bool> VerifyDeviceAsync(string ip, int port)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 2000; // PC side timeout
                var target = new IPEndPoint(IPAddress.Parse(ip), port);
                
                // Send SUBSCRIBE as a probe
                byte[] ping = Encoding.UTF8.GetBytes("SUBSCRIBE");
                await udp.SendAsync(ping, ping.Length, target);

                // Wait for any response (Android responds to SUBSCRIBE with SUBSCRIBE or SUBSCRIBE_ACK)
                var receiveTask = udp.ReceiveAsync();
                var timeoutTask = Task.Delay(2000);
                
                var finishedTask = await Task.WhenAny(receiveTask, timeoutTask);
                if (finishedTask == receiveTask)
                {
                    // Success!
                    return true;
                }
                
                return false; // Timeout
            }
            catch (Exception ex)
            {
                Log($"[Player] Verification error: {ex.Message}");
                return false;
            }
        }

        private void MicPlaybackLoop()
        {
            Log("[MicPlayback] Playback thread started.");
            var spinner = new SpinWait();

            try
            {
                while (_running && !_cts!.Token.IsCancellationRequested)
                {
                    var packet = _micJitterBuffer!.Take();

                    if (packet == null)
                    {
                        if (_micJitterBuffer.IsBuffering)
                        {
                            Thread.Sleep(2);
                            continue;
                        }
                        spinner.SpinOnce();
                        continue;
                    }
                    spinner.Reset();

                    int samplesPerChannel;

                    if (packet.IsPLC)
                    {
                        // Gap detected → generate PLC audio
                        samplesPerChannel = _opusDecoder!.DecodePLCTo(_micPcmBuffer);
                        _micJitterBuffer.RecyclePacket(packet);
                    }
                    else
                    {
                        // Normal Opus decode (Zero-allocation)
                        samplesPerChannel = _opusDecoder!.DecodeTo(packet.Data, 0, packet.Length, _micPcmBuffer);

                        if (samplesPerChannel > 0)
                        {
                            // Clock drift monitoring
                            var stats = _micJitterBuffer.GetStatistics();

                            if (_baseMinDelay == -1 && stats.MinDelay > 0)
                            {
                                _baseMinDelay = stats.MinDelay;
                            }
                            else if (_baseMinDelay > 0 && stats.MinDelay > 0)
                            {
                                long drift = stats.MinDelay - _baseMinDelay;

                                if (drift > 15) { _currentRatio = 1.002; _baseMinDelay += 5; }
                                else if (drift < -15) { _currentRatio = 0.998; _baseMinDelay -= 5; }
                                else if (Math.Abs(drift) < 5) { _currentRatio = 1.0; }
                            }
                        }

                        _stats.IncrementPacketsSent();
                        _stats.IncrementBytesSent(packet.Length);
                        _micJitterBuffer.RecyclePacket(packet);
                    }

                    if (samplesPerChannel > 0)
                    {
                        // Apply clock drift resampling (Zero-allocation)
                        int resampledCount = ResampleInto(_micPcmBuffer, samplesPerChannel, 1, _currentRatio, _micResampledBuffer);

                        // Output to WasapiPlayer (Zero-allocation Mono -> Stereo expansion)
                        if (_wasapiPlayer != null)
                        {
                            _wasapiPlayer.AddSamples(_micResampledBuffer, 0, resampledCount);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running) CoreLogger.Instance.LogError("[MicPlayback] Fatal error", ex);
            }
            finally
            {
                Log("[MicPlayback] Playback thread stopped.");
            }
        }

        private int ResampleInto(short[] input, int samplesIn, int channels, double ratio, short[] output)
        {
            if (Math.Abs(ratio - 1.0) < 0.0001)
            {
                _currentPhase = 0.0;
                int total = samplesIn * channels;
                Array.Copy(input, 0, output, 0, total);
                return total;
            }

            int outIdx = 0;
            double step = 1.0 / ratio;

            while (_currentPhase < samplesIn)
            {
                int idx = (int)_currentPhase;
                double frac = _currentPhase - idx;

                for (int c = 0; c < channels; c++)
                {
                    short val0 = input[idx * channels + c];
                    short val1 = (idx + 1 < samplesIn) ? input[(idx + 1) * channels + c] : val0;
                    output[outIdx * channels + c] = (short)(val0 + frac * (val1 - val0));
                }
                outIdx++;
                _currentPhase += step;
            }

            _currentPhase -= samplesIn;
            return outIdx * channels;
        }


        #endregion

        private static async Task SendControlMessage(NetworkStream stream, string message)
        {
            try
            {
                byte[] msgBytes = Encoding.UTF8.GetBytes(message);
                byte[] lenPrefix = new byte[2];
                lenPrefix[0] = (byte)(msgBytes.Length >> 8);
                lenPrefix[1] = (byte)(msgBytes.Length & 0xFF);

                await stream.WriteAsync(lenPrefix, 0, 2).ConfigureAwait(false);
                await stream.WriteAsync(msgBytes, 0, msgBytes.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch { }
        }

        #region Shared Utilities

        /// <summary>
        /// Get all local IPv4 addresses for display.
        /// </summary>
        public static string[] GetLocalIPAddresses()
        {
            try
            {
                var hostName = Dns.GetHostName();
                return Dns.GetHostAddresses(hostName)
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .Distinct()
                    .ToArray();
            }
            catch
            {
                return new[] { "127.0.0.1" };
            }
        }

        private void Log(string message)
        {
            CoreLogger.Instance.Log(message);
            OnLog?.Invoke(this, message);
        }

        #endregion

        #region Lifecycle

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            Log("Shutting down...");

            // Notify UDP client if in mode 3
            if (CurrentMode == StreamingMode.WasapiToAndroid)
            {
                SendUdpControlMessage("SERVER_SHUTDOWN");
            }

            _cts?.Cancel();
            _capture?.Stop();
            _udpMicReceiver?.Stop();
            _micJitterBuffer?.Clear();
            _wasapiPlayer?.Stop();

            // Graceful shutdown for broadcast subscribers
            foreach (var ep in _subscribers.Keys)
            {
                if (_subscribers.TryGetValue(ep, out var info))
                {
                    try
                    {
                        SendControlMessage(info.Stream, "SERVER_SHUTDOWN").Wait(500);
                        info.Dispose();
                    }
                    catch { }
                }
            }
            _subscribers.Clear();

            try { _mdnsAdvertiser?.Stop(); } catch { }
            try { _micDiscoveryClient?.Stop(); } catch { }
            try { _udpServer?.Close(); } catch { }
            try { _tcpListener?.Stop(); } catch { }
            try { _controlListener?.Stop(); } catch { }
            finally { _controlListener = null; }

            _senderThread?.Join(1000);
            _micPlaybackThread?.Join(1000);
            
            Log("Engine stopped.");
            OnStopped?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            
            _capture?.Dispose();
            _circularBuffer?.Dispose();
            _opusEncoder?.Dispose();
            _udpServer?.Dispose();
            _udpMicReceiver?.Dispose();
            _opusDecoder?.Dispose();
            _wasapiPlayer?.Dispose();
            _sidebandAgent?.Dispose();
            _mdnsAdvertiser?.Dispose();
            _micDiscoveryClient?.Dispose();
            _cts?.Dispose();

            // Set to null to be safe
            _capture = null;
            _circularBuffer = null;
            _opusEncoder = null;
            _udpServer = null;
            _udpMicReceiver = null;
            _micJitterBuffer = null;
            _opusDecoder = null;
            _sidebandAgent = null;
            _mdnsAdvertiser = null;
            _micDiscoveryClient = null;
        }

        #endregion
    }
}
