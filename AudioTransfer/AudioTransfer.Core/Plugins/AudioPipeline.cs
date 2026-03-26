using System;
using System.Collections.Generic;
using System.Linq;

namespace AudioTransfer.Core.Plugins;

public class AudioPipeline
{
    private readonly List<IAudioPlugin> _stages = new();
    public IReadOnlyList<IAudioPlugin> Stages => _stages;

    public AudioPipeline Add(IAudioPlugin plugin)
    {
        _stages.Add(plugin);
        return this;
    }

    public AudioPipeline Remove(string name)
    {
        _stages.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    public void Process(short[] buffer, int length, int sampleRate, int channels)
    {
        foreach (var plugin in _stages)
        {
            if (plugin.IsEnabled)
            {
                plugin.Process(buffer, length, sampleRate, channels);
            }
        }
    }

    public void TogglePlugin(string name, bool enabled)
    {
        var plugin = _stages.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (plugin != null)
        {
            plugin.IsEnabled = enabled;
        }
    }
}
