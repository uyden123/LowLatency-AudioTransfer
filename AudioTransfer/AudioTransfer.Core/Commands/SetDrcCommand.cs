using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class SetDrcCommand : IControlCommand
{
    public string CommandName => "set_drc";

    public void Execute(JsonElement root, CommandContext context)
    {
        if (root.TryGetProperty("value", out var drcProp))
        {
            bool enabled = drcProp.GetBoolean();
            context.TogglePlugin("DRC", enabled);
        }
    }
}
