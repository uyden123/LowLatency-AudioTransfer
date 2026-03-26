using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AudioTransfer.Core.Commands;

public class CommandRegistry
{
    private readonly Dictionary<string, IControlCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public CommandRegistry()
    {
        Register(new SetBitrateCommand());
        Register(new SetMuteCommand());
        Register(new SetDrcCommand());
        Register(new SetEqCommand());
        Register(new SetSessionVolCommand());
        Register(new MixerSyncRequestCommand());
        Register(new MixerIconRequestCommand());
        Register(new StopCommand());
    }

    public void Register(IControlCommand command)
    {
        _commands[command.CommandName] = command;
    }

    public void Execute(string json, CommandContext context)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmdProp)) return;
            
            string cmd = cmdProp.GetString() ?? "";
            context.Log($"[UDP-Control] Command: {cmd}");

            if (_commands.TryGetValue(cmd, out var handler))
            {
                handler.Execute(root, context);
            }
            else
            {
                context.Log($"[UDP-Control] Unknown command: {cmd}");
            }
        }
        catch (Exception ex)
        {
            context.Log($"[UDP-Control] Parse error: {ex.Message}");
        }
    }
}
