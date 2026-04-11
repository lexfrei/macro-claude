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
    // Upper bound on slot indices the plugin is willing to track.
    // Must stay in sync with the number of concrete ClaudeSlotNCommand
    // classes under src/Actions/ and with SlotAssigner.maxSlots in
    // MacroClaudePlugin.Load. Any Publish call outside this range is
    // rejected — this guards against zombie SlotAssigner instances
    // from a previous Plugin.Load that never got Unload'd, which can
    // otherwise corrupt SlotBus with stale mappings.
    public const Int32 ValidSlotCount = 9;

    private static readonly ConcurrentDictionary<Int32, SessionSnapshot> Snapshots = new();
    private static readonly Object OwnerLock = new();
    private static Guid _currentOwner = Guid.Empty;

    public static event Action<Int32, SessionSnapshot?>? SlotChanged;

    // Called by MacroClaudePlugin.Load to become the sole authorised
    // publisher on the bus. Any previous owner's token stops being
    // valid, so a zombie Plugin instance that survived an incomplete
    // LPS Unload can no longer publish into SlotBus. The snapshot
    // store is wiped so the new owner starts with a clean slate;
    // subsequent Publish calls from the new owner repopulate it.
    public static Guid AcquireOwnership()
    {
        var token = Guid.NewGuid();
        lock (OwnerLock)
        {
            _currentOwner = token;
            Snapshots.Clear();
        }
        PluginLog.Info($"macro-claude: SlotBus ownership acquired by token {token}");
        return token;
    }

    public static void Publish(Guid ownerToken, Int32 slot, SessionSnapshot? snapshot)
    {
        // Read the current owner inside the lock. Guid is a 128-bit
        // struct and the .NET memory model does not guarantee atomic
        // reads of values wider than a pointer, so an unlocked read
        // can observe a torn value during a concurrent
        // AcquireOwnership call. Taking the lock here costs a single
        // uncontended mutex acquire per publish, which is cheap next
        // to the FSEvents/CPU work the caller has already done.
        Guid owner;
        lock (OwnerLock)
        {
            owner = _currentOwner;
        }
        if (ownerToken != owner)
        {
            // Zombie publisher from a previous Plugin.Load — silently
            // drop. Not logged per-call because it would be a flood.
            return;
        }
        if (slot is < 0 or >= ValidSlotCount)
        {
            PluginLog.Warning(
                $"macro-claude: SlotBus.Publish rejected out-of-range slot {slot.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return;
        }
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

    public static Int32 SnapshotCount => Snapshots.Count;
}
