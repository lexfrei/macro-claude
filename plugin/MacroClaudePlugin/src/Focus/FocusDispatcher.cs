using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Loupedeck.MacroClaudePlugin.Focus;

// Routes a focus request for a Claude Code session to the appropriate
// host application:
//
//   1. VS Code integrated terminal — via the companion extension's
//      HTTP bridge (one lock file per window under
//      ~/.claude/macro-claude-bridge/*.lock).
//   2. iTerm2 session — TODO, needs the iTerm2 protobuf API client.
//
// In every case, a native NSRunningApplication.activate call is used to
// raise the owning macOS application to the foreground. For VS Code the
// in-window terminal.show() already scrolls the correct terminal into
// view before we raise the window.
public static class FocusDispatcher
{
    private const String VSCodeBundleId = "com.microsoft.VSCode";
    private const String ITerm2BundleId = "com.googlecode.iterm2";
    private const String BridgeDirName = "macro-claude-bridge";
    private const String AuthHeader = "x-macro-claude-auth";

    private static readonly HttpClient HttpBridgeClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    public static async Task<FocusResult> FocusAsync(
        Int32 pid,
        String cwd,
        CancellationToken cancellationToken)
    {
        if (pid <= 0)
        {
            return FocusResult.InvalidPid;
        }

        // 1. Ask the VS Code companion extension bridge whether this
        //    PID belongs to one of its windows. We do this *first* to
        //    distinguish VS Code sessions from iTerm2 sessions so the
        //    caskad does not accidentally steal focus in the wrong
        //    terminal app. The bridge also runs `terminal.show()` on
        //    the matching integrated terminal so the right panel tab
        //    is active inside its window, and returns the workspace
        //    root path of that window so we can ask LaunchServices to
        //    raise it.
        var vscodeAnswer = await TryFocusVSCodeTerminalAsync(pid, cancellationToken).ConfigureAwait(false);
        if (vscodeAnswer is not null)
        {
            var (workspaceName, workspaceRoot) = vscodeAnswer.Value;
            PluginLog.Info(
                $"macro-claude: bridge confirmed VS Code ownership of pid {pid.ToString(System.Globalization.CultureInfo.InvariantCulture)} (workspaceRoot='{workspaceRoot}' name='{workspaceName}')");

            // Path A (preferred): open the workspace root reported by
            // the bridge via `vscode://file/<root>`. macOS
            // LaunchServices routes the URL to the VS Code window
            // already holding that folder — including windows on
            // different fullscreen Spaces, which AppleScript AXRaise
            // cannot reach because Accessibility API is Space-local.
            // We prefer the bridge-reported root over the session
            // `cwd` because claude may have been launched from a
            // subdirectory of the workspace; using cwd there would
            // pop open a brand-new window rooted at the subdirectory
            // instead of activating the existing window.
            if (!String.IsNullOrEmpty(workspaceRoot)
                && workspaceRoot.StartsWith('/')
                && VSCodeUrlActivator.OpenWorkspace(workspaceRoot))
            {
                return FocusResult.VSCodeTerminal;
            }

            // Path A': cwd fallback — if the bridge did not report a
            // workspace root (rare; happens for windows opened with
            // no folder), fall back to the session snapshot's cwd.
            if (!String.IsNullOrEmpty(cwd)
                && cwd.StartsWith('/')
                && VSCodeUrlActivator.OpenWorkspace(cwd))
            {
                return FocusResult.VSCodeTerminal;
            }

            // Path B: single-Space AXRaise via AppleScript. Works when
            // all VS Code windows live in the same Space, so it is a
            // sensible fallback when neither URL path worked.
            if (!String.IsNullOrEmpty(workspaceName)
                && AppleScriptActivator.RaiseCodeWindowByWorkspace(workspaceName))
            {
                return FocusResult.VSCodeTerminal;
            }

            // Path C: bundle-level activate. Lands the user in VS
            // Code somewhere — better than nothing.
            NativeActivator.ActivateByBundleId(VSCodeBundleId);
            return FocusResult.VSCodeTerminal;
        }

        // 2. Try iTerm2 session-level focus via the protobuf API. This
        //    is the ideal path — it activates the exact session (tab +
        //    split) that owns the Claude Code process, not just the
        //    iTerm2 app. Requires the user to have enabled the iTerm2
        //    Python API in Settings > General > Magic; on first use it
        //    triggers an AppleScript cookie prompt which the user must
        //    approve once per plugin process lifetime.
        if (await ITerm2Client.FocusSessionByPidAsync(pid, cancellationToken).ConfigureAwait(false))
        {
            NativeActivator.ActivateByBundleId(ITerm2BundleId);
            return FocusResult.ITerm2Session;
        }

        // 3. iTerm2 app-level activate — happens when the API is off,
        //    the socket is missing, the cookie was denied, or no
        //    session matches the PID. The user lands in iTerm2 and
        //    picks the tab manually.
        if (NativeActivator.ActivateByBundleId(ITerm2BundleId))
        {
            return FocusResult.ITerm2AppOnly;
        }

        // 4. VS Code app-level activate — last-chance fallback used
        //    when the companion extension is not installed yet, or the
        //    bridge lock file for this window is stale. Raises the VS
        //    Code window to the foreground without knowing which
        //    integrated terminal owns the PID. Better than NotFound —
        //    the user at least lands in the right app and can pick the
        //    terminal manually.
        if (NativeActivator.ActivateByBundleId(VSCodeBundleId))
        {
            return FocusResult.VSCodeAppOnly;
        }

        // 5. Unknown — surface so caller can log.
        _ = cwd;
        return FocusResult.NotFound;
    }

