using System;
using System.Diagnostics;

namespace Loupedeck.MacroClaudePlugin.Focus;

// Raise a specific VS Code window to the foreground via AppleScript
// + System Events + Accessibility API. This is the only reliable path
// on modern macOS for picking a specific window of a multi-window app
// from a background helper process like Logi Plugin Service.
//
// Why osascript and not P/Invoke to libobjc?
//   NSRunningApplication.activate(withOptions:) on a multi-window app
//   always raises the most-recently-used window in its history — it
//   cannot target a specific window. AXUIElement in ApplicationServices
//   can, but wiring it through objc_msgSend requires ~200 lines of
//   P/Invoke plus CFRelease bookkeeping. osascript is a single exec
//   and produces equivalent results for a one-shot raise.
//
// Permissions: the first invocation will trigger a macOS TCC prompt
// asking the user to grant Accessibility access to Logi Plugin Service.
// Without that grant, System Events returns an error and the method
// returns false; the caller then falls back to bundle-level activate.
internal static class AppleScriptActivator
{
    private const String OsaScriptPath = "/usr/bin/osascript";
    private const String VSCodeProcessName = "Code";

    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(2);

    public static Boolean RaiseCodeWindowByWorkspace(String workspaceName)
    {
        if (String.IsNullOrEmpty(workspaceName))
        {
            return false;
        }

        var script = BuildRaiseScript(workspaceName);
        return TryRunOsaScript(script);
    }

    private static String BuildRaiseScript(String workspaceName)
    {
        // Escape double quotes and backslashes so the workspace name
        // cannot break out of the AppleScript string literal. This is
        // defence-in-depth: workspaceName comes from the local VS Code
        // extension which we trust, but the value originates from user
        // filesystem paths.
        var escaped = workspaceName
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        // The script:
        //   1. Activate VS Code (raises the app to foreground).
        //   2. In System Events → process "Code", iterate windows and
        //      AXRaise the first one whose title contains the workspace
        //      substring. VS Code window titles have the form
        //      "<file> — <workspace> — Visual Studio Code".
        //   3. On stdout, print "matched: N; titles: ..." so the
        //      caller can see exactly which titles were enumerated
        //      and whether the filter hit. Non-zero matched means the
        //      AXRaise was attempted; zero means we need a different
        //      substring to disambiguate windows.
        return
            "tell application \"System Events\"\n"
            + $"    tell process \"{VSCodeProcessName}\"\n"
            + "        set frontmost to true\n"
            + "        set titles to {}\n"
            + "        try\n"
            + "            repeat with w in (every window)\n"
            + "                set end of titles to name of w\n"
            + "            end repeat\n"
            + "        end try\n"
            + "        set matchCount to 0\n"
            + "        try\n"
            + $"            set matches to (every window whose name contains \"{escaped}\")\n"
            + "            set matchCount to (count of matches)\n"
            + "            if matchCount > 0 then\n"
            + "                set targetWindow to item 1 of matches\n"
            + "                perform action \"AXRaise\" of targetWindow\n"
            + "            end if\n"
            + "        end try\n"
            + "        set AppleScript's text item delimiters to \" | \"\n"
            + "        set titleList to titles as string\n"
            + "        set AppleScript's text item delimiters to \"\"\n"
            + "        return \"matched=\" & matchCount & \" titles=\" & titleList\n"
            + "    end tell\n"
            + "end tell\n";
    }

    private static Boolean TryRunOsaScript(String script)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OsaScriptPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            // Pipe the script via stdin and pass `-` as the file to
            // avoid command-line length limits and shell quoting.
            process.StartInfo.ArgumentList.Add("-");

            if (!process.Start())
            {
                PluginLog.Warning("macro-claude: osascript failed to start");
                return false;
            }

            process.StandardInput.Write(script);
            process.StandardInput.Close();

            return WaitAndInterpret(process);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            PluginLog.Error(ex, "macro-claude: osascript spawn failed");
            return false;
        }
        catch (System.IO.IOException ex)
        {
            PluginLog.Error(ex, "macro-claude: osascript IO error");
            return false;
        }
    }

    private static Boolean WaitAndInterpret(Process process)
    {
        var finished = process.WaitForExit((Int32)ExecutionTimeout.TotalMilliseconds);
        if (!finished)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between WaitForExit and Kill.
            }

            PluginLog.Warning("macro-claude: osascript AXRaise timed out");
            return false;
        }

        if (process.ExitCode == 0)
        {
            var stdout = process.StandardOutput.ReadToEnd().Trim();
            PluginLog.Info($"macro-claude: osascript AXRaise exit 0 → {stdout}");
            return stdout.StartsWith("matched=", StringComparison.Ordinal)
                && !stdout.StartsWith("matched=0", StringComparison.Ordinal);
        }

        var stderr = process.StandardError.ReadToEnd();
        PluginLog.Warning(
            $"macro-claude: osascript AXRaise exited {process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {stderr.Trim()}");
        return false;
    }
}
