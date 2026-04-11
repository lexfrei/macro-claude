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

    // Button layout (works at both 60x60 and 90x90 MXCC sizes):
    //
    //   ┌──────────────────┐
    //   │ ████████████████ │  4px accent bar — state colour
    //   │                  │
    //   │        ●         │  large state glyph — state colour
    //   │                  │
    //   │   macro-claude   │  project name — white
    //   │      00:42       │  elapsed time — muted grey
    //   └──────────────────┘
    //
    // Dark background, colour is used as an accent rather than a
    // wash, to avoid the "traffic light" look the user complained
    // about. The large glyph carries the state recognition at a
    // glance; accent bar confirms it with colour without drowning
    // the text.
    private static readonly BitmapColor Background = new(24, 24, 26);
    private static readonly BitmapColor NameColor = new(235, 235, 235);
    private static readonly BitmapColor MutedColor = new(140, 140, 150);

    private static BitmapImage DrawEmpty(PluginImageSize imageSize)
    {
        using var builder = new BitmapBuilder(imageSize);
        builder.Clear(Background);

        var w = builder.Width;
        var h = builder.Height;

        builder.DrawText(
            text: "·",
            x: 0,
            y: 0,
            width: w,
            height: h,
            color: MutedColor,
            fontSize: h / 2,
            lineHeight: 0,
            spaceHeight: 0,
            fontName: null);
        return builder.ToImage();
    }

    private static BitmapImage DrawSlot(SessionSnapshot snapshot, PluginImageSize imageSize)
    {
        using var builder = new BitmapBuilder(imageSize);
        builder.Clear(Background);

        var w = builder.Width;
        var h = builder.Height;
        var accent = AccentFor(snapshot.State);

        // Precomputed layout constants derived from button height.
        // Named intermediates sidestep the SA1407 / IDE0047 precedence
        // flip-flop between the two analyzer packs.
        var quarter = h / 4;
        var half = h / 2;
        var twentieth = h / 20;
        var barH = Math.Max(3, twentieth);
        var glyphSize = (Int32)((Double)h * 0.38);
        var glyphY = barH + 2;
        var glyphH = half - barH;
        var nameSize = Math.Max(10, h / 7);
        var nameY = half + 2;
        var nameH = quarter;
        var timeSize = Math.Max(9, h / 8);
        var timeY = h - quarter;
        var timeH = quarter - 2;
        var textX = 2;
        var textW = w - 4;

        // Top accent bar — state colour strip.
        builder.FillRectangle(0, 0, w, barH, accent);

        // Large state glyph, top third.
        builder.DrawText(
            text: GlyphFor(snapshot.State),
            x: 0,
            y: glyphY,
            width: w,
            height: glyphH,
            color: accent,
            fontSize: glyphSize,
            lineHeight: 0,
            spaceHeight: 0,
            fontName: null);

        // Project name, middle strip.
        builder.DrawText(
            text: TruncateForButton(snapshot.ShortName, w),
            x: textX,
            y: nameY,
            width: textW,
            height: nameH,
            color: NameColor,
            fontSize: nameSize,
            lineHeight: 0,
            spaceHeight: 0,
            fontName: null);

        // Elapsed time, bottom strip.
        builder.DrawText(
            text: FormatElapsed(snapshot.Elapsed),
            x: textX,
            y: timeY,
            width: textW,
            height: timeH,
            color: MutedColor,
            fontSize: timeSize,
            lineHeight: 0,
            spaceHeight: 0,
            fontName: null);

        return builder.ToImage();
    }

    // Trim the name to something that will not clip the button. The
    // SDK will squash text to fit, but very long names become a grey
    // smudge. Cap at 14 characters with an ellipsis; button widths
    // in the 60-90px range render up to ~10 characters comfortably
    // at the name font size we picked.
    private static String TruncateForButton(String name, Int32 width)
    {
        _ = width;
        return name.Length <= 14 ? name : name[..13] + "…";
    }

    private static BitmapColor AccentFor(SessionState state) => state switch
    {
        SessionState.Gone => new BitmapColor(90, 90, 95),        // muted grey
        SessionState.Idle => new BitmapColor(90, 210, 130),      // soft green
        SessionState.Working => new BitmapColor(100, 160, 255),  // bright blue
        SessionState.Thinking => new BitmapColor(120, 220, 230), // cyan
        SessionState.Stuck => new BitmapColor(255, 170, 60),     // amber
        SessionState.Error => new BitmapColor(240, 90, 90),      // red
        _ => new BitmapColor(90, 90, 95),
    };

    // Single-glyph state icon, legible at 38% button height. Each
    // glyph is in the core Unicode planes that the Loupedeck default
    // font renders correctly on MXCC.
    private static String GlyphFor(SessionState state) => state switch
    {
        SessionState.Idle => "●",      // solid circle
        SessionState.Working => "▶",   // right-pointing triangle
        SessionState.Thinking => "⋯",  // horizontal ellipsis
        SessionState.Stuck => "‼",     // double exclamation
        SessionState.Error => "✗",     // heavy x
        SessionState.Gone => "·",      // middle dot
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
