#nullable enable
namespace Loupedeck.MacroClaudePlugin.Status
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

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
        private readonly FileSystemWatcher _statusWatcher;
        private readonly FileSystemWatcher _sessionsWatcher;
        private readonly ConcurrentDictionary<String, Accumulator> _bySessionId;
        private readonly ConcurrentDictionary<Int32, String> _sessionIdByPid;
        private readonly System.Timers.Timer _pollTimer;
        private Boolean _disposed;

        public event EventHandler<SessionSnapshot>? SessionUpdated;
        public event EventHandler<String>? SessionRemoved;

        public StatusReader(String homeDirectory)
        {
            this._sessionStatusDir = Path.Combine(homeDirectory, ".claude", "session-status");
            this._sessionsDir = Path.Combine(homeDirectory, ".claude", "sessions");

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
            this._statusWatcher.Dispose();
            this._sessionsWatcher.EnableRaisingEvents = false;
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
                this.RefreshCpuUsage();
                this.EmitAllSnapshots();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StatusReader poll error: {ex.Message}");
            }
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
                    acc.HeartbeatAt = DateTimeOffset.FromUnixTimeSeconds(hbEl.GetInt64());
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
            if (proc == null)
            {
                return;
            }
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(500))
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                }
                return;
            }

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

            // PIDs missing from ps output — process is dead, remove.
            foreach (var pid in pids)
            {
                if (!seenPids.Contains(pid)
                    && this._sessionIdByPid.TryRemove(pid, out var sessionId)
                    && this._bySessionId.TryRemove(sessionId, out _))
                {
                    this.SessionRemoved?.Invoke(this, sessionId);
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

            if (acc != null)
            {
                this.Emit(acc, DateTimeOffset.UtcNow);
            }
        }

        private void Emit(Accumulator acc, DateTimeOffset now)
        {
            var state = StateResolver.Determine(
                lastEvent: acc.LastEvent,
                heartbeatAt: acc.HeartbeatAt,
                cpuPercent: acc.CpuPercent,
                interruptedMarker: acc.InterruptedMarker,
                now: now);

            var snapshot = new SessionSnapshot(
                SessionId: acc.SessionId,
                Pid: acc.Pid,
                Cwd: acc.Cwd,
                DisplayName: acc.DisplayName ?? String.Empty,
                State: state,
                TurnStartedAt: acc.TurnStartedAt,
                IdleSince: acc.IdleSince,
                UpdatedAt: now);

            this.SessionUpdated?.Invoke(this, snapshot);
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
            public DateTimeOffset? HeartbeatAt { get; set; }
            public DateTimeOffset? TurnStartedAt { get; set; }
            public DateTimeOffset? IdleSince { get; set; }
            public Double CpuPercent { get; set; }
            public Boolean InterruptedMarker { get; set; }
        }
    }
}
