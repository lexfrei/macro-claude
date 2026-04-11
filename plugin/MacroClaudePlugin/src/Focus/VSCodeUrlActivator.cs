using System;
using System.Diagnostics;

namespace Loupedeck.MacroClaudePlugin.Focus;

// Raise a specific VS Code window by opening its workspace directory
// through the `vscode://` URL scheme. macOS LaunchServices handles the
// URL, which means:
//
//   1. If the workspace is already open in some window, VS Code
//      activates that exact window — including windows that live in
//      a different fullscreen Space. AX / System Events cannot see
//      fullscreen windows on other Spaces, so AppleScript AXRaise
//      silently fails; `open` is the only reliable path.
//   2. LaunchServices transparently switches the user to the Space
//      that owns the target window. No manual Mission Control dance.
//   3. If the workspace is not open, VS Code opens a new window for
//      it. That is still a reasonable outcome — the user landed
//      somewhere useful.
//
// We shell out to `/usr/bin/open -g` rather than call
// LSOpenCFURLRef via P/Invoke because:
//   * `open` is a stable macOS system utility, not a removable
//     dependency like osascript.
//   * LaunchServices from P/Invoke requires CoreServices.framework
//     loading plus CFURL bridging, roughly 80 lines of boilerplate.
//   * The cost of one process spawn per button press (~20 ms) is
//     imperceptible.
internal static class VSCodeUrlActivator
{
    private const String OpenPath = "/usr/bin/open";

    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(2);

    public static Boolean OpenWorkspace(String cwd)
    {
        if (String.IsNullOrEmpty(cwd))
        {
            return false;
        }

        // Only absolute paths can be handed to vscode://file — the
        // URL handler / LaunchServices reject anything else.
        if (!cwd.StartsWith('/'))
        {
            PluginLog.Warning($"macro-claude: VSCodeUrlActivator called with non-absolute cwd '{cwd}'");
            return false;
        }

        // Percent-encode each path segment before building the URL.
        // Raw paths containing reserved URL characters (`#`, `?`, `%`
        // and friends) are otherwise misparsed by LaunchServices —
        // `#` becomes a fragment marker, `?` a query delimiter, etc.
        // For a repository checked out at /Users/lex/code/bug#42 the
        // unencoded URL would lose everything after `#` and VS Code
        // would open /Users/lex/code/bug instead.
        //
        // Uri.EscapeDataString encodes each segment per RFC 3986
        // unreserved-char rules, which matches what `open` and VS
        // Code's URI parser expect. We keep the leading slash and
        // rejoin with literal slashes to preserve the path structure.
        var url = $"vscode://file{VSCodeUrlEncoder.EncodePath(cwd)}";
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OpenPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            // No -g: we DO want VS Code to come to the foreground —
            // that is the entire purpose of the button press. `open`
            // itself runs headless regardless; the flag only affects
            // the target app's activation.
            // --: end of options, URL follows.
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(url);

            if (!process.Start())
            {
                PluginLog.Warning("macro-claude: /usr/bin/open failed to start");
                return false;
            }

            var finished = process.WaitForExit((Int32)ExecutionTimeout.TotalMilliseconds);
            if (!finished)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between WaitForExit and Kill.
                }

                PluginLog.Warning("macro-claude: /usr/bin/open timed out");
                return false;
            }

            if (process.ExitCode == 0)
            {
                PluginLog.Info($"macro-claude: opened {url}");
                return true;
            }

            var stderr = process.StandardError.ReadToEnd();
            PluginLog.Warning(
                $"macro-claude: /usr/bin/open exited {process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {stderr.Trim()}");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            PluginLog.Error(ex, "macro-claude: /usr/bin/open spawn failed");
            return false;
        }
        catch (System.IO.IOException ex)
        {
            PluginLog.Error(ex, "macro-claude: /usr/bin/open IO error");
            return false;
        }
    }
}
