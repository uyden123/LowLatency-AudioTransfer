using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AudioTransfer.Core.Audio;
using AudioTransfer.Core.Logging;
using AudioTransfer.Core.Models;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// TCP client that connects to Android AudioTransmitterService (TcpAudioServer)
    /// and receives mic audio packets.
    /// 
    /// Android protocol:
    ///   - Connect to TCP server on port 5003
    ///   - Send "SUBSCRIBE" text
    ///   - Receive packets: [2 bytes Length BE] [1 byte Codec] [8 bytes Timestamp LE] [N bytes Audio Data]
    ///   - Codec 0 = PCM (48kHz, stereo, 16-bit)
    /// </summary>
    public sealed class MicReceiverClient : IDisposable
    {
        private const int HEADER_SIZE = 2;

        private TcpClient? tcpClient;
        private NetworkStream? stream;
        private Thread? receiveThread;
        private volatile bool isRunning;

        private readonly string host;
        private readonly int port;

        // Statistics
        private long _packetsReceived;
        private long _bytesReceived;
        private readonly System.Diagnostics.Stopwatch _statsTimer = new();

        public event EventHandler<AudioDataEventArgs>? DataAvailable;
        public event EventHandler<Audio.ErrorEventArgs>? ErrorOccurred;
        public event EventHandler? Disconnected;

        /// <summary>
        /// Audio format from the Android device (44.1kHz, mono, 16-bit PCM by default)
        /// </summary>
        public AudioFormat Format { get; } = new AudioFormat(44100, 1, 16);

        public bool IsConnected => tcpClient?.Connected == true && isRunning;

        public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public MicReceiverClient(string host, int port = 5003)
        {
            this.host = host;
            this.port = port;
        }

        /// <summary>
        /// Connect to Android TCP audio server and start receiving audio
        /// </summary>
        public void Connect()
        {
            if (isRunning)
                throw new InvalidOperationException("Already connected.");

            CoreLogger.Instance.Log($"[MicReceiver] Connecting to {host}:{port}...");

            tcpClient = new TcpClient();
            tcpClient.NoDelay = true;
            tcpClient.ReceiveBufferSize = 65536;

            try
            {
                tcpClient.Connect(host, port);
                CoreLogger.Instance.Log("[MicReceiver] TCP connection established.");
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.LogError("[MicReceiver] Connection failed", ex);
                throw;
            }

            stream = tcpClient.GetStream();

            byte[] subscribeMsg = Encoding.UTF8.GetBytes("SUBSCRIBE");
            stream.Write(subscribeMsg, 0, subscribeMsg.Length);
            stream.Flush();
            CoreLogger.Instance.Log("[MicReceiver] Sent SUBSCRIBE, waiting for audio data...");

            isRunning = true;
            _statsTimer.Start();

            receiveThread = new Thread(ReceiveLoop)
            {
                Name = "MicReceiverThread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            receiveThread.Start();

            CoreLogger.Instance.Log($"[MicReceiver] Connected to Android mic at {host}:{port}");
        }

        private void ReceiveLoop()
        {
            byte[] headerBuf = new byte[HEADER_SIZE];

            try
            {
                while (isRunning && tcpClient?.Connected == true)
                {
                    if (!ReadExact(stream!, headerBuf, 0, HEADER_SIZE))
                    {
                        CoreLogger.Instance.Log("[MicReceiver] Connection closed by remote.");
                        break;
                    }

                    int payloadLength = (headerBuf[0] << 8) | headerBuf[1];

                    if (payloadLength <= 0 || payloadLength > 1_000_000)
                    {
                        CoreLogger.Instance.Log($"[MicReceiver] Invalid payload length: {payloadLength}", LogLevel.Warning);
                        continue;
                    }

                    byte[] payload = new byte[payloadLength];
                    if (!ReadExact(stream!, payload, 0, payloadLength))
                    {
                        CoreLogger.Instance.Log("[MicReceiver] Connection closed while reading payload.");
                        break;
                    }

                    int codec = payload[0];
                    long timestamp = BitConverter.ToInt64(payload, 1);

                    int audioLength = payloadLength - 9;
                    
                    if (codec == 255)
                    {
                        string msg = Encoding.UTF8.GetString(payload, 9, audioLength).Trim();
                        CoreLogger.Instance.Log($"[MicReceiver] Control message: {msg}");
                        if (msg == "SERVER_SHUTDOWN")
                        {
                            isRunning = false;
                            break;
                        }
                        continue;
                    }

                    if (audioLength <= 0)
                        continue;

                    byte[] audioData = new byte[audioLength];
                    Buffer.BlockCopy(payload, 9, audioData, 0, audioLength);

                    Interlocked.Increment(ref _packetsReceived);
                    Interlocked.Add(ref _bytesReceived, audioLength);

                    DataAvailable?.Invoke(this, new AudioDataEventArgs(audioData, audioLength, Format));
                }
            }
            catch (IOException ex)
            {
                if (isRunning)
                {
                    CoreLogger.Instance.LogError("[MicReceiver] IO error", ex);
                    ErrorOccurred?.Invoke(this, new Audio.ErrorEventArgs(ex));
                }
            }
            catch (Exception ex)
            {
                if (isRunning)
                {
                    CoreLogger.Instance.LogError("[MicReceiver] Error", ex);
                    ErrorOccurred?.Invoke(this, new Audio.ErrorEventArgs(ex));
                }
            }
            finally
            {
                isRunning = false;
                CoreLogger.Instance.Log("[MicReceiver] Receive loop ended.");
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private bool ReadExact(NetworkStream ns, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = ns.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) return false;
                totalRead += read;
            }
            return true;
        }

        public void Disconnect()
        {
            if (!isRunning) return;

            isRunning = false;
            CoreLogger.Instance.Log("[MicReceiver] Disconnecting...");

            try { stream?.Close(); } catch { }
            try { tcpClient?.Close(); } catch { }

            if (receiveThread != null && receiveThread.IsAlive)
            {
                receiveThread.Join(2000);
            }

            CoreLogger.Instance.Log("[MicReceiver] Disconnected.");
        }

        public void Dispose()
        {
            Disconnect();
            stream?.Dispose();
            tcpClient?.Dispose();
        }
    }
}
