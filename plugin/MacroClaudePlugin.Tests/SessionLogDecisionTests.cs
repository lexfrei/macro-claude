using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

// Pure-logic predicate used by MacroClaudePlugin.OnSessionUpdated
// to decide whether the verbose "session {sid} → slot {n} state={s}"
// line is worth emitting. After SlotBus dedup (prior commit) this
// line was the last remaining 10Hz log source — it fired on every
// poll tick even when nothing had actually changed.
//
// Rule: log only when it's the first sighting OR when the (slot,
// state) pair differs from what we logged last time for that session.
public sealed class SessionLogDecisionTests
{
    [Fact]
    public void First_Observation_Logs()
    {
        var should = SessionLogDecision.ShouldLog(
            previous: null,
            next: new LogMemo(0, SessionState.Idle));

        Assert.True(should);
    }

    [Fact]
    public void Same_Slot_Same_State_Does_Not_Log()
    {
        var should = SessionLogDecision.ShouldLog(
            previous: new LogMemo(0, SessionState.Idle),
            next: new LogMemo(0, SessionState.Idle));

        Assert.False(should);
    }

    [Fact]
    public void Same_Slot_Different_State_Logs()
    {
        var should = SessionLogDecision.ShouldLog(
            previous: new LogMemo(0, SessionState.Idle),
            next: new LogMemo(0, SessionState.Working));

        Assert.True(should);
    }

    [Fact]
    public void Different_Slot_Same_State_Logs()
    {
        // Edge case: SlotAssigner can re-bind a session to a new
        // slot if an earlier slot freed up. Rare in practice but
        // worth logging when it happens.
        var should = SessionLogDecision.ShouldLog(
            previous: new LogMemo(0, SessionState.Idle),
            next: new LogMemo(3, SessionState.Idle));

        Assert.True(should);
    }
}
