using System;
using System.Collections.Generic;
using System.Linq;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

public sealed class OrphanStatusDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 13, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);
    private static readonly String[] ExpectedAlpha = ["alpha"];
    private static readonly String[] ExpectedOk = ["ok"];
    private static readonly String[] ExpectedOrphan = ["orphan"];

    private static OrphanCandidate Candidate(
        String sessionId,
        Int32 pid = 0,
        Boolean hasStatusFile = true,
        TimeSpan? heartbeatAge = null,
        TimeSpan? jsonlAge = null)
    {
        DateTimeOffset? heartbeat = heartbeatAge is null ? null : Now - heartbeatAge.Value;
        DateTimeOffset? jsonl = jsonlAge is null ? null : Now - jsonlAge.Value;
        return new OrphanCandidate(sessionId, pid, hasStatusFile, heartbeat, jsonl);
    }

    [Fact]
    public void Session_With_Nonzero_Pid_Is_Not_Reaped()
    {
        // ReapDeadPidSessions handles live/dead PIDs; this path is only
        // for the orphan-Pid=0 case left behind by a hard reboot.
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate("alpha", pid: 1234, heartbeatAge: TimeSpan.FromHours(1))],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_Without_Status_File_Flag_Is_Not_Reaped()
    {
        // Accumulator created from sessions/<pid>.json before the hook
        // fired. Reaping it would kill a live session on the next tick.
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate("alpha", hasStatusFile: false, heartbeatAge: TimeSpan.FromHours(1))],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_Without_Heartbeat_Or_Jsonl_Is_Not_Reaped()
    {
        // No timing signal at all. Safer to leave it for
        // ReconcileSessionStatusDirectory to pick up once the status
        // file disappears from disk.
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate("alpha", heartbeatAge: null, jsonlAge: null)],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_With_Fresh_Jsonl_But_Stale_Heartbeat_Is_Not_Reaped()
    {
        // Regression guard: the hook script can be uninstalled or die
        // mid-session while Claude Code keeps streaming output into the
        // transcript. StatusReader.Emit treats max(hook, jsonl) as
        // liveness, so the reaper must follow the same rule to avoid
        // deleting a still-live session.
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate(
                "alpha",
                heartbeatAge: TimeSpan.FromHours(4),
                jsonlAge: TimeSpan.FromSeconds(30))],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_With_Stale_Heartbeat_And_No_Jsonl_Is_Reaped()
    {
        // The classic hard-reboot orphan: status file on disk, no
        // sessions/<pid>.json, no transcript activity.
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate(
                "alpha",
                heartbeatAge: Threshold + TimeSpan.FromSeconds(1),
                jsonlAge: null)],
            Now,
            Threshold).ToList();

        Assert.Equal(ExpectedAlpha, result);
    }

    [Fact]
    public void Session_With_Stale_Heartbeat_And_Stale_Jsonl_Is_Reaped()
    {
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate(
                "alpha",
                heartbeatAge: TimeSpan.FromHours(4),
                jsonlAge: TimeSpan.FromHours(3))],
            Now,
            Threshold).ToList();

        Assert.Equal(ExpectedAlpha, result);
    }

    [Fact]
    public void Session_With_Only_Fresh_Jsonl_Is_Not_Reaped()
    {
        // Hook never fired but transcript is active — a live session.
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate(
                "alpha",
                heartbeatAge: null,
                jsonlAge: TimeSpan.FromSeconds(10))],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_With_Fresh_Heartbeat_Is_Not_Reaped()
    {
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate("alpha", heartbeatAge: TimeSpan.FromMinutes(1))],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_At_Exact_Threshold_Is_Not_Reaped()
    {
        // Strict > comparison: a session whose heartbeat is exactly at
        // the threshold is still considered recent.
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate("alpha", heartbeatAge: Threshold)],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Session_With_Stale_Heartbeat_Is_Reaped()
    {
        var result = OrphanStatusDecision.SessionsToReap(
            [Candidate("alpha", heartbeatAge: Threshold + TimeSpan.FromSeconds(1))],
            Now,
            Threshold).ToList();

        Assert.Equal(ExpectedAlpha, result);
    }

    [Fact]
    public void Mixed_List_Reaps_Only_Eligible_Sessions()
    {
        var candidates = new List<OrphanCandidate>
        {
            Candidate("orphan", heartbeatAge: TimeSpan.FromHours(4)),
            Candidate("live", pid: 9999, heartbeatAge: TimeSpan.FromHours(4)),
            Candidate("fresh-accumulator", hasStatusFile: false, heartbeatAge: TimeSpan.FromHours(4)),
            Candidate("just-started", heartbeatAge: TimeSpan.FromSeconds(30)),
            Candidate("no-signals", heartbeatAge: null, jsonlAge: null),
            Candidate(
                "jsonl-keeps-alive",
                heartbeatAge: TimeSpan.FromHours(4),
                jsonlAge: TimeSpan.FromSeconds(5)),
        };

        var result = OrphanStatusDecision.SessionsToReap(candidates, Now, Threshold).ToList();

        Assert.Equal(ExpectedOrphan, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_Or_Null_Session_Id_Is_Ignored(String? bad)
    {
        var candidates = new List<OrphanCandidate>
        {
            Candidate(bad!, heartbeatAge: TimeSpan.FromHours(1)),
            Candidate("ok", heartbeatAge: TimeSpan.FromHours(1)),
        };

        var result = OrphanStatusDecision.SessionsToReap(candidates, Now, Threshold).ToList();

        Assert.Equal(ExpectedOk, result);
    }

    [Fact]
    public void Empty_Input_Returns_Empty()
    {
        var result = OrphanStatusDecision.SessionsToReap(
            [],
            Now,
            Threshold).ToList();

        Assert.Empty(result);
    }

    // LatestOf is exposed as internal so the reaper and
    // StatusReader.Emit share one liveness definition; these direct
    // tests pin the contract so a drift in either caller is caught.
    [Fact]
    public void LatestOf_Returns_Null_When_Both_Inputs_Null()
    {
        Assert.Null(OrphanStatusDecision.LatestOf(null, null));
    }

    [Fact]
    public void LatestOf_Returns_Right_When_Left_Null()
    {
        var right = new DateTimeOffset(2026, 4, 13, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(right, OrphanStatusDecision.LatestOf(null, right));
    }

    [Fact]
    public void LatestOf_Returns_Left_When_Right_Null()
    {
        var left = new DateTimeOffset(2026, 4, 13, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(left, OrphanStatusDecision.LatestOf(left, null));
    }

    [Fact]
    public void LatestOf_Picks_Strictly_Greater_Value()
    {
        var earlier = new DateTimeOffset(2026, 4, 13, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 4, 13, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(later, OrphanStatusDecision.LatestOf(earlier, later));
        Assert.Equal(later, OrphanStatusDecision.LatestOf(later, earlier));
    }

    [Fact]
    public void LatestOf_Returns_Either_When_Inputs_Equal()
    {
        var shared = new DateTimeOffset(2026, 4, 13, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(shared, OrphanStatusDecision.LatestOf(shared, shared));
    }
}
