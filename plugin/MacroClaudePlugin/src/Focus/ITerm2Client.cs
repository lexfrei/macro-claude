using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Google.Protobuf;

using Iterm2;

namespace Loupedeck.MacroClaudePlugin.Focus;

// Minimal iTerm2 Python API client used for session-level focus on
// button press. Talks protobuf over a WebSocket tunnelled through the
// iTerm2 Unix domain socket.
//
// Vendored wire format: plugin/MacroClaudePlugin/src/Proto/api.proto
// (sourced from github.com/gnachman/iTerm2/blob/master/proto/api.proto).
//
// Connection flow:
//   1. AF_UNIX connect to
//      ~/Library/Application Support/iTerm2/private/socket
//   2. Manual HTTP/1.1 Upgrade handshake with subprotocol
//      "api.iterm2.com" and x-iterm2-cookie / x-iterm2-key headers
//   3. WebSocket.CreateFromStream on the upgraded NetworkStream
//   4. Send ClientOriginatedMessage protobuf frames, receive
//      ServerOriginatedMessage frames
//
// Authentication: iTerm2's AppleScript "request cookie and key" flow.
// Called once via osascript on the first use and cached in memory for
// the plugin process lifetime. Every subsequent button press is
// entirely zero-exec.
//
// The alternative auth path (a root-owned
// ~/Library/Application Support/iTerm2/disable-automation-auth file)
// needs a one-time sudo operation that we cannot trigger from a plugin
// so it is not attempted here.
internal sealed class ITerm2Client : IDisposable
{
    private static readonly String SocketPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "Application Support",
        "iTerm2",
        "private",
        "socket");

    private const String Subprotocol = "api.iterm2.com";
    private const String AppName = "macro-claude";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    // Cached cookie/key for the plugin process lifetime. Guarded by CredsLock.
    private static (String Cookie, String Key)? _cachedCreds;
    private static readonly Object CredsLock = new();

    private Stream? _stream;
    private WebSocket? _ws;
    private Int64 _nextRequestId;

    // True iff the iTerm2 API Unix socket exists. When false, the caller
    // should fall through to app-level activate.
    public static Boolean IsAvailable() => File.Exists(SocketPath);

    // Connect to iTerm2, walk every window/tab/session, find the session
    // whose foreground jobPid matches targetPid, and activate it.
    public static async Task<Boolean> FocusSessionByPidAsync(
        Int32 targetPid,
        CancellationToken cancellationToken)
    {
        if (targetPid <= 0 || !IsAvailable())
        {
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        var client = new ITerm2Client();
        try
        {
            if (!await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                return false;
            }

            var sessionId = await client
                .FindSessionByJobPidAsync(targetPid, timeoutCts.Token)
                .ConfigureAwait(false);

            if (String.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            return await client
                .ActivateSessionAsync(sessionId, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            client.Dispose();
        }
    }

    // -------------------------------------------------------------------
    // Connect + WebSocket handshake
    // -------------------------------------------------------------------

    private async Task<Boolean> ConnectAsync(CancellationToken ct)
    {
        if (GetCookieCredentials() is not { } creds)
        {
            return false;
        }

        // CA2000: NetworkStream takes ownership via ownsSocket:true, so the
        // socket is disposed transitively when this._stream is disposed in
        // Dispose(). Suppress the analyzer which does not understand the
        // ownership handoff.
#pragma warning disable CA2000 // Dispose objects before losing scope
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
#pragma warning restore CA2000
        try
        {
            await socket
                .ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return false;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            return false;
        }

        this._stream = new NetworkStream(socket, ownsSocket: true);

        var secKey = GenerateWebSocketKey();
        var handshake = BuildHttpUpgradeRequest(secKey, creds.Cookie, creds.Key);
        var handshakeBytes = Encoding.ASCII.GetBytes(handshake);
        await this._stream.WriteAsync(handshakeBytes, ct).ConfigureAwait(false);

        if (!await ReadHttpUpgradeResponseAsync(this._stream, ct).ConfigureAwait(false))
        {
            return false;
        }

        this._ws = WebSocket.CreateFromStream(
            this._stream,
            isServer: false,
            subProtocol: Subprotocol,
            keepAliveInterval: Timeout.InfiniteTimeSpan);

        return true;
    }

    private static String BuildHttpUpgradeRequest(String secKey, String cookie, String key)
    {
        var sb = new StringBuilder(512);
        sb.Append("GET / HTTP/1.1\r\n");
        sb.Append("Host: localhost\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Version: 13\r\n");
        sb.Append("Sec-WebSocket-Key: ").Append(secKey).Append("\r\n");
        sb.Append("Sec-WebSocket-Protocol: ").Append(Subprotocol).Append("\r\n");
        sb.Append("origin: ws://localhost/\r\n");
        sb.Append("x-iterm2-library-version: ").Append(AppName).Append(" 1.0\r\n");
        sb.Append("x-iterm2-disable-auth-ui: true\r\n");
        sb.Append("x-iterm2-cookie: ").Append(cookie).Append("\r\n");
        sb.Append("x-iterm2-key: ").Append(key).Append("\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static async Task<Boolean> ReadHttpUpgradeResponseAsync(Stream stream, CancellationToken ct)
    {
        var buf = new Byte[4096];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            total += n;

            var text = Encoding.ASCII.GetString(buf, 0, total);
            if (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            return text.StartsWith("HTTP/1.1 101", StringComparison.Ordinal);
        }
        return false;
    }

    private static String GenerateWebSocketKey()
    {
        Span<Byte> raw = stackalloc Byte[16];
        RandomNumberGenerator.Fill(raw);
        return Convert.ToBase64String(raw);
    }

    // -------------------------------------------------------------------
    // Cookie / key via AppleScript (cached)
    // -------------------------------------------------------------------

    private static (String Cookie, String Key)? GetCookieCredentials()
    {
        lock (CredsLock)
        {
            if (_cachedCreds.HasValue)
            {
                return _cachedCreds;
            }
            _cachedCreds = FetchCookieFromITerm2();
            return _cachedCreds;
        }
    }

    private static (String Cookie, String Key)? FetchCookieFromITerm2()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(
                $"tell application \"iTerm2\" to request cookie and key for app named \"{AppName}\"");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            if (!proc.WaitForExit(3000))
            {
                try
                {
                    proc.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                return null;
            }

            if (proc.ExitCode != 0)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                return null;
            }
            return (parts[0], parts[1]);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------------
    // Protobuf send/receive primitives
    // -------------------------------------------------------------------

    private async Task<ServerOriginatedMessage?> SendAsync(
        ClientOriginatedMessage request,
        CancellationToken ct)
    {
        if (this._ws is null || this._ws.State != WebSocketState.Open)
        {
            return null;
        }

        request.Id = Interlocked.Increment(ref this._nextRequestId);
        var bytes = request.ToByteArray();

        await this._ws.SendAsync(
            new ArraySegment<Byte>(bytes),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken: ct).ConfigureAwait(false);

        return await this.ReceiveAsync(ct).ConfigureAwait(false);
    }

    private async Task<ServerOriginatedMessage?> ReceiveAsync(CancellationToken ct)
    {
        if (this._ws is null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        var buf = new Byte[8192];
        while (true)
        {
            var result = await this._ws
                .ReceiveAsync(new ArraySegment<Byte>(buf), ct)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            await ms.WriteAsync(buf.AsMemory(0, result.Count), ct).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        ms.Position = 0;
        try
        {
            return ServerOriginatedMessage.Parser.ParseFrom(ms);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------------
    // High-level operations
    // -------------------------------------------------------------------

    private async Task<String?> FindSessionByJobPidAsync(Int32 targetPid, CancellationToken ct)
    {
        var request = new ClientOriginatedMessage
        {
            ListSessionsRequest = new ListSessionsRequest(),
        };
        var response = await this.SendAsync(request, ct).ConfigureAwait(false);
        if (response?.ListSessionsResponse is null)
        {
            return null;
        }

        foreach (var window in response.ListSessionsResponse.Windows)
        {
            foreach (var tab in window.Tabs)
            {
                foreach (var sessionId in EnumerateSessions(tab.Root))
                {
                    var pid = await this.GetSessionJobPidAsync(sessionId, ct).ConfigureAwait(false);
                    if (pid == targetPid)
                    {
                        return sessionId;
                    }
                }
            }
        }
        return null;
    }

    private static IEnumerable<String> EnumerateSessions(SplitTreeNode? node)
    {
        if (node is null)
        {
            yield break;
        }
        foreach (var link in node.Links)
        {
            if (link.Session is { } session && !String.IsNullOrEmpty(session.UniqueIdentifier))
            {
                yield return session.UniqueIdentifier;
            }
            else if (link.Node is not null)
            {
                foreach (var child in EnumerateSessions(link.Node))
                {
                    yield return child;
                }
            }
        }
    }

    private async Task<Int32> GetSessionJobPidAsync(String sessionId, CancellationToken ct)
    {
        var variableRequest = new VariableRequest
        {
            SessionId = sessionId,
        };
        variableRequest.Get.Add("jobPid");

        var request = new ClientOriginatedMessage
        {
            VariableRequest = variableRequest,
        };
        var response = await this.SendAsync(request, ct).ConfigureAwait(false);
        if (response?.VariableResponse is null)
        {
            return 0;
        }
        if (response.VariableResponse.Values.Count == 0)
        {
            return 0;
        }

        // iTerm2 returns variables as JSON-encoded strings. For numeric
        // variables like jobPid it is usually the raw number, but some
        // versions emit a quoted string. Strip quotes and parse.
        var raw = response.VariableResponse.Values[0].Trim('"', ' ');
        return Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : 0;
    }

    private async Task<Boolean> ActivateSessionAsync(String sessionId, CancellationToken ct)
    {
        var request = new ClientOriginatedMessage
        {
            ActivateRequest = new ActivateRequest
            {
                SessionId = sessionId,
                OrderWindowFront = true,
                SelectTab = true,
                SelectSession = true,
            },
        };
        var response = await this.SendAsync(request, ct).ConfigureAwait(false);
        return response?.ActivateResponse is not null;
    }

    // -------------------------------------------------------------------
    // Cleanup
    // -------------------------------------------------------------------

    public void Dispose()
    {
        try
        {
            if (this._ws is { State: WebSocketState.Open })
            {
                this._ws.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    statusDescription: null,
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (WebSocketException)
        {
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        this._ws?.Dispose();
        this._ws = null;
        this._stream?.Dispose();
        this._stream = null;
    }
}
