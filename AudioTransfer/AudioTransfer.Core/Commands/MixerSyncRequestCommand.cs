using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class MixerSyncRequestCommand : IControlCommand
{
    public string CommandName => "mixer_sync_request";

    public void Execute(JsonElement root, CommandContext context)
    {
        bool incIcons = root.TryGetProperty("icons", out var iconsProp) && iconsProp.GetBoolean();
        context.VolumeMixer?.BroadcastFullState(incIcons);
    }
}
