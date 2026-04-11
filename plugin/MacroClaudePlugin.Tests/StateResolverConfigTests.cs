using System;
using System.IO;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

public sealed class StateResolverConfigTests : IDisposable
{
    private readonly String _tempDir;

    public StateResolverConfigTests()
    {
        this._tempDir = Path.Combine(
            Path.GetTempPath(),
            "macro-claude-config-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(this._tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private String TempFile(String content)
    {
        var path = Path.Combine(this._tempDir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TryLoadFromFile_Returns_Null_For_Missing_File()
    {
        var config = StateResolverConfig.TryLoadFromFile(
            Path.Combine(this._tempDir, "does-not-exist.json"));

        Assert.Null(config);
    }

    [Fact]
    public void TryLoadFromFile_Parses_All_Four_Fields()
    {
        var path = this.TempFile("""
            {
              "freshHeartbeatSeconds": 5,
              "staleHeartbeatSeconds": 60,
              "cpuActiveThreshold": 2.5,
              "cpuIdleThreshold": 0.25
            }
            """);

        var config = StateResolverConfig.TryLoadFromFile(path);

        Assert.NotNull(config);
        Assert.Equal(TimeSpan.FromSeconds(5), config.FreshHeartbeatWindow);
        Assert.Equal(TimeSpan.FromSeconds(60), config.StaleHeartbeatWindow);
        Assert.Equal(2.5, config.CpuActiveThreshold);
        Assert.Equal(0.25, config.CpuIdleThreshold);
    }

    [Fact]
    public void TryLoadFromFile_Allows_Partial_Config()
    {
        var path = this.TempFile("""
            {
              "cpuActiveThreshold": 5.0
            }
            """);

        var config = StateResolverConfig.TryLoadFromFile(path);

        Assert.NotNull(config);
        Assert.Null(config.FreshHeartbeatWindow);
        Assert.Null(config.StaleHeartbeatWindow);
        Assert.Equal(5.0, config.CpuActiveThreshold);
        Assert.Null(config.CpuIdleThreshold);
    }

    [Fact]
    public void TryLoadFromFile_Returns_Null_On_Invalid_Json()
    {
        var path = this.TempFile("{ not really json");

        var config = StateResolverConfig.TryLoadFromFile(path);

        Assert.Null(config);
    }

    [Fact]
    public void TryLoadFromFile_Ignores_Zero_Or_Negative_Seconds()
    {
        var path = this.TempFile("""
            {
              "freshHeartbeatSeconds": 0,
              "staleHeartbeatSeconds": -5,
              "cpuActiveThreshold": -1.0,
              "cpuIdleThreshold": 0.5
            }
            """);

        var config = StateResolverConfig.TryLoadFromFile(path);

        Assert.NotNull(config);
        Assert.Null(config.FreshHeartbeatWindow);
        Assert.Null(config.StaleHeartbeatWindow);
        Assert.Null(config.CpuActiveThreshold);
        Assert.Equal(0.5, config.CpuIdleThreshold);
    }

    [Fact]
    public void TryLoadFromFile_Ignores_Unknown_Fields()
    {
        var path = this.TempFile("""
            {
              "freshHeartbeatSeconds": 7,
              "someUnknownField": "bananas"
            }
            """);

        var config = StateResolverConfig.TryLoadFromFile(path);

        Assert.NotNull(config);
        Assert.Equal(TimeSpan.FromSeconds(7), config.FreshHeartbeatWindow);
    }
}
