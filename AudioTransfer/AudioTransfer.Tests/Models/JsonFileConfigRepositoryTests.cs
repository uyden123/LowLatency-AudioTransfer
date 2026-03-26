using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AudioTransfer.Core.Models;
using Xunit;

namespace AudioTransfer.Tests.Models;

public class JsonFileConfigRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileConfigRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigRepoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private JsonFileConfigRepository<PlayerConfig> CreateRepo(string fileName = "player_config.json")
    {
        return new JsonFileConfigRepository<PlayerConfig>(Path.Combine(_tempDir, fileName));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_PlayerConfig()
    {
        var repo = CreateRepo();
        var config = new PlayerConfig
        {
            LastConnectedIp = "10.0.0.5",
            LastConnectedPort = 9999,
            IsDarkTheme = false,
            Language = "Vietnamese"
        };

        await repo.SaveAsync(config);
        var loaded = await repo.LoadOrDefaultAsync();

        Assert.Equal("10.0.0.5", loaded.LastConnectedIp);
        Assert.Equal(9999, loaded.LastConnectedPort);
        Assert.False(loaded.IsDarkTheme);
        Assert.Equal("Vietnamese", loaded.Language);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_ServerConfig()
    {
        var repo = new JsonFileConfigRepository<ServerConfig>(
            Path.Combine(_tempDir, "server_config.json"));

        var config = new ServerConfig();
        config.Network.Port = 7777;
        config.Audio.SampleRate = 44100;

        await repo.SaveAsync(config);
        var loaded = await repo.LoadOrDefaultAsync();

        Assert.Equal(7777, loaded.Network.Port);
        Assert.Equal(44100, loaded.Audio.SampleRate);
    }

    [Fact]
    public async Task Load_FileNotExists_ReturnsDefault()
    {
        var repo = CreateRepo("nonexistent.json");
        var loaded = await repo.LoadOrDefaultAsync();

        Assert.NotNull(loaded);
        Assert.Equal("", loaded.LastConnectedIp);
        Assert.Equal(5003, loaded.LastConnectedPort);
    }

    [Fact]
    public async Task Load_CorruptJson_ReturnsDefault()
    {
        var filePath = Path.Combine(_tempDir, "corrupt.json");
        await File.WriteAllTextAsync(filePath, "NOT VALID JSON {{{");

        var repo = new JsonFileConfigRepository<PlayerConfig>(filePath);
        var loaded = await repo.LoadOrDefaultAsync();

        Assert.NotNull(loaded);
        Assert.Equal("", loaded.LastConnectedIp); // default value
    }

    [Fact]
    public async Task Save_CreatesFile()
    {
        var filePath = Path.Combine(_tempDir, "new_config.json");
        var repo = new JsonFileConfigRepository<PlayerConfig>(filePath);

        await repo.SaveAsync(new PlayerConfig());

        Assert.True(File.Exists(filePath));
    }
}
