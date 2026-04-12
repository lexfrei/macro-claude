using System;

namespace Loupedeck.MacroClaudePlugin.Status;

// Pure selection logic for the QuickFocus meta-widget. Scans
// SlotBus for the idle session with the most recent IdleSince
// timestamp. Extracted from the command class so it can be
// linked-sourced into the test project without dragging in
// Loupedeck build targets.
internal static class QuickFocusSelector
{
    // Returns the idle snapshot with the most recent IdleSince,
    // or null if no idle sessions exist.
    public static SessionSnapshot? FindMostRecentlyIdled()
    {
        SessionSnapshot? best = null;
        var bestIdle = DateTimeOffset.MinValue;

        for (var i = 0; i < SlotBus.ValidSlotCount; i++)
        {
            var snap = SlotBus.TryGetSnapshot(i);
            if (snap is null || snap.State != SessionState.Idle)
            {
                continue;
            }
            var snapIdle = snap.IdleSince ?? DateTimeOffset.MinValue;
            if (best is null || snapIdle > bestIdle)
            {
                best = snap;
                bestIdle = snapIdle;
            }
        }
        return best;
    }

    // Returns true if at least one slot has a non-null snapshot.
    public static Boolean HasAnySessions()
    {
        for (var i = 0; i < SlotBus.ValidSlotCount; i++)
        {
            if (SlotBus.TryGetSnapshot(i) is not null)
            {
                return true;
            }
        }
        return false;
    }
}
