using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class MixerIconRequestCommand : IControlCommand
{
    public string CommandName => "mixer_icon_request";

    public void Execute(JsonElement root, CommandContext context)
    {
        if (root.TryGetProperty("pid", out var pidIconProp))
        {
            uint pid = pidIconProp.GetUInt32();
            string? icon = context.VolumeMixer?.GetSessionIcon(pid);
            if (!string.IsNullOrEmpty(icon))
            {
                context.Log($"[VolumeMixer] Sending icon for PID {pid} ({icon.Length} bytes)");
                string resp = JsonSerializer.Serialize(new { command = "mixer_icon_res", pid, icon });
                _ = context.SendControlMessageAsync(resp);
            }
            else
            {
                context.Log($"[VolumeMixer] Icon requested for PID {pid} but not found or empty.");
            }
        }
    }
}
