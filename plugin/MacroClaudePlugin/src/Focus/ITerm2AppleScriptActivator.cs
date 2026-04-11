using System;
using System.Diagnostics;

namespace Loupedeck.MacroClaudePlugin.Focus;

// Raise a specific iTerm2 session by TTY via AppleScript. This exists
// because:
//
//   * iTerm2 has a rich AppleScript dictionary that lets us iterate
//     windows/tabs/sessions and call `select` on a specific session.
//     `tell application "iTerm2" to activate` drives LaunchServices
//     to bring iTerm2 forward, which — unlike the AX-based
//     AppleScriptActivator we use for VS Code — works across
//     fullscreen Mission Control Spaces because the AppleScript
//     commands execute inside iTerm2's own process, not through
//     Accessibility API on a background helper.
//
//   * The existing ITerm2Client (protobuf over Unix socket) requires
//     the user to flip on Settings → General → Magic → Enable Python
//     API, which most users never do. AppleScript works out of the
//     box with zero configuration.
//
// We derive the target session's TTY from the PID via `ps -o tty=`
// (for example pid 89172 → "s000" → "/dev/ttys000"). The AppleScript
// then walks the iTerm2 session tree looking for a session whose
// `tty` property ends with that suffix. When claude was launched
// from a shell running inside an iTerm2 session, the shell and
// claude share the same controlling TTY, so this matches regardless
// of whether the PID we have is the shell or the child process.
internal static class ITerm2AppleScriptActivator
{
    private const String PsPath = "/bin/ps";
    private const String OsaScriptPath = "/usr/bin/osascript";
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(2);

    public static Boolean FocusSessionByPid(Int32 pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        var tty = ReadTtyForPid(pid);
        if (String.IsNullOrEmpty(tty))
        {
            PluginLog.Info($"macro-claude: no tty found for pid {pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} — likely not a terminal child");
            return false;
        }

        var script = BuildFocusScript(tty);
        return TryRunOsaScript(script, tty);
    }

    // Returns the short tty name (e.g. "ttys000") or empty if the
    // process has no controlling TTY.
    private static String ReadTtyForPid(Int32 pid)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(PsPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-o", "tty=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            });

            if (proc is null)
            {
                return String.Empty;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(1000))
            {
                try
                {
                    proc.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
                return String.Empty;
            }

            // `ps -o tty=` prints short names like "s000" or "ttys000",
            // or "?" when the process has no controlling terminal.
            if (String.IsNullOrEmpty(output) || output == "?")
            {
                return String.Empty;
            }

            // Normalise: strip any "tty" prefix so we compare against
            // the suffix "s000" and match "/dev/ttys000" regardless.
            return output.StartsWith("tty", StringComparison.Ordinal)
                ? output[3..]
                : output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return String.Empty;
        }
        catch (System.IO.IOException)
        {
            return String.Empty;
        }
    }

    private static String BuildFocusScript(String ttySuffix)
    {
        // Guard against AppleScript string injection — ttySuffix is
        // sourced from ps(1) output which is tightly constrained
        // (digits + letters), but escape quotes and backslashes
        // defensively.
        var escaped = ttySuffix
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        // The script:
        //   1. Activate iTerm2 (LaunchServices-level; works across
        //      fullscreen Spaces because the activation is driven
        //      from inside the app, not via AX API).
        //   2. Walk every window → tab → session, comparing the
        //      tty property suffix. iTerm2's `tty` returns the full
        //      path (e.g. "/dev/ttys000"); we match by suffix so we
        //      do not care about the "/dev/tty" prefix.
        //   3. On the FIRST match, `tell w to select t` followed by
        //      `tell t to select s`. The first switches the active
        //      tab of the owning window; the second switches the
        //      active split inside that tab. Calling `select s`
        //      alone is NOT enough — iTerm2 will leave the current
        //      tab unchanged and the target session ends up
        //      selected inside a hidden tab. Tested against iTerm2
        //      3.5.x on macOS 15.
        //   4. Bail out of all three nested loops with `exit repeat`
        //      once matched so we do not keep select'ing additional
        //      sessions on unrelated tabs (select is idempotent for
        //      the target, but wasting cycles causes visible flicker
        //      if there are many tabs open).
        //   5. Return "matched=1" or "matched=0" for diagnostics.
        return
            "tell application \"iTerm2\"\n"
            + "    activate\n"
            + "    set matched to 0\n"
            + "    repeat with w in windows\n"
            + "        if matched is 1 then exit repeat\n"
            + "        repeat with t in tabs of w\n"
            + "            if matched is 1 then exit repeat\n"
            + "            repeat with s in sessions of t\n"
            + "                try\n"
            + "                    set sessionTty to tty of s\n"
            + $"                    if sessionTty ends with \"{escaped}\" then\n"
            + "                        tell w to select t\n"
            + "                        tell t to select s\n"
            + "                        set matched to 1\n"
            + "                        exit repeat\n"
            + "                    end if\n"
            + "                end try\n"
            + "            end repeat\n"
            + "        end repeat\n"
            + "    end repeat\n"
            + "    return \"matched=\" & matched\n"
            + "end tell\n";
    }

    private static Boolean TryRunOsaScript(String script, String ttySuffix)
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

            process.StartInfo.ArgumentList.Add("-");

            if (!process.Start())
            {
                PluginLog.Warning("macro-claude: osascript (iTerm2) failed to start");
                return false;
            }

            // Drain stdout/stderr asynchronously before blocking on
            // WaitForExit, otherwise a full OS pipe buffer can
            // deadlock the child. Same reasoning as
            // AppleScriptActivator.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            process.StandardInput.Write(script);
            process.StandardInput.Close();

            var finished = process.WaitForExit((Int32)ExecutionTimeout.TotalMilliseconds);
            if (!finished)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }

                PluginLog.Warning($"macro-claude: osascript (iTerm2) timed out for tty {ttySuffix}");
                return false;
            }

            // Synchronise with the async stdout/stderr flush — the
            // timed WaitForExit overload can return before the child's
            // output has been fully written to the redirected streams.
            try
            {
                process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                // Already exited / disposed — safe to continue.
            }

            var stdout = SafeResult(stdoutTask).Trim();
            var stderr = SafeResult(stderrTask).Trim();

            if (process.ExitCode == 0)
            {
                PluginLog.Info($"macro-claude: iTerm2 AppleScript for tty {ttySuffix} → {stdout}");
                return stdout == "matched=1";
            }

            PluginLog.Warning(
                $"macro-claude: iTerm2 AppleScript exited {process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {stderr}");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            PluginLog.Error(ex, "macro-claude: osascript (iTerm2) spawn failed");
            return false;
        }
        catch (System.IO.IOException ex)
        {
            PluginLog.Error(ex, "macro-claude: osascript (iTerm2) IO error");
            return false;
        }
    }

    private static String SafeResult(System.Threading.Tasks.Task<String> task)
    {
        try
        {
            return task.GetAwaiter().GetResult() ?? String.Empty;
        }
        catch (System.IO.IOException)
        {
            return String.Empty;
        }
        catch (OperationCanceledException)
        {
            return String.Empty;
        }
    }
}
