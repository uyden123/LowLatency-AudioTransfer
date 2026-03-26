using System;
using System.Text.Json;
using System.Threading.Tasks;
using AudioTransfer.Core.Audio.Mixer;
using AudioTransfer.Core.Codec;
using AudioTransfer.Core.Plugins;

namespace AudioTransfer.Core.Commands;

public interface IControlCommand
{
    string CommandName { get; }
    void Execute(JsonElement root, CommandContext context);
}

public class CommandContext
{
    public required OpusEncoderWrapper? OpusEncoder { get; init; }
    public required Action<bool> SetSystemMute { get; init; }
    public required Action<string, bool> TogglePlugin { get; init; }
    public required EqPlugin? EqPlugin { get; init; }
    public required VolumeMixerManager? VolumeMixer { get; init; }
    public required Func<string, Task<bool>> SendControlMessageAsync { get; init; }
    public required Action<string> Log { get; init; }
    public required Action DisconnectClient { get; init; }
    public bool IsMuted { get; set; }
}
