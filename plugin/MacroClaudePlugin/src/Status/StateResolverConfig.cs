using System;
using System.IO;
using System.Text.Json;

namespace Loupedeck.MacroClaudePlugin.Status;

// Optional per-machine threshold overrides for StateResolver, loaded
// from ~/.claude/macro-claude.json. Any missing field falls back to the
// constant defaults defined on StateResolver itself.
//
// Example config file:
//
//   {
//     "freshHeartbeatSeconds": 3,
//     "staleHeartbeatSeconds": 30,
//     "cpuActiveThreshold": 1.0,
//     "cpuIdleThreshold": 0.5
//   }
//
// The file is loaded once in StatusReader's constructor. Changes at
// runtime require a plugin reload. A missing / malformed / unreadable
// file is silently ignored — the resolver uses its built-in defaults.
public sealed record StateResolverConfig
{
    public TimeSpan? FreshHeartbeatWindow { get; init; }

    public TimeSpan? StaleHeartbeatWindow { get; init; }

    public Double? CpuActiveThreshold { get; init; }

    public Double? CpuIdleThreshold { get; init; }

    public static StateResolverConfig? TryLoadFromFile(String path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;

            return new StateResolverConfig
            {
                FreshHeartbeatWindow = TryGetSeconds(root, "freshHeartbeatSeconds"),
                StaleHeartbeatWindow = TryGetSeconds(root, "staleHeartbeatSeconds"),
                CpuActiveThreshold = TryGetDouble(root, "cpuActiveThreshold"),
                CpuIdleThreshold = TryGetDouble(root, "cpuIdleThreshold"),
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TimeSpan? TryGetSeconds(JsonElement root, String name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        var seconds = el.GetDouble();
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static Double? TryGetDouble(JsonElement root, String name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        var value = el.GetDouble();
        return value >= 0 ? value : null;
    }
}
