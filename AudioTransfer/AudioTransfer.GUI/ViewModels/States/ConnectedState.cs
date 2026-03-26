using System;
using System.Linq;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio.Mixer;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.GUI.ViewModels.States;

public class ConnectedState : IPlayerState
{
    private readonly string _targetIp;
    private readonly int _port;

    public ConnectedState(string targetIp, int port)
    {
        _targetIp = targetIp;
        _port = port;
    }

    public async Task HandleConnectToggleAsync(MainViewModel context)
    {
        // If toggle is called while connected, it means "Stop" or "Disconnect"
        // But first, do initial setup if just transitioned here
        CoreLogger.Instance.Log($"[ConnectedState] HandleConnectToggleAsync called. IsPlayerRunning: {context.IsPlayerRunning}");

        if (!context.IsPlayerRunning)
        {
            if (context.AutoMute)
            {
                CoreLogger.Instance.Log("[ConnectedState] Auto-muting system audio");
                context.PlayerEngine.SetSystemMute(true);
            }

            CoreLogger.Instance.Log($"[ConnectedState] Setting active device IP to {_targetIp}");
            context.ActiveDeviceIp = _targetIp;
            var discovery = context.DiscoveredServers.FirstOrDefault(x => x.IpAddress == _targetIp);
            context.ActiveDeviceName = discovery?.Name ?? "Android Device";

            context.Config.LastConnectedIp = _targetIp;
            context.Config.LastConnectedPort = _port;
            await context.SaveConfigAsync();

            context.IsPlayerRunning = true; // Mark as running so next click stops
            context.TransitionToStatsView();
            UpdateUi(context);
        }
        else
        {
            // Stop action
            CoreLogger.Instance.Log($"[ConnectedState] Requested to STOP connection to {_targetIp}. Transitioning to StoppingState.");
            context.ChangeState(new StoppingState());
            await context.CurrentState.HandleConnectToggleAsync(context);
        }
    }

    public void UpdateUi(MainViewModel context)
    {
        context.IsPlayerConnecting = false;
        context.PlayerStatusText = "STOP";
        context.IsStatsVisible = true;
        CoreLogger.Instance.Log($"[ConnectedState] UpdateUi called. IsPlayerRunning : {context.IsPlayerRunning}");
    }
}
