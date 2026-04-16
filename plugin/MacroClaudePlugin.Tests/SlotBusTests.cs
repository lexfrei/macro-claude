using System;
using System.Collections.Generic;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

// SlotBus is static and process-wide — serialise all classes that
// touch it via the "SlotBusStatic" collection so xunit v3 does not
// run them in parallel and stomp each other's ownership token.
[Collection("SlotBusStatic")]
public sealed class SlotBusTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 4, 12, 0, 0, 0, TimeSpan.Zero);

    public SlotBusTests()
    {
        SlotBus.ResetForTests();
    }

    public void Dispose()
    {
        SlotBus.ResetForTests();
    }

    private static SessionSnapshot SnapshotFor(String sessionId) => new(
        SessionId: sessionId,
        Pid: 1234,
        Cwd: "/tmp/fake",
        DisplayName: "",
        State: SessionState.Idle,
        TurnStartedAt: null,
        IdleSince: Now,
        UpdatedAt: Now);

    // ------------------------------------------------------------------
    // AcquireOwnership
    // ------------------------------------------------------------------

    [Fact]
    public void AcquireOwnership_Returns_Non_Empty_Guid()
    {
        var token = SlotBus.AcquireOwnership();

        Assert.NotEqual(Guid.Empty, token);
    }

    [Fact]
    public void AcquireOwnership_Returns_Different_Tokens_On_Consecutive_Calls()
    {
        var first = SlotBus.AcquireOwnership();
        var second = SlotBus.AcquireOwnership();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AcquireOwnership_Wipes_Previous_Snapshots()
    {
        var token1 = SlotBus.AcquireOwnership();
        SlotBus.Publish(token1, slot: 0, SnapshotFor("session-one"));
        Assert.Equal(1, SlotBus.SnapshotCount);

        var token2 = SlotBus.AcquireOwnership();

        Assert.Equal(0, SlotBus.SnapshotCount);
        Assert.Null(SlotBus.TryGetSnapshot(0));
        Assert.NotEqual(token1, token2);
    }

    // ------------------------------------------------------------------
    // Publish — happy paths
    // ------------------------------------------------------------------

    [Fact]
    public void Publish_With_Valid_Owner_Stores_Snapshot()
    {
        var token = SlotBus.AcquireOwnership();
        var snap = SnapshotFor("session-happy");

        var published = SlotBus.Publish(token, slot: 3, snap);

        Assert.True(published);
        Assert.Same(snap, SlotBus.TryGetSnapshot(3));
        Assert.Equal(1, SlotBus.SnapshotCount);
    }

    [Fact]
    public void Publish_Null_Snapshot_Removes_Existing_Slot()
    {
        var token = SlotBus.AcquireOwnership();
        SlotBus.Publish(token, slot: 2, SnapshotFor("session-to-remove"));
        Assert.Equal(1, SlotBus.SnapshotCount);

        var removed = SlotBus.Publish(token, slot: 2, snapshot: null);

        Assert.True(removed);
        Assert.Null(SlotBus.TryGetSnapshot(2));
        Assert.Equal(0, SlotBus.SnapshotCount);
    }

    [Fact]
    public void Publish_Fires_SlotChanged_Event()
    {
        var token = SlotBus.AcquireOwnership();
        var events = new List<(Int32 Slot, SessionSnapshot? Snap)>();
        SlotBus.SlotChanged += (s, snap) => events.Add((s, snap));

        var snap1 = SnapshotFor("session-evt");
        SlotBus.Publish(token, slot: 5, snap1);
        SlotBus.Publish(token, slot: 5, snapshot: null);

        Assert.Equal(2, events.Count);
        Assert.Equal((5, (SessionSnapshot?)snap1), events[0]);
        Assert.Equal((5, (SessionSnapshot?)null), events[1]);
    }

    // ------------------------------------------------------------------
    // Publish — zombie publisher rejection
    // ------------------------------------------------------------------

    [Fact]
    public void Publish_With_Stale_Token_Is_Rejected_After_Reacquire()
    {
        var staleToken = SlotBus.AcquireOwnership();
        var freshToken = SlotBus.AcquireOwnership();
        Assert.NotEqual(staleToken, freshToken);

        var rejected = SlotBus.Publish(staleToken, slot: 0, SnapshotFor("ghost"));

        Assert.False(rejected);
        Assert.Null(SlotBus.TryGetSnapshot(0));
        Assert.Equal(0, SlotBus.SnapshotCount);
    }

    [Fact]
    public void Publish_With_Stale_Token_Does_Not_Fire_SlotChanged()
    {
        var staleToken = SlotBus.AcquireOwnership();
        SlotBus.AcquireOwnership();
        var fired = false;
        SlotBus.SlotChanged += (_, _) => fired = true;

        SlotBus.Publish(staleToken, slot: 0, SnapshotFor("ghost"));

        Assert.False(fired);
    }

    [Fact]
    public void Publish_With_Empty_Guid_Token_Is_Rejected()
    {
        SlotBus.AcquireOwnership();

        var rejected = SlotBus.Publish(Guid.Empty, slot: 0, SnapshotFor("empty-token"));

        Assert.False(rejected);
        Assert.Null(SlotBus.TryGetSnapshot(0));
    }

    [Fact]
    public void Publish_Before_Any_AcquireOwnership_Is_Rejected()
    {
        // SnapshotCount starts at zero in the test setup; any publish
        // without a valid owner must return false and not store the
        // snapshot.
        var madeUpToken = Guid.NewGuid();

        var rejected = SlotBus.Publish(madeUpToken, slot: 0, SnapshotFor("early"));

        Assert.False(rejected);
        Assert.Equal(0, SlotBus.SnapshotCount);
    }

    // ------------------------------------------------------------------
    // Publish — slot index bounds
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(27)]
    public void Publish_With_Out_Of_Range_Slot_Is_Rejected(Int32 slot)
    {
        var token = SlotBus.AcquireOwnership();

        var rejected = SlotBus.Publish(token, slot, SnapshotFor("oob"));

        Assert.False(rejected);
        Assert.Equal(0, SlotBus.SnapshotCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    public void Publish_Accepts_All_Valid_Slot_Indices(Int32 slot)
    {
        var token = SlotBus.AcquireOwnership();

        var accepted = SlotBus.Publish(token, slot, SnapshotFor("valid-slot"));

        Assert.True(accepted);
        Assert.NotNull(SlotBus.TryGetSnapshot(slot));
    }

    [Fact]
    public void ValidSlotCount_Constant_Matches_Plugin_Load_Setting()
    {
        // MacroClaudePlugin.Load constructs SlotAssigner(maxSlots:
        // SlotBus.ValidSlotCount) and there are exactly this many
        // ClaudeSlotNCommand classes under src/Actions/. If someone
        // moves the constant, this test reminds them to add or
        // remove command classes accordingly.
        Assert.Equal(9, SlotBus.ValidSlotCount);
    }

    // ------------------------------------------------------------------
    // Publish — deduplication
    //
    // StatusReader re-emits snapshots on every poll tick (once per
    // second) even when nothing semantic has changed — its job is to
    // sample the filesystem, not decide whether the UI needs a repaint.
    // SlotBus is the right place to shed that noise: if the new
    // snapshot is content-equal to the one already in the slot, drop
    // the SlotChanged event. The ActionImageChanged flood was causing
    // the MacroClaude.log to grow by ~10 lines/sec.
    // ------------------------------------------------------------------

    private static readonly DateTimeOffset StableIdleSince
        = new(2026, 4, 12, 0, 0, 0, TimeSpan.Zero);

    private static SessionSnapshot SnapshotWith(
        String sessionId,
        SessionState state,
        Int32 pid,
        DateTimeOffset updatedAt) => new(
            SessionId: sessionId,
            Pid: pid,
            Cwd: "/tmp/dedup",
            DisplayName: "",
            State: state,
            TurnStartedAt: null,
            IdleSince: StableIdleSince,
            UpdatedAt: updatedAt,
            RepoName: "repo");

    [Fact]
    public void Publish_Identical_Snapshot_With_Only_UpdatedAt_Diff_Fires_SlotChanged_Once()
    {
        var token = SlotBus.AcquireOwnership();
        var events = new List<(Int32 Slot, SessionSnapshot? Snap)>();
        SlotBus.SlotChanged += (s, snap) => events.Add((s, snap));

        var first = SnapshotWith("sid-dup", SessionState.Idle, pid: 100, updatedAt: Now);
        var secondTimestampOnly = SnapshotWith("sid-dup", SessionState.Idle, pid: 100, updatedAt: Now.AddSeconds(1));

        SlotBus.Publish(token, slot: 0, first);
        SlotBus.Publish(token, slot: 0, secondTimestampOnly);

        Assert.Single(events);
    }

    [Fact]
    public void Publish_Different_State_Fires_SlotChanged_Twice()
    {
        var token = SlotBus.AcquireOwnership();
        var events = new List<(Int32 Slot, SessionSnapshot? Snap)>();
        SlotBus.SlotChanged += (s, snap) => events.Add((s, snap));

        var idle = SnapshotWith("sid-state", SessionState.Idle, pid: 100, updatedAt: Now);
        var working = SnapshotWith("sid-state", SessionState.Working, pid: 100, updatedAt: Now);

        SlotBus.Publish(token, slot: 0, idle);
        SlotBus.Publish(token, slot: 0, working);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Publish_Different_Pid_Fires_SlotChanged_Twice()
    {
        var token = SlotBus.AcquireOwnership();
        var events = new List<(Int32 Slot, SessionSnapshot? Snap)>();
        SlotBus.SlotChanged += (s, snap) => events.Add((s, snap));

        var pid100 = SnapshotWith("sid-pid", SessionState.Idle, pid: 100, updatedAt: Now);
        var pid200 = SnapshotWith("sid-pid", SessionState.Idle, pid: 200, updatedAt: Now);

        SlotBus.Publish(token, slot: 0, pid100);
        SlotBus.Publish(token, slot: 0, pid200);

        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Publish_Null_After_Snapshot_Always_Fires_SlotChanged()
    {
        var token = SlotBus.AcquireOwnership();
        var events = new List<(Int32 Slot, SessionSnapshot? Snap)>();
        SlotBus.SlotChanged += (s, snap) => events.Add((s, snap));

        var snap = SnapshotWith("sid-clear", SessionState.Idle, pid: 100, updatedAt: Now);

        SlotBus.Publish(token, slot: 0, snap);
        SlotBus.Publish(token, slot: 0, snapshot: null);

        Assert.Equal(2, events.Count);
        Assert.Null(events[1].Snap);
    }
}
