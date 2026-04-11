using System;

namespace Loupedeck.MacroClaudePlugin;

// Thin static adapter over the Loupedeck PluginLogFile so plugin code
// can log without threading the PluginLogFile reference through every
// class. Initialized exactly once from the Plugin constructor.
internal static class PluginLog
{
    private static PluginLogFile? _pluginLogFile;

    public static void Init(PluginLogFile pluginLogFile)
    {
        pluginLogFile.CheckNullArgument(nameof(pluginLogFile));
        _pluginLogFile = pluginLogFile;
    }

    public static void Verbose(String text) => _pluginLogFile?.Verbose(text);

    public static void Verbose(Exception ex, String text) => _pluginLogFile?.Verbose(ex, text);

    public static void Info(String text) => _pluginLogFile?.Info(text);

    public static void Info(Exception ex, String text) => _pluginLogFile?.Info(ex, text);

    public static void Warning(String text) => _pluginLogFile?.Warning(text);

    public static void Warning(Exception ex, String text) => _pluginLogFile?.Warning(ex, text);

    public static void Error(String text) => _pluginLogFile?.Error(text);

    public static void Error(Exception ex, String text) => _pluginLogFile?.Error(ex, text);
}
