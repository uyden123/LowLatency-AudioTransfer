using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AudioTransfer.Core.Network
{
    public static class UPnPPortMapper
    {
        private const string SearchMessage =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "ST: upnp:rootdevice\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 3\r\n\r\n";

        public static async Task<bool> MapPort(int port, string description)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 3000;
                byte[] data = Encoding.UTF8.GetBytes(SearchMessage);
                var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

                await udp.SendAsync(data, data.Length, endpoint);

                DateTime start = DateTime.Now;
                while ((DateTime.Now - start).TotalSeconds < 3)
                {
                    if (udp.Available > 0)
                    {
                        var result = await udp.ReceiveAsync();
                        string response = Encoding.UTF8.GetString(result.Buffer);

                        if (response.Contains("LOCATION: "))
                        {
                            string location = GetLocation(response);
                            if (!string.IsNullOrEmpty(location))
                            {
                                return await SoapRequest(location, port, description);
                            }
                        }
                    }
                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[UPnP] Error: {ex.Message}");
            }
            return false;
        }

        private static string GetLocation(string response)
        {
            var lines = response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("LOCATION: ", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(10).Trim();
            }
            return "";
        }

        private static async Task<bool> SoapRequest(string url, int port, string description)
        {
            try
            {
                string localIp = GetLocalIPAddress();
                string soapBody =
                    $"<?xml version=\"1.0\"?>" +
                    $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                    $"<s:Body>" +
                    $"<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                    $"<NewRemoteHost></NewRemoteHost>" +
                    $"<NewExternalPort>{port}</NewExternalPort>" +
                    $"<NewProtocol>TCP</NewProtocol>" +
                    $"<NewInternalPort>{port}</NewInternalPort>" +
                    $"<NewInternalClient>{localIp}</NewInternalClient>" +
                    $"<NewEnabled>1</NewEnabled>" +
                    $"<NewPortMappingDescription>{description}</NewPortMappingDescription>" +
                    $"<NewLeaseDuration>0</NewLeaseDuration>" +
                    $"</u:AddPortMapping>" +
                    $"</s:Body>" +
                    $"</s:Envelope>";

                using var client = new WebClient();
                client.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"");
                client.Headers.Add("Content-Type", "text/xml; charset=\"utf-8\"");

                // Note: Actual UPnP service URL might differ, we try standard path first
                // A production version would parse the XML at 'url' to find the actual controlURL
                string controlUrl = url.Substring(0, url.IndexOf("/", 8)) + "/ipc";

                await client.UploadStringTaskAsync(new Uri(url), soapBody);
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[UPnP] Port {port} mapped successfully on Router.");
                return true;
            }
            catch { return false; }
        }

        private static string GetLocalIPAddress()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
    }
}
