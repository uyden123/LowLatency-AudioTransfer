using System.Text.Json;
using System.Threading.Tasks;
using AudioTransfer.Core.Codec;
using AudioTransfer.Core.Commands;
using Xunit;

namespace AudioTransfer.Tests.Commands;

public class SetBitrateCommandTests
{
    private readonly SetBitrateCommand _command = new();

    [Fact]
    public void Execute_SetsBitrateOnEncoder()
    {
        var encoder = new OpusEncoderWrapper(2, 64000, 20); // 2 channels, 64k, 20ms
        var context = CreateContext(encoder);
        string json = """{"command": "set_bitrate", "value": 128000}""";
        var doc = JsonDocument.Parse(json);

        _command.Execute(doc.RootElement, context);

        Assert.Equal(128000, encoder.Bitrate);
        encoder.Dispose();
    }

    [Fact]
    public void Execute_NullEncoder_NoException()
    {
        var context = CreateContext(null);
        string json = """{"command": "set_bitrate", "value": 128000}""";
        var doc = JsonDocument.Parse(json);

        var exception = Record.Exception(() => _command.Execute(doc.RootElement, context));

        Assert.Null(exception);
    }

    [Fact]
    public void CommandName_ReturnsCorrectName()
    {
        Assert.Equal("set_bitrate", _command.CommandName);
    }

    private static CommandContext CreateContext(OpusEncoderWrapper? encoder)
    {
        return new CommandContext
        {
            OpusEncoder = encoder,
            SetSystemMute = _ => { },
            TogglePlugin = (_, _) => { },
            EqPlugin = null,
            VolumeMixer = null,
            SendControlMessageAsync = _ => Task.FromResult(true),
            Log = _ => { }
        };
    }
}
