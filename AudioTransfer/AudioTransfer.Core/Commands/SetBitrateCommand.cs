using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class SetBitrateCommand : IControlCommand
{
    public string CommandName => "set_bitrate";

    public void Execute(JsonElement root, CommandContext context)
    {
        if (context.OpusEncoder != null && root.TryGetProperty("value", out var valProp))
        {
            context.OpusEncoder.Bitrate = valProp.GetInt32();
        }
    }
}
