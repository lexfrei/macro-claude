using System;
using System.Threading;

using Loupedeck.MacroClaudePlugin.Focus;
using Loupedeck.MacroClaudePlugin.Status;

namespace Loupedeck.MacroClaudePlugin.Actions;

// Meta-widget: a single button that focuses the most recently
// idled Claude Code session. Not bound to a fixed slot — it
// dynamically picks the best target on every press.
//
// Three visual states on the bitmap:
//   ⏎ green  — idle target available, press to jump there
//   ▶ blue   — all sessions running, nothing idle to focus
//   · grey   — no sessions at all
//
// The text label (GetCommandDisplayName) shows "→ <ShortName>"
// when a target is available, or "Claude Code" otherwise.
//
// Selection logic lives in QuickFocusSelector (under Status/)
// so it can be linked-sourced into the test project without
// dragging in Loupedeck build targets.
public sealed class QuickFocusCommand : PluginDynamicCommand
{
    private static readonly BitmapColor Background = new(24, 24, 26);
    private static readonly BitmapColor MutedColor = new(140, 140, 150);
    private static readonly BitmapColor IdleAccent = new(90, 210, 130);
    private static readonly BitmapColor WorkingAccent = new(100, 160, 255);

    public QuickFocusCommand()
        : base(
            displayName: "Claude: Quick Focus",
            description: "Focus the most recently idled Claude Code session",
            groupName: "Claude")
    {
        SlotBus.SlotChanged += this.OnSlotChanged;
        PluginLog.Info("macro-claude: QuickFocusCommand created");
    }

    private void OnSlotChanged(Int32 slot, SessionSnapshot? snapshot) =>
        this.ActionImageChanged();

    protected override void RunCommand(String actionParameter)
    {
        var target = QuickFocusSelector.FindMostRecentlyIdled();
        if (target is null)
        {
            PluginLog.Info("macro-claude: QuickFocus — no idle session to focus");
            return;
        }

        PluginLog.Info(
            $"macro-claude: QuickFocus → {target.ShortName} (pid {target.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

        _ = FocusDispatcher
            .FocusAsync(target.Pid, target.Cwd, CancellationToken.None)
            .ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception is { } ex)
                    {
                        PluginLog.Error(ex, $"macro-claude: QuickFocus failed for pid {target.Pid}");
                    }
                    else
                    {
                        PluginLog.Info($"macro-claude: QuickFocus result: {t.Result}");
                    }
                },
                System.Threading.Tasks.TaskScheduler.Default);
    }

    protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
    {
        var target = QuickFocusSelector.FindMostRecentlyIdled();
        var hasAnySessions = QuickFocusSelector.HasAnySessions();

        using var builder = new BitmapBuilder(imageSize);
        builder.Clear(Background);

        var w = builder.Width;
        var h = builder.Height;
        var twentieth = h / 20;
        var barH = Math.Max(3, twentieth);
        var glyphSize = (Int32)((Double)h * 0.66);
        var glyphY = barH;
        var glyphH = h - barH;

        String glyph;
        BitmapColor accent;

        if (target is not null)
        {
            glyph = "⏎";
            accent = IdleAccent;
        }
        else if (hasAnySessions)
        {
            glyph = "▶";
            accent = WorkingAccent;
        }
        else
        {
            glyph = "·";
            accent = MutedColor;
        }

        builder.FillRectangle(0, 0, w, barH, accent);
        builder.DrawText(
            text: glyph,
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

    protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
    {
        var target = QuickFocusSelector.FindMostRecentlyIdled();
        if (target is null)
        {
            return "Claude Code";
        }
        return $"→ {target.ShortName}{Environment.NewLine}{FormatElapsed(target.Elapsed)}";
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
}
