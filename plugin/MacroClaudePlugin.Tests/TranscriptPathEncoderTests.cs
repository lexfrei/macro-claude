using System;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

// Locks the encoding convention Claude Code uses to derive the
// per-project directory name under ~/.claude/projects from a cwd.
// The rule verified against real on-disk directories: every
// character outside [A-Za-z0-9_-] is replaced with '-'. Dots,
// slashes, spaces, unicode — all collapse to '-'. Dashes and
// underscores already present in the cwd survive as-is.
public sealed class TranscriptPathEncoderTests
{
    [Theory]
    [InlineData("/Users/lex", "-Users-lex")]
    [InlineData("/Users/lex/.claude", "-Users-lex--claude")]
    [InlineData(
        "/Users/lex/git/github.com/lexfrei/macro-claude",
        "-Users-lex-git-github-com-lexfrei-macro-claude")]
    [InlineData(
        "/Users/lex/git/github.com/aenix-org/ccc",
        "-Users-lex-git-github-com-aenix-org-ccc")]
    [InlineData("", "")]
    [InlineData("/", "-")]
    [InlineData("/a/b_c/d-e", "-a-b_c-d-e")]
    [InlineData("/path with spaces/x", "-path-with-spaces-x")]
    public void Encode_Replaces_NonAlnum_And_Preserves_Dash_Underscore(String cwd, String expected)
    {
        var actual = TranscriptPathEncoder.Encode(cwd);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Encode_Handles_Consecutive_Specials_As_Multiple_Dashes()
    {
        // Real-world case: user had a worktree at
        //   /Users/lex/git/.../.claude-worktrees/...
        // which Claude Code encoded to "...--claude-worktrees-..." —
        // two consecutive dashes coming from `/` + `.`. We lock this
        // behaviour so a future "collapse repeated dashes" pass does
        // not break lookups for existing on-disk directories.
        var actual = TranscriptPathEncoder.Encode("/a/.b");

        Assert.Equal("-a--b", actual);
    }

    [Fact]
    public void Encode_Null_Returns_Empty()
    {
        var actual = TranscriptPathEncoder.Encode(cwd: null);

        Assert.Equal(String.Empty, actual);
    }
}
