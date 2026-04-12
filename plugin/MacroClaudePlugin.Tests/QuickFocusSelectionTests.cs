using System;
using System.Threading;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

// Tests for QuickFocusSelector.FindMostRecentlyIdled — the pure
// selection logic that picks the best target from SlotBus state.
// Each test resets SlotBus, publishes test snapshots, and asserts
// the selection result.
//
// Serialised with SlotBusTests via [Collection] because both
// classes mutate the static SlotBus singleton — xunit v3 would
// otherwise run them in parallel and they'd stomp each other's
// _currentOwner token.
[Collection("SlotBusStatic")]
public sealed class QuickFocusSelectionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 4, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _token;

    public QuickFocusSelectionTests()
    {
        SlotBus.ResetForTests();
        _token = SlotBus.AcquireOwnership();
    }

    public void Dispose()
    {
        SlotBus.ResetForTests();
    }

    private static Int32 _nextPid = 10000;

    private SessionSnapshot Snap(
        String sessionId,
        SessionState state,
        DateTimeOffset? idleSince = null)
        => new(
            SessionId: sessionId,
            Pid: Interlocked.Increment(ref _nextPid),
            Cwd: $"/tmp/{sessionId}",
            DisplayName: "",
            State: state,
            TurnStartedAt: null,
            IdleSince: idleSince,
            UpdatedAt: Now);

    [Fact]
    public void Returns_Null_When_No_Sessions()
    {
        Assert.Null(QuickFocusSelector.FindMostRecentlyIdled());
    }

    [Fact]
    public void Returns_Null_When_All_Sessions_Working()
    {
        SlotBus.Publish(_token, 0, Snap("a", SessionState.Working));
        SlotBus.Publish(_token, 1, Snap("b", SessionState.Working));
        SlotBus.Publish(_token, 2, Snap("c", SessionState.Thinking));

        Assert.Null(QuickFocusSelector.FindMostRecentlyIdled());
    }

    [Fact]
    public void Returns_Only_Idle_Session()
    {
        SlotBus.Publish(_token, 0, Snap("working", SessionState.Working));
        SlotBus.Publish(_token, 1, Snap("idle-one", SessionState.Idle, Now.AddMinutes(-5)));
        SlotBus.Publish(_token, 2, Snap("thinking", SessionState.Thinking));

        var result = QuickFocusSelector.FindMostRecentlyIdled();

        Assert.NotNull(result);
        Assert.Equal("idle-one", result.SessionId);
    }

    [Fact]
    public void Returns_Most_Recently_Idled_When_Multiple_Idle()
    {
        SlotBus.Publish(_token, 0, Snap("old-idle", SessionState.Idle, Now.AddMinutes(-30)));
        SlotBus.Publish(_token, 1, Snap("fresh-idle", SessionState.Idle, Now.AddMinutes(-1)));
        SlotBus.Publish(_token, 2, Snap("mid-idle", SessionState.Idle, Now.AddMinutes(-10)));

        var result = QuickFocusSelector.FindMostRecentlyIdled();

        Assert.NotNull(result);
        Assert.Equal("fresh-idle", result.SessionId);
    }

    [Fact]
    public void Prefers_Session_With_IdleSince_Over_Null()
    {
        SlotBus.Publish(_token, 0, Snap("has-timestamp", SessionState.Idle, Now.AddMinutes(-5)));
        SlotBus.Publish(_token, 1, Snap("no-timestamp", SessionState.Idle, idleSince: null));

        var result = QuickFocusSelector.FindMostRecentlyIdled();

        Assert.NotNull(result);
        Assert.Equal("has-timestamp", result.SessionId);
    }

    [Fact]
    public void Ignores_Error_And_Stuck_Sessions()
    {
        SlotBus.Publish(_token, 0, Snap("error", SessionState.Error));
        SlotBus.Publish(_token, 1, Snap("stuck", SessionState.Stuck));
        SlotBus.Publish(_token, 2, Snap("idle", SessionState.Idle, Now));

        var result = QuickFocusSelector.FindMostRecentlyIdled();

        Assert.NotNull(result);
        Assert.Equal("idle", result.SessionId);
    }

    [Fact]
    public void Ignores_Waiting_Sessions()
    {
        var published0 = SlotBus.Publish(_token, 0, Snap("waiting", SessionState.Waiting));
        var published1 = SlotBus.Publish(_token, 1, Snap("idle", SessionState.Idle, Now));

        Assert.True(published0, "slot 0 publish failed");
        Assert.True(published1, "slot 1 publish failed");
        Assert.Equal(2, SlotBus.SnapshotCount);

        var result = QuickFocusSelector.FindMostRecentlyIdled();

        Assert.NotNull(result);
        Assert.Equal("idle", result.SessionId);
    }

    [Fact]
    public void Handles_All_Slots_Filled()
    {
        for (var i = 0; i < SlotBus.ValidSlotCount; i++)
        {
            var state = i == 4 ? SessionState.Idle : SessionState.Working;
            var idle = i == 4 ? Now.AddSeconds(-10) : (DateTimeOffset?)null;
            SlotBus.Publish(_token, i, Snap($"session-{i}", state, idle));
        }

        var result = QuickFocusSelector.FindMostRecentlyIdled();

        Assert.NotNull(result);
        Assert.Equal("session-4", result.SessionId);
    }
}
