using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Loupedeck.MacroClaudePlugin.Status;

// Watches the three Claude Code source-of-truth directories and emits
// SessionSnapshot events when a session's resolved state might have
// changed.
//
//   ~/.claude/session-status/<session_id>.json  — hook-written status
//   ~/.claude/sessions/<pid>.json               — session lifecycle
//   ~/.claude/projects/**/<session_id>.jsonl    — transcript, heartbeat
//
// CPU usage is polled once per second via `ps` for every known PID.
// StateResolver is invoked on every update and also on the timer tick so
// that thinking/stuck transitions surface without a fresh hook.
public sealed class StatusReader : IDisposable
{
    private const Int32 PollIntervalMs = 1000;

    private readonly String _sessionStatusDir;
    private readonly String _sessionsDir;
    private readonly String _projectsDir;
    private readonly FileSystemWatcher _statusWatcher;
    private readonly FileSystemWatcher _sessionsWatcher;
    private readonly ConcurrentDictionary<String, Accumulator> _bySessionId;
    private readonly ConcurrentDictionary<Int32, String> _sessionIdByPid;
    private readonly System.Timers.Timer _pollTimer;
    private readonly StateResolverConfig? _resolverConfig;
    private Boolean _disposed;

    public event EventHandler<SessionSnapshot>? SessionUpdated;
    public event EventHandler<String>? SessionRemoved;

    public StatusReader(String homeDirectory)
        : this(homeDirectory, LoadResolverConfigFromDefaultLocation(homeDirectory))
    {
    }

    public StatusReader(String homeDirectory, StateResolverConfig? resolverConfig)
    {
        this._resolverConfig = resolverConfig;
        this._sessionStatusDir = Path.Combine(homeDirectory, ".claude", "session-status");
        this._sessionsDir = Path.Combine(homeDirectory, ".claude", "sessions");
        this._projectsDir = Path.Combine(homeDirectory, ".claude", "projects");

        Directory.CreateDirectory(this._sessionStatusDir);

        this._bySessionId = new ConcurrentDictionary<String, Accumulator>();
        this._sessionIdByPid = new ConcurrentDictionary<Int32, String>();

        this._statusWatcher = new FileSystemWatcher(this._sessionStatusDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false,
        };
        this._statusWatcher.Created += this.OnStatusFileEvent;
        this._statusWatcher.Changed += this.OnStatusFileEvent;
        this._statusWatcher.Deleted += this.OnStatusFileDeleted;

        this._sessionsWatcher = new FileSystemWatcher(this._sessionsDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false,
        };
        this._sessionsWatcher.Created += this.OnSessionFileEvent;
        this._sessionsWatcher.Changed += this.OnSessionFileEvent;
        this._sessionsWatcher.Deleted += this.OnSessionFileDeleted;

        this._pollTimer = new System.Timers.Timer(PollIntervalMs)
        {
            AutoReset = true,
            Enabled = false,
        };
        this._pollTimer.Elapsed += this.OnPollTick;
    }

    public void Start()
    {
        this.InitialScan();
        this._statusWatcher.EnableRaisingEvents = true;
        this._sessionsWatcher.EnableRaisingEvents = true;
        this._pollTimer.Start();
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }
        this._disposed = true;

        this._pollTimer.Stop();
        this._pollTimer.Elapsed -= this.OnPollTick;
        this._pollTimer.Dispose();

        this._statusWatcher.EnableRaisingEvents = false;
        this._statusWatcher.Created -= this.OnStatusFileEvent;
        this._statusWatcher.Changed -= this.OnStatusFileEvent;
        this._statusWatcher.Deleted -= this.OnStatusFileDeleted;
        this._statusWatcher.Dispose();

