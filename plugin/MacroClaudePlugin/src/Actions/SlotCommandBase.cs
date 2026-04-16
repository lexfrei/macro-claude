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
        return snapshot is null ? DrawEmpty(imageSize) : DrawSlot(snapshot, imageSize);
    }

    // The text label that Logi Options+ renders UNDER the button
    // bitmap. We carry only the repo name and elapsed time here —
    // no state mark — because the state is already conveyed by the
    // glyph on the bitmap itself. Duplicating the state symbol in
    // both places produced a visually noisy "▶ macro-claude" row
    // right below a big ▶ icon.
    protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
    {
        var snapshot = SlotBus.TryGetSnapshot(this.SlotIndex);
        if (snapshot is null)
        {
            return "—";
        }
        return $"{snapshot.ShortName}{Environment.NewLine}{FormatElapsed(snapshot.Elapsed)}";
    }

    // `snapshot` is unused here: the published snapshot is already
    // in SlotBus (Publish writes it before raising SlotChanged), and
    // GetCommandImage / GetCommandDisplayName read it from there. The
    // parameter is part of the Action<Int32, SessionSnapshot?>
    // delegate signature SlotBus exposes, so we can't drop it.
    //
    // No verbose log either: OnSessionUpdated already logs
    // (suppressed to state transitions by SessionLogDecision), and
    // this handler fires at 1 Hz per active slot to keep the
    // elapsed-time counter on the macropad label ticking. Logging
    // from here would flood the log with one line per slot per
    // second in steady state.
    private void OnSlotChanged(Int32 slot, SessionSnapshot? snapshot)
    {
        _ = snapshot;
        if (slot != this.SlotIndex)
        {
            return;
        }
        this.ActionImageChanged();
    }

    // Button bitmap layout (works at both 60x60 and 90x90 MXCC sizes):
    //
    //   ┌──────────────────┐
    //   │ ████████████████ │  thin accent bar — state colour
    //   │                  │
    //   │                  │
    //   │        ●         │  large centred state glyph — state colour
    //   │                  │
    //   │                  │
    //   └──────────────────┘
    //
    // Dark background, one glyph, one colour strip, nothing else.
    // Text (repo name + elapsed time) is delivered via
    // GetCommandDisplayName, which Logi Options+ renders as a label
    // underneath the button. Putting the text on the bitmap AND in
    // the label duplicated everything and made the icon fight the
    // label for attention.
    private static readonly BitmapColor Background = new(24, 24, 26);
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

        // Precomputed layout constants — named intermediates sidestep
        // the SA1407 / IDE0047 precedence flip-flop between the two
        // analyzer packs when used inside expressions below.
        var twentieth = h / 20;
        var barH = Math.Max(3, twentieth);
        var glyphSize = (Int32)((Double)h * 0.66);
        var glyphY = barH;
        var glyphH = h - barH;

        // Thin accent bar at the top — state colour.
        builder.FillRectangle(0, 0, w, barH, accent);

        // One big centred glyph filling most of the remaining area.
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

        return builder.ToImage();
    }

    private static BitmapColor AccentFor(SessionState state) => state switch
    {
        SessionState.Gone => new BitmapColor(90, 90, 95),        // muted grey
        SessionState.Idle => new BitmapColor(90, 210, 130),      // soft green
        SessionState.Working => new BitmapColor(100, 160, 255),  // bright blue
        SessionState.Thinking => new BitmapColor(120, 220, 230), // cyan
        SessionState.Stuck => new BitmapColor(255, 170, 60),     // amber
        SessionState.Error => new BitmapColor(240, 90, 90),      // red
        SessionState.Waiting => new BitmapColor(200, 150, 255),  // lavender
        _ => new BitmapColor(90, 90, 95),
    };

    // Single-glyph state icon, legible at 38% button height. Each
    // glyph is in the core Unicode planes that the Loupedeck default
    // font renders correctly on MXCC.
    private static String GlyphFor(SessionState state) => state switch
    {
        SessionState.Idle => "●",       // solid circle
        SessionState.Working => "▶",    // right-pointing triangle
        SessionState.Thinking => "⋯",   // horizontal ellipsis
        SessionState.Stuck => "‼",      // double exclamation
        SessionState.Error => "✗",      // heavy x
        SessionState.Waiting => "?",    // question mark — needs user input
        SessionState.Gone => "·",       // middle dot
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
