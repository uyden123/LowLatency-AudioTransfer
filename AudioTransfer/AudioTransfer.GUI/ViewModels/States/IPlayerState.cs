using System.Threading.Tasks;

namespace AudioTransfer.GUI.ViewModels.States;

public interface IPlayerState
{
    Task HandleConnectToggleAsync(MainViewModel context);
    void UpdateUi(MainViewModel context);
}
