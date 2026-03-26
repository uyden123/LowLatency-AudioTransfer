using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class SetSessionVolCommand : IControlCommand
{
    public string CommandName => "set_session_vol";

    public void Execute(JsonElement root, CommandContext context)
    {
        if (root.TryGetProperty("pid", out var pidProp) && root.TryGetProperty("vol", out var volProp))
        {
            uint pid = pidProp.GetUInt32();
            float vol = (float)volProp.GetDouble();
            context.VolumeMixer?.SetVolume(pid, vol);
            context.Log($"[VolumeMixer] Set PID {pid} volume to {vol:P0}");
        }
    }
}