        this._sessionsWatcher.EnableRaisingEvents = false;
        this._sessionsWatcher.Created -= this.OnSessionFileEvent;
        this._sessionsWatcher.Changed -= this.OnSessionFileEvent;
        this._sessionsWatcher.Deleted -= this.OnSessionFileDeleted;
        this._sessionsWatcher.Dispose();
    }

    // -------------------------------------------------------------------
    // Initial scan + event handlers
    // -------------------------------------------------------------------

    private void InitialScan()
    {
        if (Directory.Exists(this._sessionsDir))
        {
            foreach (var file in Directory.EnumerateFiles(this._sessionsDir, "*.json"))
            {
                this.TryReadSessionsFile(file);
            }
        }

        if (Directory.Exists(this._sessionStatusDir))
        {
            foreach (var file in Directory.EnumerateFiles(this._sessionStatusDir, "*.json"))
            {
                this.TryReadStatusFile(file);
            }
        }

        this.RefreshJsonlSignals();
        this.EmitAllSnapshots();
    }

    private void OnStatusFileEvent(Object sender, FileSystemEventArgs e)
    {
        this.TryReadStatusFile(e.FullPath);
        this.EmitSnapshotForFile(e.FullPath, isSessionsFile: false);
    }

    private void OnStatusFileDeleted(Object sender, FileSystemEventArgs e)
    {
        var sessionId = Path.GetFileNameWithoutExtension(e.Name) ?? String.Empty;
        if (String.IsNullOrEmpty(sessionId))
        {
            return;
        }
        if (this._bySessionId.TryRemove(sessionId, out var acc))
        {
            this._sessionIdByPid.TryRemove(acc.Pid, out _);
            this.SessionRemoved?.Invoke(this, sessionId);
        }
    }

    private void OnSessionFileEvent(Object sender, FileSystemEventArgs e)
    {
        this.TryReadSessionsFile(e.FullPath);
        this.EmitSnapshotForFile(e.FullPath, isSessionsFile: true);
    }

    private void OnSessionFileDeleted(Object sender, FileSystemEventArgs e)
    {
        var pidStr = Path.GetFileNameWithoutExtension(e.Name) ?? String.Empty;
        if (!Int32.TryParse(pidStr, out var pid))
        {
            return;
        }
        if (this._sessionIdByPid.TryRemove(pid, out var sessionId)
            && this._bySessionId.TryRemove(sessionId, out _))
        {
            this.SessionRemoved?.Invoke(this, sessionId);
        }
    }

    private void OnPollTick(Object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            this.ReconcileSessionStatusDirectory();
            this.ResolveMissingPids();
            this.ReapDeadPidSessions();
            this.RefreshCpuUsage();
            this.RefreshJsonlSignals();
            this.EmitAllSnapshots();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StatusReader poll error: {ex.Message}");
        }
    }

    // Some Claude Code frontends — notably the Anthropic VS Code
    // extension when a user reloads the window — leave stale
    // ~/.claude/session-status/<id>.json and
    // ~/.claude/sessions/<pid>.json files behind, because they do
    // not always fire a SessionEnd hook before dying. Over time the
    // user sees the same project on the macropad several times,
    // one button per historical session_id.
    //
    // For every accumulator with a known pid, check that the pid is
    // still alive. If not, we know the session is gone and can reap
    // it immediately — and, for hygiene, also scrub the stale
    // session-status file so the next reconcile sweep does not
    // resurrect the accumulator a tick later.
    private void ReapDeadPidSessions()
    {
        if (this._bySessionId.IsEmpty)
        {
            return;
        }

        foreach (var sessionId in this._bySessionId.Keys.ToArray())
        {
            if (!this._bySessionId.TryGetValue(sessionId, out var acc))
            {
                continue;
            }
            if (acc.Pid <= 0)
            {
                continue;
            }
            if (IsProcessAlive(acc.Pid))
            {
                continue;
            }

            TryDeleteStaleStatusFile(this._sessionStatusDir, sessionId);

            if (this._bySessionId.TryRemove(sessionId, out var removed))
            {
                if (removed.Pid > 0)
                {
                    this._sessionIdByPid.TryRemove(removed.Pid, out _);
                }
                this.SessionRemoved?.Invoke(this, sessionId);
            }
        }
    }

    private static Boolean IsProcessAlive(Int32 pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Already exited.
            return false;
        }
    }

    private static void TryDeleteStaleStatusFile(String sessionStatusDir, String sessionId)
    {
        try
        {
            var path = Path.Combine(sessionStatusDir, sessionId + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Someone else holds the file — try again next tick.
        }
        catch (UnauthorizedAccessException)
        {
            // Permissions — bail, harmless.
        }
    }

    // Every tick, look at accumulators that still have Pid=0 and try
    // to resolve them against the sessions directory. This is a
    // belt-and-braces retry for cases where the hook arrived before
    // Claude Code had written its sessions/<pid>.json file.
    private void ResolveMissingPids()
    {
        if (this._bySessionId.IsEmpty)
        {
            return;
        }
        foreach (var acc in this._bySessionId.Values)
        {
            if (acc.Pid == 0)
            {
                this.TryResolvePidForSession(acc);
            }
        }
    }

    // FileSystemWatcher on macOS sits on top of FSEvents and routinely
    // drops rapid Rename/Delete events, especially when hooks churn
    // either of our source-of-truth directories. That leaves ghost
    // sessions in memory forever. This reconciliation sweep is cheap
    // (two directory enumerates per second, each tiny) and catches
    // everything the watcher missed: any session_id that no longer
    // has a file in EITHER the session-status directory OR the
    // sessions/<pid>.json lifecycle directory is removed from
    // _bySessionId and the subscriber is notified.
    //
    // The union is important: an accumulator created from a
    // sessions/<pid>.json observation (before a hook event has
    // landed a session-status file) would otherwise only disappear
    // when its pid dies, which misses the case where the pid itself
    // vanished without ReapDeadPidSessions noticing (pid reuse,
    // stale zombie from an earlier Plugin instance, etc.).
    private void ReconcileSessionStatusDirectory()
    {
        if (this._bySessionId.IsEmpty)
        {
            return;
        }

        HashSet<String> onDisk;
        try
        {
            onDisk = new HashSet<String>(StringComparer.Ordinal);

            if (Directory.Exists(this._sessionStatusDir))
            {
                foreach (var file in Directory.EnumerateFiles(this._sessionStatusDir, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file) ?? String.Empty;
                    if (!String.IsNullOrEmpty(name))
                    {
                        onDisk.Add(name);
                    }
                }
            }

            if (Directory.Exists(this._sessionsDir))
            {
                foreach (var file in Directory.EnumerateFiles(this._sessionsDir, "*.json"))
                {
                    var sessionId = TryReadSessionIdFromSessionsFile(file);
                    if (!String.IsNullOrEmpty(sessionId))
                    {
                        onDisk.Add(sessionId);
                    }
                }
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var sessionIds = this._bySessionId.Keys.ToArray();
        var flags = new Dictionary<String, Boolean>(StringComparer.Ordinal);
        foreach (var sessionId in sessionIds)
        {
            if (this._bySessionId.TryGetValue(sessionId, out var acc))
            {
                flags[sessionId] = acc.HasStatusFile;
            }
        }

        foreach (var sessionId in ReconcileDecision.SessionsToReap(sessionIds, flags, onDisk))
        {
            if (this._bySessionId.TryRemove(sessionId, out var removed))
            {
                if (removed.Pid > 0)
                {
                    this._sessionIdByPid.TryRemove(removed.Pid, out _);
                }
                this.SessionRemoved?.Invoke(this, sessionId);
            }
        }
    }

    // Light-weight read of just the `sessionId` field from a
    // sessions/<pid>.json lifecycle file — used by the reconcile
    // sweep to check whether a session is still represented on
    // disk without firing any of the heavier accumulator-update
    // machinery.
    private static String TryReadSessionIdFromSessionsFile(String path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("sessionId", out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString() ?? String.Empty;
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return String.Empty;
    }

    // -------------------------------------------------------------------
    // File readers — tolerant of partial writes and bad JSON.
    // -------------------------------------------------------------------

    private void TryReadSessionsFile(String path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var pid = root.TryGetProperty("pid", out var pidEl) ? pidEl.GetInt32() : 0;
            var sessionId = root.TryGetProperty("sessionId", out var sidEl)
                ? sidEl.GetString() ?? String.Empty
                : String.Empty;
            var cwd = root.TryGetProperty("cwd", out var cwdEl)
                ? cwdEl.GetString() ?? String.Empty
                : String.Empty;
            var name = root.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : null;

            if (pid <= 0 || String.IsNullOrEmpty(sessionId))
            {
                return;
            }

            var acc = this._bySessionId.GetOrAdd(sessionId, _ => new Accumulator { SessionId = sessionId });
            acc.Pid = pid;
            acc.Cwd = cwd;
            acc.DisplayName = name;

            // A session seen through the lifecycle file is "tracked"
            // by the reconcile sweep in the same way as one seen
            // through a hook-written status file. Without this flag,
            // an accumulator created here can never be reaped by the
            // directory sweep, only by ReapDeadPidSessions which
            // misses the pid-reuse / zombie-plugin cases.
            acc.HasStatusFile = true;

            this._sessionIdByPid[pid] = sessionId;
        }
        catch (IOException)
        {
            // Partial write — skip and let the next event retry.
        }
        catch (JsonException)
        {
            // Non-JSON garbage — skip.
        }
        catch (UnauthorizedAccessException)
        {
            // File gone between enum and read — skip.
        }
    }

    // Claude Code writes ~/.claude/sessions/<pid>.json asynchronously
    // after it starts, so a hook SessionStart event can race ahead of
    // the sessions file. The hook path gives us session_id + cwd but
    // no pid, leaving Accumulator.Pid at its default zero — the focus
    // dispatcher then rejects the button press with InvalidPid. When
    // we notice a zero Pid, sweep the sessions directory for a file
    // that claims the same sessionId and adopt its pid.
    private void TryResolvePidForSession(Accumulator acc)
    {
        if (acc.Pid > 0 || String.IsNullOrEmpty(acc.SessionId))
        {
            return;
        }
        if (!Directory.Exists(this._sessionsDir))
        {
            return;
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(this._sessionsDir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("sessionId", out var sidEl)
                        || sidEl.ValueKind != JsonValueKind.String
                        || sidEl.GetString() != acc.SessionId)
                    {
                        continue;
                    }
                    if (!root.TryGetProperty("pid", out var pidEl)
                        || pidEl.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }
                    var pid = pidEl.GetInt32();
                    if (pid <= 0)
                    {
                        continue;
                    }
                    acc.Pid = pid;
                    if (root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                    {
                        acc.Cwd = cwdEl.GetString() ?? acc.Cwd;
                    }
                    if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        acc.DisplayName = nameEl.GetString();
                    }
                    this._sessionIdByPid[pid] = acc.SessionId;
                    return;
                }
                catch (IOException)
                {
                    // Partial write / disappeared — try next file.
                }
                catch (JsonException)
                {
                    // Non-JSON garbage — try next file.
                }
                catch (UnauthorizedAccessException)
                {
                    // Locked — try next file.
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TryReadStatusFile(String path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var sessionId = root.TryGetProperty("session_id", out var sidEl)
                ? sidEl.GetString() ?? String.Empty
                : String.Empty;
            if (String.IsNullOrEmpty(sessionId))
            {
                return;
            }

            var acc = this._bySessionId.GetOrAdd(sessionId, _ => new Accumulator { SessionId = sessionId });

            // Mark the accumulator as hook-backed. ReconcileSessionStatusDirectory
            // will only consider it eligible for removal once this
            // flag is set — before that, the accumulator might have
            // come from a sessions/<pid>.json observation and would
            // get killed on the very next tick, leaving the user
            // with a disappeared button for a live session.
            acc.HasStatusFile = true;

            // Resolve pid from ~/.claude/sessions/<pid>.json when we
            // still do not have one. Hook events race ahead of the
            // sessions file, and FSEvents sometimes drops the Created
            // notification entirely, so this guard catches both.
            this.TryResolvePidForSession(acc);

            if (root.TryGetProperty("cwd", out var cwdEl))
            {
                acc.Cwd = cwdEl.GetString() ?? acc.Cwd;
            }
            if (root.TryGetProperty("last_event", out var evEl))
            {
                acc.LastEvent = evEl.GetString();
            }
            if (root.TryGetProperty("last_event_s", out var evAtEl) && evAtEl.ValueKind == JsonValueKind.Number)
            {
                acc.LastEventAt = DateTimeOffset.FromUnixTimeSeconds(evAtEl.GetInt64());
            }
            if (root.TryGetProperty("heartbeat_s", out var hbEl) && hbEl.ValueKind == JsonValueKind.Number)
            {
                acc.HookHeartbeatAt = DateTimeOffset.FromUnixTimeSeconds(hbEl.GetInt64());
            }
            if (root.TryGetProperty("turn_started_s", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number)
            {
                acc.TurnStartedAt = DateTimeOffset.FromUnixTimeSeconds(tsEl.GetInt64());
            }
            else
            {
                acc.TurnStartedAt = null;
            }
            if (root.TryGetProperty("idle_since_s", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
            {
                acc.IdleSince = DateTimeOffset.FromUnixTimeSeconds(idEl.GetInt64());
            }
            else
            {
                acc.IdleSince = null;
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // -------------------------------------------------------------------
    // JSONL transcript heartbeat and interrupted marker.
    // -------------------------------------------------------------------
    //
    // Transcript file lives at:
    //   ~/.claude/projects/<path-encoded-cwd>/<session_id>.jsonl
    //
    // where <path-encoded-cwd> is the cwd with '/' replaced by '-'. Instead
    // of reconstructing the exact path we scan all project folders for
    // files matching "<session_id>.jsonl" and pick the one with the latest
    // mtime as the authoritative transcript.

    private void RefreshJsonlSignals()
    {
        if (!Directory.Exists(this._projectsDir))
        {
            return;
        }

        foreach (var acc in this._bySessionId.Values)
        {
            try
            {
                var transcript = this.FindTranscript(acc.SessionId);
                if (transcript is null)
                {
                    continue;
                }
                var info = new FileInfo(transcript);
                if (info.Exists)
                {
                    acc.JsonlMtimeAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                }
                acc.InterruptedMarker = JsonlTailHasInterruptMarker(transcript);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private String? FindTranscript(String sessionId)
    {
        var fileName = $"{sessionId}.jsonl";
        try
        {
            return Directory
                .EnumerateFiles(this._projectsDir, fileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Boolean JsonlTailHasInterruptMarker(String path)
    {
        // Read up to the last 4 KB of the file — the interrupt marker is a
        // single user message that fits comfortably in that window.
        const Int32 TailBytes = 4096;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            var read = (Int32)Math.Min(length, TailBytes);
            if (read <= 0)
            {
                return false;
            }
            stream.Seek(-read, SeekOrigin.End);
            var buffer = new Byte[read];
            var offset = 0;
            while (offset < read)
            {
                var n = stream.Read(buffer, offset, read - offset);
                if (n <= 0)
                {
                    break;
                }
                offset += n;
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, offset);
            return text.Contains("[Request interrupted by user]", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------
    // CPU polling via `ps`.
    // -------------------------------------------------------------------

    private void RefreshCpuUsage()
    {
        if (this._sessionIdByPid.IsEmpty)
        {
            return;
        }

        var pids = this._sessionIdByPid.Keys.ToArray();
        var pidList = String.Join(",", pids);

        var psi = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(pidList);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("pid=,pcpu=");

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return;
        }

        // Drain stdout/stderr asynchronously before WaitForExit to
        // avoid a pipe-buffer deadlock when ps writes enough output
        // to fill the OS pipe (~64 KB).
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(500))
        {
            try
            {
                proc.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }

        try
        {
            proc.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Already disposed.
        }

        var output = stdoutTask.GetAwaiter().GetResult() ?? String.Empty;
        _ = stderrTask;

        // We intentionally ignore proc.ExitCode. The only thing
        // that matters is whether a PID appears in the output rows:
        //   present → process alive, update its CPU
        //   absent  → process dead, reap the session
        //
        // Exit code is not checked because BSD/macOS `ps -p` returns
        // 1 whenever ANY requested PID is missing — which is the
        // normal dead-PID detection path, not an error.
        //
        // Empty output is also not guarded against: if all tracked
        // PIDs have exited between ticks, ps legitimately returns
        // no rows, and the reap loop below correctly removes all of
        // them. A hypothetical catastrophic ps crash would also
        // produce empty output and reap everything, but that is
        // recoverable — hooks recreate session state on the next
        // event. The alternative (guarding on empty output) would
        // permanently strand dead sessions when every Claude window
        // is closed at once.
        var seenPids = new HashSet<Int32>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            if (!Int32.TryParse(parts[0], out var pid))
            {
                continue;
            }
            if (!Double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cpu))
            {
                continue;
            }
            seenPids.Add(pid);
            if (this._sessionIdByPid.TryGetValue(pid, out var sessionId)
                && this._bySessionId.TryGetValue(sessionId, out var acc))
            {
                acc.CpuPercent = cpu;
            }
        }

        // PIDs missing from ps output — process is dead, remove and
        // also clean up the stale status file so reconciliation does
        // not resurrect the session on the next plugin startup.
        foreach (var pid in pids)
        {
            if (!seenPids.Contains(pid)
                && this._sessionIdByPid.TryRemove(pid, out var sessionId))
            {
                TryDeleteStaleStatusFile(this._sessionStatusDir, sessionId);
                if (this._bySessionId.TryRemove(sessionId, out _))
                {
                    this.SessionRemoved?.Invoke(this, sessionId);
                }
            }
        }
    }

    // -------------------------------------------------------------------
    // Snapshot emission.
    // -------------------------------------------------------------------

    private void EmitAllSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var acc in this._bySessionId.Values)
        {
            this.Emit(acc, now);
        }
    }

    private void EmitSnapshotForFile(String path, Boolean isSessionsFile)
    {
        var key = Path.GetFileNameWithoutExtension(path) ?? String.Empty;
        if (String.IsNullOrEmpty(key))
        {
            return;
        }

        Accumulator? acc = null;
        if (isSessionsFile)
        {
            if (Int32.TryParse(key, out var pid)
                && this._sessionIdByPid.TryGetValue(pid, out var sessionId))
            {
                this._bySessionId.TryGetValue(sessionId, out acc);
            }
        }
        else
        {
            this._bySessionId.TryGetValue(key, out acc);
        }

        if (acc is not null)
        {
            this.Emit(acc, DateTimeOffset.UtcNow);
        }
    }

    private void Emit(Accumulator acc, DateTimeOffset now)
    {
        // Freshest heartbeat is the max of the hook-written heartbeat and
        // the transcript JSONL mtime. That way we cover both streaming
        // token output and tool-call activity.
        var heartbeat = Max(acc.HookHeartbeatAt, acc.JsonlMtimeAt);

        var state = StateResolver.Determine(
            lastEvent: acc.LastEvent,
            heartbeatAt: heartbeat,
            cpuPercent: acc.CpuPercent,
            interruptedMarker: acc.InterruptedMarker,
            now: now,
            config: this._resolverConfig);

        var snapshot = new SessionSnapshot(
            SessionId: acc.SessionId,
            Pid: acc.Pid,
            Cwd: acc.Cwd,
            DisplayName: acc.DisplayName ?? String.Empty,
            State: state,
            TurnStartedAt: acc.TurnStartedAt,
            IdleSince: acc.IdleSince,
            UpdatedAt: now,
            RepoName: GitRepoResolver.ResolveRepoName(acc.Cwd) ?? String.Empty);

        this.SessionUpdated?.Invoke(this, snapshot);
    }

    private static StateResolverConfig? LoadResolverConfigFromDefaultLocation(String homeDirectory)
    {
        var configPath = Path.Combine(homeDirectory, ".claude", "macro-claude.json");
        return StateResolverConfig.TryLoadFromFile(configPath);
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null)
        {
            return b;
        }
        if (b is null)
        {
            return a;
        }
        return a.Value > b.Value ? a : b;
    }

    // -------------------------------------------------------------------
    // Mutable per-session accumulator. Internal only — never escapes.
    // -------------------------------------------------------------------

    private sealed class Accumulator
    {
        public String SessionId { get; init; } = String.Empty;
        public Int32 Pid { get; set; }
        public String Cwd { get; set; } = String.Empty;
        public String? DisplayName { get; set; }
        public String? LastEvent { get; set; }
        public DateTimeOffset? LastEventAt { get; set; }
        public DateTimeOffset? HookHeartbeatAt { get; set; }
        public DateTimeOffset? JsonlMtimeAt { get; set; }
        public DateTimeOffset? TurnStartedAt { get; set; }
        public DateTimeOffset? IdleSince { get; set; }
        public Double CpuPercent { get; set; }
        public Boolean InterruptedMarker { get; set; }

        // True once we have read a hook-written session-status file
        // for this session at least once. Reconciliation only reaps
        // sessions where this flag is set — otherwise an accumulator
        // created from ~/.claude/sessions/<pid>.json (seen before
        // the first hook event) would be killed on the next tick,
        // before the hook has had a chance to land its status file.
        public Boolean HasStatusFile { get; set; }
    }
}
