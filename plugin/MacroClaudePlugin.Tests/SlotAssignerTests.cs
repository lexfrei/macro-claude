using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

public sealed class SlotAssignerTests
{
    [Fact]
    public void Ensure_Assigns_First_Free_Slot_Starting_At_Zero()
    {
        var assigner = new SlotAssigner(maxSlots: 9);

        Assert.Equal(0, assigner.Ensure("sess-A"));
        Assert.Equal(1, assigner.Ensure("sess-B"));
        Assert.Equal(2, assigner.Ensure("sess-C"));
    }

    [Fact]
    public void Ensure_Is_Idempotent_For_Same_Session_Id()
    {
        var assigner = new SlotAssigner(maxSlots: 9);

        var first = assigner.Ensure("sess");
        var second = assigner.Ensure("sess");
        var third = assigner.Ensure("sess");

        Assert.Equal(0, first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void Release_Frees_The_Slot_For_Reuse()
    {
        var assigner = new SlotAssigner(maxSlots: 9);

        assigner.Ensure("sess-A"); // 0
        assigner.Ensure("sess-B"); // 1
        assigner.Ensure("sess-C"); // 2

        var freed = assigner.Release("sess-B");
        Assert.Equal(1, freed);

        // The next Ensure picks the lowest free slot — now slot 1.
        Assert.Equal(1, assigner.Ensure("sess-D"));
    }

    [Fact]
    public void Ensure_Returns_Minus_One_When_All_Slots_Occupied()
    {
        var assigner = new SlotAssigner(maxSlots: 3);

        Assert.Equal(0, assigner.Ensure("sess-A"));
        Assert.Equal(1, assigner.Ensure("sess-B"));
        Assert.Equal(2, assigner.Ensure("sess-C"));

        Assert.Equal(-1, assigner.Ensure("sess-D"));
    }

    [Fact]
    public void Release_Returns_Minus_One_For_Unknown_Session()
    {
        var assigner = new SlotAssigner(maxSlots: 9);

        Assert.Equal(-1, assigner.Release("never-registered"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_Or_Null_SessionId_Yields_Minus_One(String? sessionId)
    {
        var assigner = new SlotAssigner(maxSlots: 9);
        Assert.Equal(-1, assigner.Ensure(sessionId!));
        Assert.Equal(-1, assigner.Release(sessionId!));
    }

    [Fact]
    public void GetSessionAt_Returns_Assigned_Session_Id_Or_Null()
    {
        var assigner = new SlotAssigner(maxSlots: 9);

        assigner.Ensure("sess-A");
        assigner.Ensure("sess-B");

        Assert.Equal("sess-A", assigner.GetSessionAt(0));
        Assert.Equal("sess-B", assigner.GetSessionAt(1));
        Assert.Null(assigner.GetSessionAt(2));
        Assert.Null(assigner.GetSessionAt(42));
    }

    [Fact]
    public void Constructor_Rejects_Non_Positive_MaxSlots()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotAssigner(maxSlots: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotAssigner(maxSlots: -5));
    }

    [Fact]
    public async Task Concurrent_Ensure_Calls_Do_Not_Double_Assign()
    {
        var assigner = new SlotAssigner(maxSlots: 9);
        const Int32 parallelism = 128;
        var sessionIds = Enumerable.Range(0, parallelism)
            .Select(i => $"sess-{i}")
            .ToArray();

        var results = new ConcurrentBag<Int32>();

        await Parallel.ForEachAsync(
            sessionIds,
            async (sid, ct) =>
            {
                await Task.Yield();
                results.Add(assigner.Ensure(sid));
            });

        // Exactly 9 unique non-negative slots plus -1 markers for the rest.
        var assigned = results.Where(r => r >= 0).ToArray();
        var full = results.Where(r => r == -1).ToArray();

        Assert.Equal(9, assigned.Length);
        Assert.Equal(9, assigned.Distinct().Count());
        Assert.Equal(parallelism - 9, full.Length);
    }
}
