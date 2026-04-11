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

    protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
    {
        var slot = ParameterToSlot(actionParameter);
        if (slot < 0 || !this._snapshotsBySlot.TryGetValue(slot, out var snapshot))
        {
            return DrawEmpty(imageSize);
        }
        return DrawSlot(snapshot, imageSize);
    }

    // Text fallback used in the command palette / tooltips where bitmaps
    // are not shown. Mirrors the image content but as a plain string.
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

        return $"{mark} {snapshot.ShortName}{Environment.NewLine}{FormatElapsed(snapshot.Elapsed)}";
    }

    private static BitmapImage DrawEmpty(PluginImageSize imageSize)
    {
        using var builder = new BitmapBuilder(imageSize);
        builder.Clear(new BitmapColor(30, 30, 30));
        builder.DrawText("—", BitmapColor.White);
        return builder.ToImage();
    }

    private static BitmapImage DrawSlot(SessionSnapshot snapshot, PluginImageSize imageSize)
    {
        using var builder = new BitmapBuilder(imageSize);
        builder.Clear(BackgroundFor(snapshot.State));
        var text = $"{LabelFor(snapshot.State)}{Environment.NewLine}{snapshot.ShortName}{Environment.NewLine}{FormatElapsed(snapshot.Elapsed)}";
        builder.DrawText(text, BitmapColor.White);
        return builder.ToImage();
    }

    private static BitmapColor BackgroundFor(SessionState state) => state switch
    {
        SessionState.Gone => new BitmapColor(30, 30, 30),       // dark gray
        SessionState.Idle => new BitmapColor(30, 150, 70),      // green
        SessionState.Working => new BitmapColor(30, 100, 200),  // blue
        SessionState.Thinking => new BitmapColor(30, 180, 200), // cyan
        SessionState.Stuck => new BitmapColor(220, 120, 30),    // orange
        SessionState.Error => new BitmapColor(200, 50, 50),     // red
        _ => new BitmapColor(30, 30, 30),
    };

    private static String LabelFor(SessionState state) => state switch
    {
        SessionState.Idle => "IDLE",
        SessionState.Working => "WORK",
        SessionState.Thinking => "THINK",
        SessionState.Stuck => "STUCK",
        SessionState.Error => "ERROR",
        SessionState.Gone => "GONE",
        _ => "?",
    };

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
