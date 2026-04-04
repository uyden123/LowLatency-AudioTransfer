using System;
using System.Threading.Tasks;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.GUI.ViewModels.States;

public class StoppingState : IPlayerState
{
    public async Task HandleConnectToggleAsync(MainViewModel context)
    {
        CoreLogger.Instance.Log("[StoppingState] HandleConnectToggleAsync called");
        
        // Trigger UI animation back to connection view immediately
        context.IsStatsVisible = false;
        context.TransitionToConnectView();

        if (context.AutoConnect)
        {
            CoreLogger.Instance.Log("[StoppingState] Suppressing auto-connect for 5 minutes");
            context.SuppressAutoConnect(TimeSpan.FromMinutes(5));
            context.NotifyUser("Auto-connect suppressed for 5 minutes.", "Auto Connect");
        }

        CoreLogger.Instance.Log("[StoppingState] Calling PlayerEngine.Stop()");
        await Task.Run(() => context.PlayerEngine.Stop());
        
        CoreLogger.Instance.Log("[StoppingState] Transitioning to DisconnectedState");
        context.ChangeState(new DisconnectedState());
    }

    public void UpdateUi(MainViewModel context)
    {
        CoreLogger.Instance.Log("[StoppingState] UpdateUi called");
        context.IsPlayerConnecting = true; // Disables buttons
        context.PlayerStatusText = "STOPPING...";
    }
}
