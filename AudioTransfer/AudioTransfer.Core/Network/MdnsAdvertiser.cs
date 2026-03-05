using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// Advertises an mDNS/DNS-SD service so that Android's NsdManager can discover it.
    /// Service type: _audiooverlan._udp.local.
    /// </summary>
    public sealed class MdnsAdvertiser : IDisposable
    {
        private static readonly IPAddress MdnsMulticastAddress = IPAddress.Parse("224.0.0.251");
        private const int MdnsPort = 5353;

        private readonly string _instanceName;
        private readonly string _serviceType; // e.g. "_audiooverlan._udp"
        private readonly int _servicePort;
        private readonly Dictionary<string, string> _txtRecords;

        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;
        private Task? _announceTask;
        private volatile bool _running;

        // Pre-built DNS names
        private readonly string _serviceDnsName;   // "_audiooverlan._udp.local."
        private readonly string _instanceDnsName;  // "AudioOverLAN._audiooverlan._udp.local."
        private readonly string _hostName;          // "hostname.local."

        public MdnsAdvertiser(string instanceName, string serviceType, int servicePort,
            Dictionary<string, string>? txtRecords = null)
        {
            _instanceName = instanceName;
            _serviceType = serviceType;
            _servicePort = servicePort;
            _txtRecords = txtRecords ?? new Dictionary<string, string>();

            var hostname = Dns.GetHostName();
            _hostName = $"{hostname}.local.";
            _serviceDnsName = $"{_serviceType}.local.";
            _instanceDnsName = $"{_instanceName}.{_serviceType}.local.";
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();

            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                _udpClient.JoinMulticastGroup(MdnsMulticastAddress);

                CoreLogger.Instance.Log($"[mDNS] Advertising '{_instanceName}' as {_serviceDnsName} on port {_servicePort}");
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.LogError("[mDNS] Failed to start", ex);
                _running = false;
                return;
            }

            // Listen for mDNS queries
            _listenerTask = Task.Run(() => ListenLoop(_cts.Token));

            // Periodic announcement (every 10 seconds)
            _announceTask = Task.Run(async () =>
            {
                // Initial burst: announce 3 times quickly
                for (int i = 0; i < 3 && _running; i++)
                {
                    SendAnnouncement();
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                }

                // Then periodic
                while (_running && !_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(10_000, _cts.Token).ConfigureAwait(false);
                        SendAnnouncement();
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            // Send goodbye (TTL=0)
            try { SendGoodbye(); } catch { }

            _cts?.Cancel();

            try { _udpClient?.DropMulticastGroup(MdnsMulticastAddress); } catch { }
            try { _udpClient?.Close(); } catch { }
            _udpClient?.Dispose();
            _udpClient = null;

            CoreLogger.Instance.Log("[mDNS] Advertiser stopped.");
        }

        private void ListenLoop(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udpClient?.Receive(ref remoteEp);
                    if (data == null || data.Length < 12) continue;

                    // Parse DNS header
                    int flags = (data[2] << 8) | data[3];
                    bool isQuery = (flags & 0x8000) == 0; // QR bit = 0 means query
                    if (!isQuery) continue;

                    int qdcount = (data[4] << 8) | data[5];
                    if (qdcount == 0) continue;

                    // Parse question section
                    int offset = 12;
                    for (int i = 0; i < qdcount && offset < data.Length; i++)
                    {
                        string qname = ReadDnsName(data, ref offset);
                        if (offset + 4 > data.Length) break;
                        int qtype = (data[offset] << 8) | data[offset + 1];
                        offset += 4; // skip QTYPE + QCLASS

                        // Check if query matches our service
                        bool matchesBrowse = qname.Equals(_serviceDnsName, StringComparison.OrdinalIgnoreCase)
                                          || qname.Equals("_services._dns-sd._udp.local.", StringComparison.OrdinalIgnoreCase);
                        bool matchesInstance = qname.Equals(_instanceDnsName, StringComparison.OrdinalIgnoreCase);
                        bool matchesHost = qname.Equals(_hostName, StringComparison.OrdinalIgnoreCase);

                        if (matchesBrowse || matchesInstance || matchesHost)
                        {
                            // Respond with our records
                            SendAnnouncement();
                            break;
                        }
                    }
                }
                catch (SocketException) { if (!_running) break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private void SendAnnouncement()
        {
            try
            {
                var response = BuildAnnouncementPacket(ttl: 120);
                var endpoint = new IPEndPoint(MdnsMulticastAddress, MdnsPort);
                _udpClient?.Send(response, response.Length, endpoint);
            }
            catch (Exception ex)
            {
                if (_running)
                    CoreLogger.Instance.LogError("[mDNS] Send error", ex);
            }
        }

        private void SendGoodbye()
        {
            var response = BuildAnnouncementPacket(ttl: 0);
            var endpoint = new IPEndPoint(MdnsMulticastAddress, MdnsPort);
            _udpClient?.Send(response, response.Length, endpoint);
        }

        /// <summary>
        /// Build a full mDNS announcement response containing:
        ///   - PTR: _audiooverlan._udp.local. -> AudioOverLAN._audiooverlan._udp.local.
        ///   - SRV: instance -> hostname:port
        ///   - TXT: instance -> txt records
        ///   - A:   hostname.local. -> IP addresses
        /// </summary>
        /// <summary>
        /// Write a 16-bit unsigned value as big-endian bytes.
        /// This avoids overflow when the value exceeds short range (e.g. 0x8001 for cache-flush class).
        /// </summary>
        private static void WriteUInt16BE(System.IO.BinaryWriter bw, ushort value)
        {
            bw.Write((byte)(value >> 8));
            bw.Write((byte)(value & 0xFF));
        }

        /// <summary>
        /// Write a 32-bit signed value as big-endian bytes.
        /// </summary>
        private static void WriteInt32BE(System.IO.BinaryWriter bw, int value)
        {
            bw.Write((byte)((value >> 24) & 0xFF));
            bw.Write((byte)((value >> 16) & 0xFF));
            bw.Write((byte)((value >> 8) & 0xFF));
            bw.Write((byte)(value & 0xFF));
        }

        private byte[] BuildAnnouncementPacket(int ttl)
        {
            var ms = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(ms);

            // DNS Header (response, authoritative)
            WriteUInt16BE(bw, 0x0000);          // Transaction ID
            WriteUInt16BE(bw, 0x8400);          // Flags: Response + Authoritative
            WriteUInt16BE(bw, 0x0000);          // QDCOUNT
            int ancountPos = (int)ms.Position;
            WriteUInt16BE(bw, 0x0000);          // ANCOUNT (placeholder)
            WriteUInt16BE(bw, 0x0000);          // NSCOUNT
            WriteUInt16BE(bw, 0x0000);          // ARCOUNT

            int answerCount = 0;

            // 1. PTR record: _audiooverlan._udp.local. -> instance
            WriteDnsName(bw, _serviceDnsName);
            WriteUInt16BE(bw, 12);                              // TYPE PTR
            WriteUInt16BE(bw, 0x0001);                          // CLASS IN
            WriteInt32BE(bw, ttl);                              // TTL
            byte[] instanceNameBytes = EncodeDnsName(_instanceDnsName);
            WriteUInt16BE(bw, (ushort)instanceNameBytes.Length); // RDLENGTH
            bw.Write(instanceNameBytes);
            answerCount++;

            // 2. SRV record: instance -> hostname:port
            WriteDnsName(bw, _instanceDnsName);
            WriteUInt16BE(bw, 33);                              // TYPE SRV
            WriteUInt16BE(bw, 0x8001);                          // CLASS IN + cache-flush
            WriteInt32BE(bw, ttl);                              // TTL
            byte[] hostNameBytes = EncodeDnsName(_hostName);
            WriteUInt16BE(bw, (ushort)(6 + hostNameBytes.Length)); // RDLENGTH
            WriteUInt16BE(bw, 0);                               // Priority
            WriteUInt16BE(bw, 0);                               // Weight
            WriteUInt16BE(bw, (ushort)_servicePort);            // Port
            bw.Write(hostNameBytes);                            // Target
            answerCount++;

            // 3. TXT record
            WriteDnsName(bw, _instanceDnsName);
            WriteUInt16BE(bw, 16);                              // TYPE TXT
            WriteUInt16BE(bw, 0x8001);                          // CLASS IN + cache-flush
            WriteInt32BE(bw, ttl);                              // TTL
            byte[] txtData = EncodeTxtRecords();
            WriteUInt16BE(bw, (ushort)txtData.Length);           // RDLENGTH
            bw.Write(txtData);
            answerCount++;

            // 4. A records for each local IP
            foreach (var ip in GetLocalIPv4Addresses())
            {
                WriteDnsName(bw, _hostName);
                WriteUInt16BE(bw, 1);                           // TYPE A
                WriteUInt16BE(bw, 0x8001);                      // CLASS IN + cache-flush
                WriteInt32BE(bw, ttl);                          // TTL
                WriteUInt16BE(bw, 4);                           // RDLENGTH
                bw.Write(ip.GetAddressBytes());                 // Address
                answerCount++;
            }

            // 5. PTR record for service enumeration: _services._dns-sd._udp.local. -> _audiooverlan._udp.local.
            WriteDnsName(bw, "_services._dns-sd._udp.local.");
            WriteUInt16BE(bw, 12);                              // TYPE PTR
            WriteUInt16BE(bw, 0x0001);                          // CLASS IN
            WriteInt32BE(bw, ttl);                              // TTL
            byte[] serviceTypeBytes = EncodeDnsName(_serviceDnsName);
            WriteUInt16BE(bw, (ushort)serviceTypeBytes.Length);
            bw.Write(serviceTypeBytes);
            answerCount++;

            // Fix answer count
            var result = ms.ToArray();
            result[ancountPos]     = (byte)(answerCount >> 8);
            result[ancountPos + 1] = (byte)(answerCount & 0xFF);

            return result;
        }

        #region DNS Encoding Helpers

        private static void WriteDnsName(System.IO.BinaryWriter bw, string name)
        {
            bw.Write(EncodeDnsName(name));
        }

        private static byte[] EncodeDnsName(string name)
        {
            var ms = new System.IO.MemoryStream();
            if (name.EndsWith(".")) name = name[..^1];
            var labels = name.Split('.');
            foreach (var label in labels)
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
            }
            ms.WriteByte(0); // Root label
            return ms.ToArray();
        }

        private static string ReadDnsName(byte[] data, ref int offset)
        {
            var parts = new List<string>();
            bool jumped = false;
            int savedOffset = -1;
            int maxOffset = data.Length;

            while (offset < maxOffset)
            {
                byte len = data[offset];
                if (len == 0) { offset++; break; }

                if ((len & 0xC0) == 0xC0)
                {
                    // Pointer
                    if (!jumped) savedOffset = offset + 2;
                    offset = ((len & 0x3F) << 8) | data[offset + 1];
                    jumped = true;
                    continue;
                }

                offset++;
                if (offset + len > data.Length) break;
                parts.Add(Encoding.UTF8.GetString(data, offset, len));
                offset += len;
            }

            if (jumped && savedOffset >= 0) offset = savedOffset;
            return string.Join(".", parts) + ".";
        }

        private byte[] EncodeTxtRecords()
        {
            var ms = new System.IO.MemoryStream();
            if (_txtRecords.Count == 0)
            {
                // Empty TXT record must have at least one byte (length 0)
                ms.WriteByte(0);
            }
            else
            {
                foreach (var kv in _txtRecords)
                {
                    string entry = $"{kv.Key}={kv.Value}";
                    byte[] bytes = Encoding.UTF8.GetBytes(entry);
                    ms.WriteByte((byte)Math.Min(bytes.Length, 255));
                    ms.Write(bytes, 0, Math.Min(bytes.Length, 255));
                }
            }
            return ms.ToArray();
        }

        private static IPAddress[] GetLocalIPv4Addresses()
        {
            try
            {
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .ToArray();
            }
            catch
            {
                return new[] { IPAddress.Loopback };
            }
        }

        #endregion

        public void Dispose()
        {
            Stop();
        }
    }
}
