using System;
using System.Collections.Generic;
using System.Linq;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

public sealed class ReconcileDecisionTests
{
    private static readonly String[] EmptyIds = [];
    private static readonly String[] OnlyAlpha = ["alpha"];
    private static readonly String[] OnlyFresh = ["fresh"];
    private static readonly String[] OnlyNewcomer = ["newcomer"];
    private static readonly String[] Mixed =
    [
        "alive-on-disk",
        "dead-had-file",
        "fresh-no-file",
        "dead-never-had-file",
    ];
    private static readonly String[] ExpectedAlpha = ["alpha"];
    private static readonly String[] ExpectedDeadHadFile = ["dead-had-file"];
    private static readonly String[] ExpectedOk = ["ok"];

    private static HashSet<String> OnDisk(params String[] ids)
        => new(ids, StringComparer.Ordinal);

    private static Dictionary<String, Boolean> Flags(params (String Id, Boolean HasFile)[] entries)
    {
        var dict = new Dictionary<String, Boolean>(StringComparer.Ordinal);
        foreach (var (id, hasFile) in entries)
        {
            dict[id] = hasFile;
        }
        return dict;
    }

    [Fact]
    public void Session_Still_On_Disk_Is_Not_Reaped()
    {
        var result = ReconcileDecision.SessionsToReap(
            currentSessionIds: OnlyAlpha,
            hasStatusFile: Flags(("alpha", true)),
            onDisk: OnDisk("alpha")).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_With_Missing_File_And_Flag_Is_Reaped()
    {
        var result = ReconcileDecision.SessionsToReap(
            currentSessionIds: OnlyAlpha,
            hasStatusFile: Flags(("alpha", true)),
            onDisk: OnDisk()).ToList();

        Assert.Equal(ExpectedAlpha, result);
    }

    // The regression guard: a session observed through sessions/<pid>.json
    // before the hook ever fired must stay in memory across a reconcile
    // sweep, otherwise iTerm2 / new sessions flicker off the macropad for
    // the first second of their life.
    [Fact]
    public void Session_Without_Status_File_Yet_Is_Preserved()
    {
        var result = ReconcileDecision.SessionsToReap(
            currentSessionIds: OnlyFresh,
            hasStatusFile: Flags(("fresh", false)),
            onDisk: OnDisk()).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_Not_In_Flags_Map_Is_Preserved()
    {
        // The flags map can be incomplete if an accumulator has been
        // created but not yet inspected — treat it as "never had a
        // file" rather than crashing or reaping.
        var result = ReconcileDecision.SessionsToReap(
            currentSessionIds: OnlyNewcomer,
            hasStatusFile: Flags(),
            onDisk: OnDisk()).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Multiple_Sessions_Mixed_State_Reaps_Only_Eligible()
    {
        var result = ReconcileDecision.SessionsToReap(
            currentSessionIds: Mixed,
            hasStatusFile: Flags(
                ("alive-on-disk", true),
                ("dead-had-file", true),
                ("fresh-no-file", false),
                ("dead-never-had-file", false)),
            onDisk: OnDisk("alive-on-disk")).ToList();

        Assert.Equal(ExpectedDeadHadFile, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_Or_Null_Session_Id_Is_Ignored(String? bad)
    {
        var input = new[] { bad!, "ok" };
        var result = ReconcileDecision.SessionsToReap(
            currentSessionIds: input,
            hasStatusFile: Flags(("ok", true)),
            onDisk: OnDisk()).ToList();

        Assert.Equal(ExpectedOk, result);
    }

    [Fact]
    public void Empty_Input_Returns_Empty()
    {
        var result = ReconcileDecision.SessionsToReap(
            currentSessionIds: EmptyIds,
            hasStatusFile: Flags(),
            onDisk: OnDisk()).ToList();

        Assert.Empty(result);
    }
}
