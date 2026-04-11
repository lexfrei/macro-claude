using System;

namespace Loupedeck.MacroClaudePlugin.Status;

// Process-wide event bus used to push slot updates from MacroClaudePlugin
// to SessionStatusCommand without resolving the command instance via the
// Loupedeck API (which does not expose a clean lookup for dynamic
// command instances from Plugin.Load).
//
// SessionStatusCommand subscribes in its constructor; MacroClaudePlugin
// publishes whenever StatusReader emits an update. Unsubscription happens
// when the command instance is disposed by the runtime — for a singleton
// plugin that is equivalent to process shutdown, so no leak window.
internal static class SlotBus
{
    public static event Action<Int32, SessionSnapshot?>? SlotChanged;

    public static void Publish(Int32 slot, SessionSnapshot? snapshot)
        => SlotChanged?.Invoke(slot, snapshot);
}
