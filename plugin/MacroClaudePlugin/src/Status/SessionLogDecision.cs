using System;

namespace Loupedeck.MacroClaudePlugin.Status;

// Pure predicate that answers: "should OnSessionUpdated emit its
// verbose 'session → slot' log line for this update?". The caller
// maintains a per-session memo of the last (slot, state) it logged
// and passes it in as `previous`.
public static class SessionLogDecision
{
    public static Boolean ShouldLog(
        (Int32 Slot, SessionState State)? previous,
        Int32 nextSlot,
        SessionState nextState)
    {
        if (previous is not { } prev)
        {
            return true;
        }
        return prev.Slot != nextSlot || prev.State != nextState;
    }
}
