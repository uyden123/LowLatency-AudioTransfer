using System;
using System.Linq;
using System.Threading.Tasks;
using AudioTransfer.Core.Logging;

namespace AudioTransfer.GUI.ViewModels.States;

public class ConnectingState : IPlayerState
{
    private readonly string _targetIp;
    private readonly int _port;

    public ConnectingState(string targetIp, int port)
    {
        _targetIp = targetIp;
        _port = port;
    }

    public async Task HandleConnectToggleAsync(MainViewModel context)
    {
        CoreLogger.Instance.Log($"[ConnectingState] Starting connection to {_targetIp}:{_port}");
        
        try
        {
            // Move heavy initialization to background thread to avoid blocking UI
            bool success = await Task.Run(() => 
                context.PlayerEngine.StartAndroidMicListenerAsync(_targetIp, _port, context.SelectedOutputDevice?.Id));
            
            CoreLogger.Instance.Log($"[ConnectingState] StartAndroidMicListenerAsync result: {success}");
            
            if (!success)
            {
                CoreLogger.Instance.Log("[ConnectingState] Failed to start listener. Notifying user.");
                context.NotifyUser($"Failed to start listener at {_targetIp}.", "Error");
                return;
            }

            // 2. Wait for Handshake (replaces VerifyDeviceAsync)
            CoreLogger.Instance.Log($"[ConnectingState] Waiting for handshake with {_targetIp}...");
            
            // Handshake timeout: 5 seconds for reliability
            bool isAlive = await context.PlayerEngine.MicReceiver!.WaitHandshakeAsync(5000);
            
            CoreLogger.Instance.Log($"[ConnectingState] Handshake result: {isAlive}");
            
            if (!isAlive)
            {
                CoreLogger.Instance.Log($"[ConnectingState] Handshake with {_targetIp} timed out. Stopping engine.");
                context.NotifyUser($"Handshake timed out with {_targetIp}.\nCheck IP and Android app.", "Connection Failed");
                
                await Task.Run(() => context.PlayerEngine.Stop());
                context.ChangeState(new DisconnectedState());
                return;
            }

            // Success -> Connected
            CoreLogger.Instance.Log($"[ConnectingState] Success! Transitioning to ConnectedState for {_targetIp}");
            context.ChangeState(new ConnectedState(_targetIp, _port));
            await context.CurrentState.HandleConnectToggleAsync(context);
        }
        catch (Exception ex)
        {
            CoreLogger.Instance.Log($"[ConnectingState] Exception during connection: {ex.Message}\n{ex.StackTrace}");
            context.NotifyUser($"Connection error: {ex.Message}", "Error");
            await Task.Run(() => context.PlayerEngine.Stop());
            context.ChangeState(new DisconnectedState());
        }
    }

    public void UpdateUi(MainViewModel context)
    {
        CoreLogger.Instance.Log("[ConnectingState] UpdateUi called");
        context.IsPlayerConnecting = true;
        context.PlayerStatusText = "CONNECTING...";
    }
}
