using System;
using System.Collections.Generic;

namespace Loupedeck.MacroClaudePlugin.Status;

// Assigns Claude Code sessions to LCD key slots on a first-come,
// first-served basis. One instance is shared by the plugin.
//
// Thread-safe via a single mutex — all operations are O(1) over small
// dictionaries so contention is not a concern.
internal sealed class SlotAssigner
{
    private readonly Object _lock = new();
    private readonly Dictionary<String, Int32> _sessionToSlot = new(StringComparer.Ordinal);
    private readonly Dictionary<Int32, String> _slotToSession = new();
    private readonly Int32 _maxSlots;

    public SlotAssigner(Int32 maxSlots)
    {
        if (maxSlots <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSlots));
        }
        this._maxSlots = maxSlots;
    }

    public Int32 MaxSlots => this._maxSlots;

    // Ensures the session has a slot assignment. Returns the slot index
    // (0..MaxSlots-1) or -1 if all slots are occupied.
    public Int32 Ensure(String sessionId)
    {
        if (String.IsNullOrEmpty(sessionId))
        {
            return -1;
        }

        lock (this._lock)
        {
            if (this._sessionToSlot.TryGetValue(sessionId, out var existing))
            {
                return existing;
            }

            for (var i = 0; i < this._maxSlots; i++)
            {
                if (!this._slotToSession.ContainsKey(i))
                {
                    this._sessionToSlot[sessionId] = i;
                    this._slotToSession[i] = sessionId;
                    return i;
                }
            }

            return -1;
        }
    }

    // Removes the session's slot assignment. Returns the freed slot
    // index or -1 if the session was not assigned.
    public Int32 Release(String sessionId)
    {
        if (String.IsNullOrEmpty(sessionId))
        {
            return -1;
        }

        lock (this._lock)
        {
            if (this._sessionToSlot.Remove(sessionId, out var slot))
            {
                this._slotToSession.Remove(slot);
                return slot;
            }
            return -1;
        }
    }

    public String? GetSessionAt(Int32 slot)
    {
        lock (this._lock)
        {
            return this._slotToSession.TryGetValue(slot, out var sid) ? sid : null;
        }
    }
}
