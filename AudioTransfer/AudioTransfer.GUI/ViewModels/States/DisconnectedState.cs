using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.GUI.ViewModels.States;

public class DisconnectedState : IPlayerState
{
    private const string ipPattern = @"^((25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$";
    
    public async Task HandleConnectToggleAsync(MainViewModel context)
    {
        string targetIp = context.TargetIp?.Trim() ?? "";
        CoreLogger.Instance.Log($"[DisconnectedState] HandleConnectToggleAsync called with IP: '{targetIp}'");

        if (string.IsNullOrEmpty(targetIp))
        {
            context.NotifyUser("Please enter an Android IP address or use discovery.", "Input Required");
            return;
        }

        if (!Regex.IsMatch(targetIp, ipPattern))
        {
            CoreLogger.Instance.Log($"[DisconnectedState] Invalid IP format: {targetIp}");
            context.NotifyUser("Invalid IP format. Example: 192.168.1.100", "Input Error");
            return;
        }

        // Transition to connecting state
        CoreLogger.Instance.Log($"[DisconnectedState] Transitioning to ConnectingState for {targetIp}");
        context.ChangeState(new ConnectingState(targetIp, 5003));
        
        // Give UI thread a chance to update before heavy work starts
        await Task.Yield();

        await context.CurrentState.HandleConnectToggleAsync(context);
    }

    public void UpdateUi(MainViewModel context)
    {
        CoreLogger.Instance.Log("[DisconnectedState] UpdateUi called");
        context.IsPlayerConnecting = false;
        context.IsPlayerRunning = false;
        context.PlayerStatusText = "CONNECT";
        context.IsStatsVisible = false;
    }
}
