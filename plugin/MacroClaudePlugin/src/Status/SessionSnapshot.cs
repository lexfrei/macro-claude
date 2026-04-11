using System;

namespace Loupedeck.MacroClaudePlugin.Status;

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
    DateTimeOffset UpdatedAt,
    String RepoName = "")
{
    // Duration of the current turn (working/thinking/stuck) or the time
    // spent idle, depending on the state. Null for gone/error.
    public TimeSpan? Elapsed => this.State switch
    {
        SessionState.Working or SessionState.Thinking or SessionState.Stuck
            => this.TurnStartedAt is { } started ? this.UpdatedAt - started : null,
        SessionState.Idle
            => this.IdleSince is { } idleSince ? this.UpdatedAt - idleSince : null,
        SessionState.Gone => null,
        SessionState.Error => null,
        _ => null,
    };

    // Short label for the button. Preference order:
    //   1. RepoName — resolved by GitRepoResolver at emit time, walks
    //      `.git` pointers so git worktrees show the main repo name
    //      instead of their branch-name basename.
    //   2. DisplayName — the `name` field from sessions/<pid>.json, set
    //      by Claude Code itself. Usually a git branch name, which is
    //      only useful when there's no cwd to work with.
    //   3. cwd basename — last path component of the session cwd.
    //   4. First eight characters of the session UUID — ultimate
    //      fallback so buttons never render with an empty name.
    public String ShortName
    {
        get
        {
            if (!String.IsNullOrWhiteSpace(this.RepoName))
            {
                return this.RepoName;
            }
            if (!String.IsNullOrWhiteSpace(this.DisplayName))
            {
                return this.DisplayName;
            }
            var cwd = this.Cwd?.TrimEnd('/');
            if (String.IsNullOrEmpty(cwd))
            {
                return this.SessionId.Length > 8 ? this.SessionId[..8] : this.SessionId;
            }
            var slashIdx = cwd.LastIndexOf('/');
            return slashIdx >= 0 && slashIdx < cwd.Length - 1
                ? cwd[(slashIdx + 1)..]
                : cwd;
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

    // Working but JSONL silent for 3-30s with CPU still active.
    Thinking = 3,

    // Working but heartbeat > 30s and CPU near zero — stalled or interrupted.
    Stuck = 4,

    // StopFailure hook OR JSONL tail contains "[Request interrupted by user]".
    Error = 5,
}