    // Returns (workspaceName, workspaceRoot) reported by the bridge
    // on success, or null if no bridge handled the PID. Empty strings
    // inside the tuple are legitimate — "VS Code window found, but
    // has no folder open" — so we distinguish via tuple nullability.
    private static async Task<(String Name, String Root)?> TryFocusVSCodeTerminalAsync(
        Int32 pid,
        CancellationToken cancellationToken)
    {
        var bridgeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            BridgeDirName);

        if (!Directory.Exists(bridgeDir))
        {
            return null;
        }

        var locks = EnumerateLockFiles(bridgeDir);
        foreach (var lockFile in locks)
        {
            var (port, token) = TryReadBridgeLock(lockFile);
            if (port <= 0 || String.IsNullOrEmpty(token))
            {
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/focus");
                request.Headers.TryAddWithoutValidation(AuthHeader, token);
                request.Content = new StringContent(
                    $"{{\"pid\":{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
                    Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var response = await HttpBridgeClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                return ParseBridgeResponse(body);
            }
            catch (HttpRequestException)
            {
                // Bridge for this lock is dead — try next window.
            }
            catch (TaskCanceledException)
            {
                // Timeout — try next window.
            }
        }

        return null;
    }

    private static (String Name, String Root) ParseBridgeResponse(String body)
    {
        if (String.IsNullOrEmpty(body))
        {
            return (String.Empty, String.Empty);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("workspaceName", out var ws)
                && ws.ValueKind == JsonValueKind.String
                ? ws.GetString() ?? String.Empty
                : String.Empty;
            var root = doc.RootElement.TryGetProperty("workspaceRoot", out var wr)
                && wr.ValueKind == JsonValueKind.String
                ? wr.GetString() ?? String.Empty
                : String.Empty;
            return (name, root);
        }
        catch (JsonException)
        {
            return (String.Empty, String.Empty);
        }
    }

    private static IEnumerable<String> EnumerateLockFiles(String bridgeDir)
    {
        try
        {
            return Directory.EnumerateFiles(bridgeDir, "*.lock");
        }
        catch (IOException)
        {
            return Array.Empty<String>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<String>();
        }
    }

    private static (Int32 Port, String? Token) TryReadBridgeLock(String lockFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(lockFile));
            var root = doc.RootElement;
            var port = root.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
                ? portEl.GetInt32()
                : 0;
            var token = root.TryGetProperty("authToken", out var tokenEl)
                ? tokenEl.GetString()
                : null;
            return (port, token);
        }
        catch (IOException)
        {
            return (0, null);
        }
        catch (JsonException)
        {
            return (0, null);
        }
    }
}

public enum FocusResult
{
    NotFound = 0,
    InvalidPid = 1,
    VSCodeTerminal = 2,
    ITerm2Session = 3,
    ITerm2AppOnly = 4,
    VSCodeAppOnly = 5,
}
