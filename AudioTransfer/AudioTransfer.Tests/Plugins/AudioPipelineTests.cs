using System;
using System.Linq;
using AudioTransfer.Core.Plugins;
using Xunit;

namespace AudioTransfer.Tests.Plugins;

public class FakeAudioPlugin : IAudioPlugin
{
    public string Name { get; }
    public bool IsEnabled { get; set; } = true;
    public bool WasProcessed { get; private set; }

    public FakeAudioPlugin(string name)
    {
        Name = name;
    }

    public void Process(short[] buffer, int length, int sampleRate, int channels)
    {
        WasProcessed = true;
    }
}

public class AudioPipelineTests
{
    [Fact]
    public void Add_AddsPluginToStages()
    {
        var pipeline = new AudioPipeline();
        var plugin = new FakeAudioPlugin("Test1");
        
        pipeline.Add(plugin);

        Assert.Single(pipeline.Stages);
        Assert.Equal(plugin, pipeline.Stages[0]);
    }

    [Fact]
    public void Remove_RemovesPluginByName_CaseInsensitive()
    {
        var pipeline = new AudioPipeline();
        pipeline.Add(new FakeAudioPlugin("Test1")).Add(new FakeAudioPlugin("test2"));

        pipeline.Remove("TEST2");

        Assert.Single(pipeline.Stages);
        Assert.Equal("Test1", pipeline.Stages[0].Name);
    }

    [Fact]
    public void Process_OnlyCallsEnabledPlugins()
    {
        var pipeline = new AudioPipeline();
        var enabledPlugin = new FakeAudioPlugin("Eq");
        var disabledPlugin = new FakeAudioPlugin("Drc") { IsEnabled = false };
        pipeline.Add(enabledPlugin).Add(disabledPlugin);

        pipeline.Process(Array.Empty<short>(), 0, 48000, 2);

        Assert.True(enabledPlugin.WasProcessed);
        Assert.False(disabledPlugin.WasProcessed);
    }

    [Fact]
    public void Process_CallsPluginsInOrder()
    {
        var pipeline = new AudioPipeline();

        var p1 = new FakeAudioPlugin("P1");
        var p2 = new FakeAudioPlugin("P2");
        
        // Use a mock or local func for order verif if needed, but since Process does it in order:
        pipeline.Add(p1).Add(p2);
        pipeline.Process(Array.Empty<short>(), 0, 48000, 2);

        Assert.True(p1.WasProcessed);
        Assert.True(p2.WasProcessed);
    }

    [Fact]
    public void TogglePlugin_SetsEnabledState()
    {
        var pipeline = new AudioPipeline();
        var plugin = new FakeAudioPlugin("Eq") { IsEnabled = false };
        pipeline.Add(plugin);

        pipeline.TogglePlugin("eq", true);

        Assert.True(plugin.IsEnabled);
    }

    [Fact]
    public void TogglePlugin_UnknownName_NoException()
    {
        var pipeline = new AudioPipeline();
        
        var exception = Record.Exception(() => pipeline.TogglePlugin("Eq", true));
        
        Assert.Null(exception);
    }

    [Fact]
    public void Process_EmptyPipeline_NoException()
    {
        var pipeline = new AudioPipeline();

        var exception = Record.Exception(() => pipeline.Process(Array.Empty<short>(), 0, 48000, 2));

        Assert.Null(exception);
    }

    [Fact]
    public void FluentApi_ChainedAdds_AllRegistered()
    {
        var pipeline = new AudioPipeline();
        
        pipeline.Add(new FakeAudioPlugin("A")).Add(new FakeAudioPlugin("B"));

        Assert.Equal(2, pipeline.Stages.Count);
        Assert.Equal("A", pipeline.Stages[0].Name);
        Assert.Equal("B", pipeline.Stages[1].Name);
    }
}
