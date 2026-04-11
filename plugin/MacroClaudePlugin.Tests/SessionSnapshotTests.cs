using System;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

public sealed class SessionSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 11, 12, 0, 0, TimeSpan.Zero);

    private static SessionSnapshot Build(
        SessionState state,
        DateTimeOffset? turnStartedAt = null,
        DateTimeOffset? idleSince = null,
        String displayName = "",
        String cwd = "/Users/lex/git/github.com/lexfrei/macro-claude",
        String repoName = "")
        => new(
            SessionId: "session-uuid-0123",
            Pid: 42,
            Cwd: cwd,
            DisplayName: displayName,
            State: state,
            TurnStartedAt: turnStartedAt,
            IdleSince: idleSince,
            UpdatedAt: Now,
            RepoName: repoName);

    // ------------------------------------------------------------------
    // Elapsed
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(SessionState.Working)]
    [InlineData(SessionState.Thinking)]
    [InlineData(SessionState.Stuck)]
    public void Elapsed_For_Running_States_Uses_TurnStartedAt(SessionState state)
    {
        var started = Now.AddMinutes(-5);
        var snapshot = Build(state, turnStartedAt: started);

        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Elapsed);
    }

    [Theory]
    [InlineData(SessionState.Working)]
    [InlineData(SessionState.Thinking)]
    [InlineData(SessionState.Stuck)]
    public void Elapsed_For_Running_States_Is_Null_Without_TurnStartedAt(SessionState state)
    {
        var snapshot = Build(state);

        Assert.Null(snapshot.Elapsed);
    }

    [Fact]
    public void Elapsed_For_Idle_Uses_IdleSince()
    {
        var idleSince = Now.AddSeconds(-123);
        var snapshot = Build(SessionState.Idle, idleSince: idleSince);

        Assert.Equal(TimeSpan.FromSeconds(123), snapshot.Elapsed);
    }

    [Fact]
    public void Elapsed_For_Idle_Is_Null_Without_IdleSince()
    {
        var snapshot = Build(SessionState.Idle);

        Assert.Null(snapshot.Elapsed);
    }

    [Theory]
    [InlineData(SessionState.Gone)]
    [InlineData(SessionState.Error)]
    public void Elapsed_For_Terminal_States_Is_Null(SessionState state)
    {
        var snapshot = Build(
            state,
            turnStartedAt: Now.AddMinutes(-5),
            idleSince: Now.AddMinutes(-5));

        Assert.Null(snapshot.Elapsed);
    }

    // ------------------------------------------------------------------
    // ShortName
    // ------------------------------------------------------------------

    [Fact]
    public void ShortName_Prefers_DisplayName_When_Present()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "my-named-session",
            cwd: "/Users/lex/git/github.com/lexfrei/macro-claude");

        Assert.Equal("my-named-session", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Falls_Back_To_Last_Cwd_Component()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "",
            cwd: "/Users/lex/git/github.com/lexfrei/macro-claude");

        Assert.Equal("macro-claude", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Ignores_Trailing_Slash_On_Cwd()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "",
            cwd: "/Users/lex/Documents/");

        Assert.Equal("Documents", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Falls_Back_To_Session_Id_Prefix_When_Cwd_Is_Empty()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "",
            cwd: "");

        Assert.Equal("session-", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_For_Whitespace_DisplayName_Falls_Back_To_Cwd()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "   ",
            cwd: "/tmp/test-session");

        Assert.Equal("test-session", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Prefers_RepoName_Over_DisplayName()
    {
        // Emulates a git worktree: StatusReader.Emit resolves the main
        // repo name from cwd's .git file and stuffs it into RepoName,
        // while DisplayName still holds the branch name from
        // sessions/<pid>.json. The user wants to see the repo, not the
        // branch.
        var snapshot = Build(
            SessionState.Idle,
            displayName: "passthrough-auth-refactor",
            cwd: "/Users/lex/git/github.com/lexfrei/cozytempl-wt/passthrough-auth-refactor",
            repoName: "cozytempl");

        Assert.Equal("cozytempl", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Prefers_RepoName_Over_Cwd_Basename()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "",
            cwd: "/Users/lex/git/github.com/lexfrei/cozytempl-wt/feature-x",
            repoName: "cozytempl");

        Assert.Equal("cozytempl", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Falls_Back_To_DisplayName_When_RepoName_Is_Empty()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "my-named-session",
            cwd: "/Users/lex/git/github.com/lexfrei/macro-claude",
            repoName: "");

        Assert.Equal("my-named-session", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Falls_Back_To_Cwd_Basename_When_RepoName_And_DisplayName_Empty()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "",
            cwd: "/Users/lex/git/github.com/lexfrei/macro-claude",
            repoName: "");

        Assert.Equal("macro-claude", snapshot.ShortName);
    }

    [Fact]
    public void ShortName_Ignores_Whitespace_RepoName()
    {
        var snapshot = Build(
            SessionState.Idle,
            displayName: "branch-name",
            cwd: "/tmp/foo",
            repoName: "   ");

        Assert.Equal("branch-name", snapshot.ShortName);
    }
}
