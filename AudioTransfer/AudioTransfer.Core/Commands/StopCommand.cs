using System;
using System.Text.Json;

namespace AudioTransfer.Core.Commands
{
    /// <summary>
    /// Unified command for stopping a connection/stream.
    /// Used by both server (broadcast) and client (request disconnect).
    /// </summary>
    public class StopCommand : IControlCommand
    {
        public string CommandName => "stop";

        public void Execute(JsonElement root, CommandContext context)
        {
            context.Log("[UDP-Control] Stop command received. Disconnecting client.");
            context.DisconnectClient();
        }
    }
}
