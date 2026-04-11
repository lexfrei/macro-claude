using System;
using System.Threading;

using Loupedeck.MacroClaudePlugin.Focus;
using Loupedeck.MacroClaudePlugin.Status;

namespace Loupedeck.MacroClaudePlugin.Actions;

// One PluginDynamicCommand subclass per slot. This is the shape LPS
// actually exposes in the Logi Options+ action picker for MX Creative
// Keypad: each concrete command appears as its own draggable entry, so
// the user sees nine distinct "Claude Session N" buttons in the action
// list and drops each onto a physical key.
//
// We tried the obvious alternative first — a single PluginDynamicCommand
// with 27 AddParameter calls. That works on Loupedeck Live (the device
// the SDK was originally written for) where parameterized commands
// expand in the UI as a tree, but MXCC's Options+ UI collapses the
// parameters and shows only the top-level command. LPS then refuses to
// bind the button because no specific parameter was chosen, and the UI
// renders an unavailable-command triangle without ever calling
// GetCommandImage. Nine physical classes bypasses that entire mess.
//
// Why nine and not twenty-seven: one MXCC profile page holds nine keys.
// We ship the minimum that covers a single page; power users who want
// multi-page status walls can extend to eighteen or twenty-seven by
// adding more concrete ClaudeSlotNCommand files. The bus, assigner and
// resolver are already sized for it.
public abstract class SlotCommandBase : PluginDynamicCommand
{
    protected SlotCommandBase(Int32 displayNumber)
        : base(
            displayName: $"Claude Session {displayNumber}",
            description: $"Show Claude Code session {displayNumber} status",
            groupName: "Claude")
    {
        SlotBus.SlotChanged += this.OnSlotChanged;
        PluginLog.Info($"macro-claude: {this.GetType().Name} created for slot {this.SlotIndex}");
    }

    // Zero-based slot index this command is bound to. Each subclass pins
    // itself to a specific slot; the mapping from session_id to slot is
    // still dynamic and lives in SlotAssigner, so when a session ends
    // the next new session takes over the same slot and this command
    // transparently starts rendering the new session.
    protected abstract Int32 SlotIndex { get; }

    protected override void RunCommand(String actionParameter)
    {
        var snapshot = SlotBus.TryGetSnapshot(this.SlotIndex);
        if (snapshot is null)
        {
            PluginLog.Warning($"macro-claude: slot {this.SlotIndex} has no snapshot — nothing to focus");
            return;
        }

        PluginLog.Info($"focus requested for slot {this.SlotIndex} session {snapshot.SessionId} pid {snapshot.Pid}");

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
        var snapshot = SlotBus.TryGetSnapshot(this.SlotIndex);
        PluginLog.Verbose(
            $"macro-claude: {this.GetType().Name}.GetCommandImage slot={this.SlotIndex} snapshot={(snapshot is null ? "null" : snapshot.State.ToString())} storeSize={SlotBus.SnapshotCount}");
        return snapshot is null ? DrawEmpty(imageSize) : DrawSlot(snapshot, imageSize);
    }

    protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
    {
        var snapshot = SlotBus.TryGetSnapshot(this.SlotIndex);
        if (snapshot is null)
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

    private void OnSlotChanged(Int32 slot, SessionSnapshot? snapshot)
    {
        if (slot != this.SlotIndex)
        {
            return;
        }
        PluginLog.Verbose(
            $"macro-claude: slot {slot} changed → {this.GetType().Name}.ActionImageChanged (snapshot={(snapshot is null ? "cleared" : snapshot.SessionId)})");
        this.ActionImageChanged();
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
}
