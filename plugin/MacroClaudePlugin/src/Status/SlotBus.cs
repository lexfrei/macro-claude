using System;
using System.Collections.Concurrent;

namespace Loupedeck.MacroClaudePlugin.Status;

// Process-wide store + event bus for slot snapshots. Anything in the
// plugin can query the latest snapshot for a slot index, and anything
// in the plugin can subscribe to changes.
//
// Why both a store and an event: PluginDynamicCommand instances are
// created by the Loupedeck Plugin Service at unpredictable times
// (command discovery, button rendering, parameter enumeration). A
// fresh instance hands LPS an empty `_snapshotsBySlot` dictionary if
// we keep state per instance, so the user sees dashes on every key
// until the next StatusReader.Emit — which never happens if there is
// no file event. Storing snapshots on SlotBus (static, process-wide)
// means every SessionStatusCommand instance reads from the same place,
// and the event is only needed to trigger ActionImageChanged on the
// live instance.
internal static class SlotBus
{
    private static readonly ConcurrentDictionary<Int32, SessionSnapshot> Snapshots = new();

    public static event Action<Int32, SessionSnapshot?>? SlotChanged;

    public static void Publish(Int32 slot, SessionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            Snapshots.TryRemove(slot, out _);
        }
        else
        {
            Snapshots[slot] = snapshot;
        }
        SlotChanged?.Invoke(slot, snapshot);
    }

    public static SessionSnapshot? TryGetSnapshot(Int32 slot)
        => Snapshots.TryGetValue(slot, out var snap) ? snap : null;
}
