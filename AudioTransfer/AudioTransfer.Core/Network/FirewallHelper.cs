using System;
using System.Diagnostics;
using System.Security.Principal;

namespace AudioTransfer.Core.Network
{
    /// <summary>
    /// Windows Firewall rule management via netsh.
    /// </summary>
    public static class FirewallHelper
    {
        public static void AllowPort(int port, string ruleName, string protocol = "TCP")
        {
            if (!IsAdministrator())
            {
                Logging.CoreLogger.Instance.Log("[Firewall] Skip: not running as Administrator.", Logging.LogLevel.Warning);
                return;
            }

            try
            {
                // Delete existing rule first to avoid duplicates
                RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\" localport={port} protocol={protocol}");

                // Add new rule
                string cmd = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol={protocol} localport={port} profile=any description=\"Allow AudioOverLAN Streaming\"";
                if (RunNetsh(cmd))
                {
                    Logging.CoreLogger.Instance.Log($"[Firewall] Inbound rule '{ruleName}' added for {protocol} port {port}.");
                }
            }
            catch (Exception ex)
            {
                Logging.CoreLogger.Instance.LogError("[Firewall] Error", ex);
            }
        }

        public static void AllowUdpPort(int port, string ruleName)
        {
            AllowPort(port, ruleName, "UDP");
        }

        private static bool RunNetsh(string args)
        {
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = "netsh.exe";
                process.StartInfo.Arguments = args;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch { return false; }
        }

        public static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
