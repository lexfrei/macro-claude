using System;
using System.Collections.Concurrent;
using System.Threading;

using Loupedeck.MacroClaudePlugin.Focus;
using Loupedeck.MacroClaudePlugin.Status;

namespace Loupedeck.MacroClaudePlugin.Actions;

// Parameterized dynamic command: one command, N action parameters
// (slot0..slot8). Each parameter renders the state of whatever session
// has been assigned to that slot by SlotAssigner.
public class SessionStatusCommand : PluginDynamicCommand
{
    private const Int32 MaxSlots = 9;
    private const String SlotPrefix = "slot";

    private readonly ConcurrentDictionary<Int32, SessionSnapshot> _snapshotsBySlot = new();

    public SessionStatusCommand()
        : base(displayName: "Claude Session", description: "Show Claude Code session status", groupName: "Claude")
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            this.AddParameter($"{SlotPrefix}{i}", $"Slot {i + 1}", "Claude");
        }

        SlotBus.SlotChanged += this.OnSlotChanged;
    }

    private void OnSlotChanged(Int32 slot, SessionSnapshot? snapshot)
    {
        if (slot is < 0 or >= MaxSlots)
        {
            return;
        }

        if (snapshot is null)
        {
            this._snapshotsBySlot.TryRemove(slot, out _);
        }
        else
        {
            this._snapshotsBySlot[slot] = snapshot;
        }

        this.ActionImageChanged(SlotToParameter(slot));
    }

    protected override void RunCommand(String actionParameter)
    {
        var slot = ParameterToSlot(actionParameter);
        if (slot < 0 || !this._snapshotsBySlot.TryGetValue(slot, out var snapshot))
        {
            return;
        }

        PluginLog.Info($"focus requested for slot {slot} session {snapshot.SessionId} pid {snapshot.Pid}");

        // Fire-and-forget — the Loupedeck runtime expects RunCommand to
        // return immediately, and focus dispatch is best-effort.
        _ = FocusDispatcher
            .FocusAsync(snapshot.Pid, snapshot.Cwd, CancellationToken.None)
            .ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception is { } ex)
                    {
                        PluginLog.Error(ex, $"focus failed for pid {snapshot.Pid}");
                    }
                    else
                    {
                        PluginLog.Info($"focus result for pid {snapshot.Pid}: {t.Result}");
                    }
                },
                System.Threading.Tasks.TaskScheduler.Default);
    }

    protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
    {
        var slot = ParameterToSlot(actionParameter);
        if (slot < 0 || !this._snapshotsBySlot.TryGetValue(slot, out var snapshot))
        {
            return "—";
        }

        var mark = snapshot.State switch
        {
            SessionState.Idle => "✓",
            SessionState.Working => "▶",
            SessionState.Thinking => "~",
            SessionState.Stuck => "!",
            SessionState.Error => "✗",
            SessionState.Gone => "·",
            _ => "?",
        };

        var elapsed = FormatElapsed(snapshot.Elapsed);
        return $"{mark} {snapshot.ShortName}{Environment.NewLine}{elapsed}";
    }

    private static String FormatElapsed(TimeSpan? duration)
    {
        if (duration is not { } d)
        {
            return "--:--";
        }

        if (d.TotalHours >= 1)
        {
            return $"{(Int32)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}";
        }

        return $"{d.Minutes:00}:{d.Seconds:00}";
    }

    private static String SlotToParameter(Int32 slot) => $"{SlotPrefix}{slot}";

    private static Int32 ParameterToSlot(String actionParameter)
    {
        if (String.IsNullOrEmpty(actionParameter))
        {
            return -1;
        }
        if (!actionParameter.StartsWith(SlotPrefix, StringComparison.Ordinal))
        {
            return -1;
        }
        var numberPart = actionParameter.AsSpan(SlotPrefix.Length);
        if (!Int32.TryParse(numberPart, out var n))
        {
            return -1;
        }
        if (n is < 0 or >= MaxSlots)
        {
            return -1;
        }
        return n;
    }
}
