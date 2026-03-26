using System.Text.Json;
using System.Threading.Tasks;
using AudioTransfer.Core.Commands;
using Xunit;

namespace AudioTransfer.Tests.Commands;

public class SetMuteCommandTests
{
    private readonly SetMuteCommand _command = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Execute_CallsSetSystemMuteWithCorrectValue(bool expectedMute)
    {
        bool actualMute = !expectedMute; // Default to opposite
        var context = new CommandContext
        {
            OpusEncoder = null,
            SetSystemMute = m => actualMute = m,
            TogglePlugin = (_, _) => { },
            EqPlugin = null,
            VolumeMixer = null,
            SendControlMessageAsync = _ => Task.FromResult(true),
            Log = _ => { }
        };

        string json = $$"""{"command": "set_mute", "value": {{expectedMute.ToString().ToLower()}}}""";
        var doc = JsonDocument.Parse(json);

        _command.Execute(doc.RootElement, context);

        Assert.Equal(expectedMute, actualMute);
        Assert.Equal(expectedMute, context.IsMuted);
    }
}
