using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class SetMuteCommand : IControlCommand
{
    public string CommandName => "set_mute";

    public void Execute(JsonElement root, CommandContext context)
    {
        if (root.TryGetProperty("value", out var muteProp))
        {
            bool muted = muteProp.GetBoolean();
            context.IsMuted = muted;
            context.SetSystemMute(muted);
        }
    }
}
