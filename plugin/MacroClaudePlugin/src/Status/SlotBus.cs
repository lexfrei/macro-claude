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
        return token;
    }

    // Publishes a snapshot (or clears one when snapshot is null) for
    // the given slot. Returns true on success and false when the
    // call is rejected — either because the caller's token no
    // longer matches the active owner (zombie Plugin from a stale
    // LPS reload), or because the slot index is outside the
    // supported range. Silent rejection is intentional: zombie
    // publishers fire every second and we do not want them to flood
    // the log. Caller may log the false return if it cares.
    public static Boolean Publish(Guid ownerToken, Int32 slot, SessionSnapshot? snapshot)
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
        if (ownerToken == Guid.Empty || ownerToken != owner)
        {
            return false;
        }
        if (slot is < 0 or >= ValidSlotCount)
        {
            return false;
        }
        SessionSnapshot? previous;
        if (snapshot is null)
        {
            Snapshots.TryRemove(slot, out previous);
        }
        else
        {
            previous = Snapshots.TryGetValue(slot, out var existing) ? existing : null;
            Snapshots[slot] = snapshot;
        }

        // Drop no-op SlotChanged — same slot, same snapshot content.
        // Still return true: the publish itself was accepted, even
        // though no subscriber needed to know about it. StatusReader
        // re-emits every poll tick with a fresh UpdatedAt stamp but
        // otherwise identical content, and we do not want 10 Hz of
        // ActionImageChanged going into the Loupedeck plugin log.
        // Null→null (clearing an already-empty slot) is also a no-op.
        if (snapshot is null && previous is null)
        {
            return true;
        }
        if (snapshot is not null && snapshot.ContentEquals(previous))
        {
            return true;
        }

        SlotChanged?.Invoke(slot, snapshot);
        return true;
    }

    // Test hook: reset everything. Not called by production code —
    // tests invoke it in their setup to isolate from the static
    // state that previous tests may have left behind.
    internal static void ResetForTests()
    {
        lock (OwnerLock)
        {
            _currentOwner = Guid.Empty;
            Snapshots.Clear();
        }
        SlotChanged = null;
    }

    public static SessionSnapshot? TryGetSnapshot(Int32 slot)
        => Snapshots.TryGetValue(slot, out var snap) ? snap : null;

    public static Int32 SnapshotCount => Snapshots.Count;
}
