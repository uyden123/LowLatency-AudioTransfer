using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// mDNS/DNS-SD discovery client for finding Android mic services.
    /// Service type: _audiooverlan-mic._udp.local.
    /// 
    /// This is the REVERSE of MdnsAdvertiser:
    /// - MdnsAdvertiser: PC ADVERTISES _audiooverlan._udp for Android to discover
    /// - MdnsDiscoveryClient: PC DISCOVERS _audiooverlan-mic._udp advertised by Android
    /// </summary>
    public sealed class MdnsDiscoveryClient : IDisposable
    {
        private static readonly IPAddress MdnsMulticastAddress = IPAddress.Parse("224.0.0.251");
        private const int MdnsPort = 5353;

        private readonly string _serviceType; // "_audiooverlan-mic._udp"
        private readonly string _serviceDnsName; // "_audiooverlan-mic._udp.local."

        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;
        private Task? _queryTask;
        private volatile bool _running;

        // Discovered services
        private readonly Dictionary<string, DiscoveredService> _services = new();
        private readonly object _lock = new();

        public event EventHandler<DiscoveredService>? OnServiceDiscovered;
        public event EventHandler<string>? OnServiceLost;

        public MdnsDiscoveryClient(string serviceType = "_audiooverlan-mic._udp")
        {
            _serviceType = serviceType;
            _serviceDnsName = $"{serviceType}.local.";
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

                CoreLogger.Instance.Log($"[mDNS-Discovery] Started looking for '{_serviceDnsName}'");
            }
            catch (Exception ex)
            {
                CoreLogger.Instance.LogError("[mDNS-Discovery] Failed to start", ex);
                _running = false;
                return;
            }

            // Listen for mDNS responses
            _listenerTask = Task.Run(() => ListenLoop(_cts.Token));

            // Periodically send PTR queries
            _queryTask = Task.Run(async () =>
            {
                // Initial burst of queries
                for (int i = 0; i < 3 && _running; i++)
                {
                    SendQuery();
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                }

                // Then periodic queries
                while (_running && !_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(5000, _cts.Token).ConfigureAwait(false);
                        SendQuery();

                        // Check for stale services (no announcement in 30s)
                        CleanupStaleServices();
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            _cts?.Cancel();

            try { _udpClient?.DropMulticastGroup(MdnsMulticastAddress); } catch { }
            try { _udpClient?.Close(); } catch { }
            _udpClient?.Dispose();
            _udpClient = null;

            lock (_lock) _services.Clear();

            CoreLogger.Instance.Log("[mDNS-Discovery] Stopped.");
        }

        /// <summary>
        /// Send an mDNS PTR query for our service type.
        /// </summary>
        private void SendQuery()
        {
            try
            {
                var packet = BuildQueryPacket();
                var endpoint = new IPEndPoint(MdnsMulticastAddress, MdnsPort);
                _udpClient?.Send(packet, packet.Length, endpoint);
            }
            catch (Exception ex)
            {
                if (_running) CoreLogger.Instance.LogError("[mDNS-Discovery] Query error", ex);
            }
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
                    bool isResponse = (flags & 0x8000) != 0;
                    if (!isResponse) continue; // We only care about responses

                    int qdcount = (data[4] << 8) | data[5];
                    int ancount = (data[6] << 8) | data[7];
                    // int nscount = (data[8] << 8) | data[9];
                    int arcount = (data[10] << 8) | data[11];

                    int totalRecords = ancount + arcount;
                    if (totalRecords == 0) continue;

                    // Skip questions
                    int offset = 12;
                    for (int i = 0; i < qdcount && offset < data.Length; i++)
                    {
                        ReadDnsName(data, ref offset);
                        offset += 4; // QTYPE + QCLASS
                    }

                    // Parse answer + additional records
                    string? instanceName = null;
                    string? hostname = null;
                    int servicePort = 0;
                    IPAddress? ipAddress = null;
                    string? displayName = null;
                    int ttl = 0;
                    bool matchesOurService = false;

                    for (int i = 0; i < totalRecords && offset < data.Length; i++)
                    {
                        string rname = ReadDnsName(data, ref offset);
                        if (offset + 10 > data.Length) break;

                        int rtype = (data[offset] << 8) | data[offset + 1];
                        // int rclass = (data[offset + 2] << 8) | data[offset + 3];
                        ttl = (data[offset + 4] << 24) | (data[offset + 5] << 16)
                              | (data[offset + 6] << 8) | data[offset + 7];
                        int rdlength = (data[offset + 8] << 8) | data[offset + 9];
                        offset += 10;

                        int rdataStart = offset;
                        if (offset + rdlength > data.Length) break;

                        if (rtype == 12) // PTR
                        {
                            if (rname.Equals(_serviceDnsName, StringComparison.OrdinalIgnoreCase))
                            {
                                instanceName = ReadDnsName(data, ref offset);
                                matchesOurService = true;

                                if (ttl == 0)
                                {
                                    // Goodbye packet
                                    HandleServiceLost(instanceName);
                                    offset = rdataStart + rdlength;
                                    continue;
                                }
                            }
                        }
                        else if (rtype == 33) // SRV
                        {
                            if (offset + 6 <= data.Length)
                            {
                                // priority(2) + weight(2) + port(2)
                                servicePort = (data[offset + 4] << 8) | data[offset + 5];
                                int srvOffset = offset + 6;
                                hostname = ReadDnsName(data, ref srvOffset);
                            }
                        }
                        else if (rtype == 1) // A record
                        {
                            if (rdlength == 4)
                            {
                                ipAddress = new IPAddress(new[]
                                {
                                    data[offset], data[offset + 1],
                                    data[offset + 2], data[offset + 3]
                                });
                            }
                        }
                        else if (rtype == 16) // TXT
                        {
                            // TXT records are formatted as [length][key=value][length][key2=value2]...
                            int txtOffset = offset;
                            int txtEnd = offset + rdlength;
                            while (txtOffset < txtEnd)
                            {
                                int chunkLen = data[txtOffset++];
                                if (txtOffset + chunkLen > txtEnd) break;
                                string txtLine = Encoding.UTF8.GetString(data, txtOffset, chunkLen);
                                if (txtLine.StartsWith("device_name=", StringComparison.OrdinalIgnoreCase))
                                {
                                    displayName = txtLine.Substring("device_name=".Length);
                                }
                                txtOffset += chunkLen;
                            }
                        }

                        offset = rdataStart + rdlength;
                    }

                    // If we found a matching service with enough info, notify
                    if (matchesOurService && ipAddress != null && servicePort > 0)
                    {
                        HandleServiceDiscovered(instanceName ?? "Unknown", ipAddress.ToString(), servicePort, displayName);
                    }
                    else if (matchesOurService && hostname != null && servicePort > 0)
                    {
                        // We got hostname and port but no A record in this packet
                        // Try to resolve from remoteEp
                        HandleServiceDiscovered(instanceName ?? "Unknown", remoteEp.Address.ToString(), servicePort, displayName);
                    }
                }
                catch (SocketException) { if (!_running) break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private void HandleServiceDiscovered(string instanceName, string ip, int port, string? displayName)
        {
            lock (_lock)
            {
                string key = $"{ip}:{port}";
                bool isNew = !_services.ContainsKey(key);

                var service = new DiscoveredService
                {
                    InstanceName = instanceName,
                    DisplayName = displayName ?? instanceName,
                    IPAddress = ip,
                    Port = port,
                    LastSeen = DateTime.UtcNow
                };
                _services[key] = service;

                if (isNew)
                {
                    CoreLogger.Instance.Log($"[mDNS-Discovery] Found mic service: {service.DisplayName} ({instanceName}) at {ip}:{port}");
                    OnServiceDiscovered?.Invoke(this, service);
                }
                else
                {
                    // Update last seen time and display name if changed
                    _services[key] = service;
                }
            }
        }

        private void HandleServiceLost(string instanceName)
        {
            lock (_lock)
            {
                var toRemove = _services.Where(kv =>
                    kv.Value.InstanceName.Equals(instanceName, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key).ToList();

                foreach (var key in toRemove)
                {
                    var svc = _services[key];
                    _services.Remove(key);
                    CoreLogger.Instance.Log($"[mDNS-Discovery] Service lost: {svc.InstanceName} at {svc.IPAddress}:{svc.Port}");
                    OnServiceLost?.Invoke(this, svc.IPAddress);
                }
            }
        }

        private void CleanupStaleServices()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var stale = _services.Where(kv => (now - kv.Value.LastSeen).TotalSeconds > 30)
                    .Select(kv => kv.Key).ToList();

                foreach (var key in stale)
                {
                    var svc = _services[key];
                    _services.Remove(key);
                    CoreLogger.Instance.Log($"[mDNS-Discovery] Service stale: {svc.InstanceName} at {svc.IPAddress}:{svc.Port}");
                    OnServiceLost?.Invoke(this, svc.IPAddress);
                }
            }
        }

        public IReadOnlyList<DiscoveredService> GetDiscoveredServices()
        {
            lock (_lock)
            {
                return _services.Values.ToList();
            }
        }

        #region DNS Packet Building

        private byte[] BuildQueryPacket()
        {
            var ms = new System.IO.MemoryStream();
            var bw = new System.IO.BinaryWriter(ms);

            // DNS Header
            bw.Write((byte)0); bw.Write((byte)0); // Transaction ID
            bw.Write((byte)0); bw.Write((byte)0); // Flags: standard query
            bw.Write((byte)0); bw.Write((byte)1); // QDCOUNT = 1
            bw.Write((byte)0); bw.Write((byte)0); // ANCOUNT
            bw.Write((byte)0); bw.Write((byte)0); // NSCOUNT
            bw.Write((byte)0); bw.Write((byte)0); // ARCOUNT

            // Question: _audiooverlan-mic._udp.local. PTR IN
            WriteDnsName(bw, _serviceDnsName);
            bw.Write((byte)0); bw.Write((byte)12);  // QTYPE = PTR
            bw.Write((byte)0); bw.Write((byte)1);   // QCLASS = IN

            return ms.ToArray();
        }

        #endregion

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
            ms.WriteByte(0);
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

        #endregion

        public void Dispose()
        {
            Stop();
        }

        public sealed class DiscoveredService
        {
            public string InstanceName { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string IPAddress { get; set; } = "";
            public int Port { get; set; }
            public DateTime LastSeen { get; set; }

            public override string ToString() => $"{InstanceName} ({IPAddress}:{Port})";
        }
    }
}
