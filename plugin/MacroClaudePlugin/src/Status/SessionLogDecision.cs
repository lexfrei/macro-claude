using System;

namespace Loupedeck.MacroClaudePlugin.Status;

// Pure predicate that answers: "should OnSessionUpdated emit its
// verbose 'session → slot' log line for this update?". The caller
// maintains a per-session memo and passes it in as `previous`.
public static class SessionLogDecision
{
    public static Boolean ShouldLog(LogMemo? previous, LogMemo next)
    {
        if (previous is not { } prev)
        {
            return true;
        }
        return prev != next;
    }
}

// The (slot, state) pair MacroClaudePlugin remembers per-session
// to decide whether a new update is worth logging.
public readonly record struct LogMemo(Int32 Slot, SessionState State);
