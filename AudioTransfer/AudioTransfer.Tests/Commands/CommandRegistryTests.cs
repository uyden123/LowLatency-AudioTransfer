using System;
using System.Text.Json;
using System.Threading.Tasks;
using AudioTransfer.Core.Commands;
using NSubstitute;
using Xunit;

namespace AudioTransfer.Tests.Commands;

public class CommandRegistryTests
{
    private readonly CommandRegistry _registry;

    public CommandRegistryTests()
    {
        _registry = new CommandRegistry();
    }

    [Fact]
    public void RegistryExecutesRegisteredCommand()
    {
        var mockCommand = Substitute.For<IControlCommand>();
        mockCommand.CommandName.Returns("test_cmd");
        _registry.Register(mockCommand);

        var context = CreateContext();
        string json = """{"command": "test_cmd"}""";

        _registry.Execute(json, context);

        mockCommand.Received(1).Execute(Arg.Any<JsonElement>(), context);
    }

    [Fact]
    public void RegistryIgnoresUnknownCommand()
    {
        var context = CreateContext();
        string json = """{"command": "unknown_cmd"}""";

        var exception = Record.Exception(() => _registry.Execute(json, context));

        Assert.Null(exception); // Should just log, not crash
    }

    [Fact]
    public void RegistryIgnoresMalformedJson()
    {
        var context = CreateContext();
        string json = "not json";

        var exception = Record.Exception(() => _registry.Execute(json, context));

        Assert.Null(exception); // Should log parse error, not crash
    }

    [Fact]
    public void RegistryIsCaseInsensitive()
    {
        var mockCommand = Substitute.For<IControlCommand>();
        mockCommand.CommandName.Returns("test_cmd");
        _registry.Register(mockCommand);

        var context = CreateContext();
        string json = """{"command": "TEST_CMD"}""";

        _registry.Execute(json, context);

        mockCommand.Received(1).Execute(Arg.Any<JsonElement>(), context);
    }

    private static CommandContext CreateContext()
    {
        return new CommandContext
        {
            OpusEncoder = null,
            SetSystemMute = _ => { },
            TogglePlugin = (_, _) => { },
            EqPlugin = null,
            VolumeMixer = null,
            SendControlMessageAsync = _ => Task.FromResult(true),
            Log = _ => { }
        };
    }
}
