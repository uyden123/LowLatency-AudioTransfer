using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class SetEqCommand : IControlCommand
{
    public string CommandName => "set_eq";

    public void Execute(JsonElement root, CommandContext context)
    {
        if (root.TryGetProperty("value", out var eqProp))
        {
            string preset = eqProp.GetString() ?? "None";
            context.EqPlugin?.SetPreset(preset);
            context.Log($"EQ Preset set to: {preset}");
        }
    }
}
