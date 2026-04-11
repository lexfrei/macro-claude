using System;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

// Pure function under test: StateResolver.Determine.
// These tests lock the resolution rules so any future tuning of the
// heartbeat windows or CPU thresholds is an intentional, reviewed change.
public sealed class StateResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InterruptedMarker_Wins_Over_Every_Other_Signal()
    {
        var state = StateResolver.Determine(
            lastEvent: "UserPromptSubmit",
            heartbeatAt: Now,
            cpuPercent: 95.0,
            interruptedMarker: true,
            now: Now);

        Assert.Equal(SessionState.Error, state);
    }

    [Fact]
    public void StopFailure_Hook_Maps_To_Error()
    {
        var state = StateResolver.Determine(
            lastEvent: "StopFailure",
            heartbeatAt: Now,
            cpuPercent: 0.0,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Error, state);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("SessionStart")]
    [InlineData(null)]
    public void Terminal_And_Start_Events_Map_To_Idle(String? lastEvent)
    {
        var state = StateResolver.Determine(
            lastEvent: lastEvent,
            heartbeatAt: Now,
            cpuPercent: 95.0,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Idle, state);
    }

    [Theory]
    [InlineData("SessionEnd")]
    [InlineData("Notification")]
    [InlineData("CwdChanged")]
    public void Unknown_Events_Map_To_Idle(String lastEvent)
    {
        var state = StateResolver.Determine(
            lastEvent: lastEvent,
            heartbeatAt: Now,
            cpuPercent: 95.0,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Idle, state);
    }

    [Theory]
    [InlineData("UserPromptSubmit")]
    [InlineData("PreToolUse")]
    [InlineData("PostToolUse")]
    public void Fresh_Heartbeat_Maps_Working_Events_To_Working(String lastEvent)
    {
        var heartbeat = Now.AddSeconds(-1);

        var state = StateResolver.Determine(
            lastEvent: lastEvent,
            heartbeatAt: heartbeat,
            cpuPercent: 0.0,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Working, state);
    }

    [Fact]
    public void Stale_Heartbeat_With_Idle_Cpu_Maps_To_Stuck()
    {
        var heartbeat = Now.AddSeconds(-60);

        var state = StateResolver.Determine(
            lastEvent: "PreToolUse",
            heartbeatAt: heartbeat,
            cpuPercent: 0.1,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Stuck, state);
    }

    [Fact]
    public void Stale_Heartbeat_With_Active_Cpu_Maps_To_Thinking()
    {
        var heartbeat = Now.AddSeconds(-60);

        var state = StateResolver.Determine(
            lastEvent: "PreToolUse",
            heartbeatAt: heartbeat,
            cpuPercent: 12.0,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Thinking, state);
    }

    [Fact]
    public void Middle_Band_Heartbeat_With_Low_Cpu_Maps_To_Stuck()
    {
        // 10s heartbeat age — between Fresh (3s) and Stale (30s) — and CPU
        // in the middle band (neither obviously idle nor active).
        var heartbeat = Now.AddSeconds(-10);

        var state = StateResolver.Determine(
            lastEvent: "PreToolUse",
            heartbeatAt: heartbeat,
            cpuPercent: 0.8,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Stuck, state);
    }

    [Fact]
    public void Middle_Band_Heartbeat_With_Active_Cpu_Maps_To_Thinking()
    {
        var heartbeat = Now.AddSeconds(-10);

        var state = StateResolver.Determine(
            lastEvent: "PreToolUse",
            heartbeatAt: heartbeat,
            cpuPercent: 50.0,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Thinking, state);
    }

    [Fact]
    public void No_Heartbeat_Ever_Maps_To_Stuck_For_Working_Events()
    {
        // heartbeatAt == null means TimeSpan.MaxValue age, so it should
        // short-circuit to stuck.
        var state = StateResolver.Determine(
            lastEvent: "PreToolUse",
            heartbeatAt: null,
            cpuPercent: 0.0,
            interruptedMarker: false,
            now: Now);

        Assert.Equal(SessionState.Stuck, state);
    }
}
