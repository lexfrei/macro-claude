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

        // 1. Try VS Code companion extension bridge.
        var vscodeHandled = await TryFocusVSCodeTerminalAsync(pid, cancellationToken).ConfigureAwait(false);
        if (vscodeHandled)
        {
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

        // 4. Unknown — surface so caller can log.
        _ = cwd;
        return FocusResult.NotFound;
    }

    private static async Task<Boolean> TryFocusVSCodeTerminalAsync(
        Int32 pid,
        CancellationToken cancellationToken)
    {
        var bridgeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            BridgeDirName);

        if (!Directory.Exists(bridgeDir))
        {
            return false;
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

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
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

        return false;
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
}
