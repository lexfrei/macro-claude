#nullable enable
namespace Loupedeck.MacroClaudePlugin.Status
{
    using System;

    // Immutable, fully-resolved view of a single Claude Code session used by
    // button rendering and focus dispatch. Produced by StateResolver from raw
    // inputs (hook events, JSONL mtime, CPU usage, sessions/<PID>.json).
    public sealed record SessionSnapshot(
        String SessionId,
        Int32 Pid,
        String Cwd,
        String DisplayName,
        SessionState State,
        DateTimeOffset? TurnStartedAt,
        DateTimeOffset? IdleSince,
        DateTimeOffset UpdatedAt)
    {
        // Duration of the current turn (working/thinking/stuck) or the time
        // spent idle, depending on the state. Null for gone/error.
        public TimeSpan? Elapsed
        {
            get
            {
                switch (this.State)
                {
                    case SessionState.Working:
                    case SessionState.Thinking:
                    case SessionState.Stuck:
                        return this.TurnStartedAt is { } started
                            ? this.UpdatedAt - started
                            : (TimeSpan?)null;
                    case SessionState.Idle:
                        return this.IdleSince is { } idleSince
                            ? this.UpdatedAt - idleSince
                            : (TimeSpan?)null;
                    case SessionState.Gone:
                    case SessionState.Error:
                    default:
                        return null;
                }
            }
        }

        // Short label for the button (last path component or session name).
        public String ShortName
        {
            get
            {
                if (!String.IsNullOrWhiteSpace(this.DisplayName))
                {
                    return this.DisplayName;
                }
                var cwdLast = this.Cwd?.TrimEnd('/');
                if (String.IsNullOrEmpty(cwdLast))
                {
                    return this.SessionId.Length > 8
                        ? this.SessionId.Substring(0, 8)
                        : this.SessionId;
                }
                var slashIdx = cwdLast.LastIndexOf('/');
                return slashIdx >= 0 && slashIdx < cwdLast.Length - 1
                    ? cwdLast.Substring(slashIdx + 1)
                    : cwdLast;
            }
        }
    }

    public enum SessionState
    {
        // No PID in ~/.claude/sessions/ — nothing to render.
        Gone = 0,

        // Last event = Stop. Assistant waiting for prompt.
        Idle = 1,

        // UserPromptSubmit or tool call, heartbeat fresh (< 3s).
        Working = 2,

        // Working but JSONL silent for 3–30s with CPU still active.
        Thinking = 3,

        // Working but heartbeat > 30s and CPU near zero — stalled or interrupted.
        Stuck = 4,

        // StopFailure hook OR JSONL tail contains "[Request interrupted by user]".
        Error = 5,
    }
}
