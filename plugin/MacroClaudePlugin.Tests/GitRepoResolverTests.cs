using System;
using System.IO;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

public sealed class GitRepoResolverTests : IDisposable
{
    private readonly String _tempRoot;

    public GitRepoResolverTests()
    {
        this._tempRoot = Path.Combine(Path.GetTempPath(), "macro-claude-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this._tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Leave behind on test runner shutdown — best effort.
        }
    }

    [Fact]
    public void Plain_Repo_Returns_Cwd_Basename()
    {
        var repo = Path.Combine(this._tempRoot, "myproject");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal("myproject", GitRepoResolver.ResolveRepoName(repo));
    }

    [Fact]
    public void Plain_Repo_Handles_Trailing_Slash()
    {
        var repo = Path.Combine(this._tempRoot, "myproject");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal("myproject", GitRepoResolver.ResolveRepoName(repo + "/"));
    }

    [Fact]
    public void Worktree_Returns_Main_Repo_Basename()
    {
        // Set up layout:
        //   <tmp>/myrepo/.git/worktrees/feature-x/
        //   <tmp>/myrepo-wt/feature-x/.git  → "gitdir: <tmp>/myrepo/.git/worktrees/feature-x"
        var mainRepo = Path.Combine(this._tempRoot, "myrepo");
        Directory.CreateDirectory(Path.Combine(mainRepo, ".git", "worktrees", "feature-x"));

        var worktree = Path.Combine(this._tempRoot, "myrepo-wt", "feature-x");
        Directory.CreateDirectory(worktree);

        var worktreeGitFile = Path.Combine(worktree, ".git");
        var gitdirLine = $"gitdir: {mainRepo}/.git/worktrees/feature-x";
        File.WriteAllText(worktreeGitFile, gitdirLine + Environment.NewLine);

        Assert.Equal("myrepo", GitRepoResolver.ResolveRepoName(worktree));
    }

    [Fact]
    public void Worktree_With_Trailing_Whitespace_In_Git_File_Still_Resolves()
    {
        var mainRepo = Path.Combine(this._tempRoot, "alpha");
        Directory.CreateDirectory(Path.Combine(mainRepo, ".git", "worktrees", "bravo"));

        var worktree = Path.Combine(this._tempRoot, "alpha-worktrees", "bravo");
        Directory.CreateDirectory(worktree);

        File.WriteAllText(
            Path.Combine(worktree, ".git"),
            $"gitdir: {mainRepo}/.git/worktrees/bravo   \n\n");

        Assert.Equal("alpha", GitRepoResolver.ResolveRepoName(worktree));
    }

    [Fact]
    public void Malformed_Git_File_Falls_Back_To_Cwd_Basename()
    {
        var worktree = Path.Combine(this._tempRoot, "broken", "leaf");
        Directory.CreateDirectory(worktree);

        File.WriteAllText(Path.Combine(worktree, ".git"), "not a real gitdir line");

        Assert.Equal("leaf", GitRepoResolver.ResolveRepoName(worktree));
    }

    [Fact]
    public void Git_File_Without_Worktrees_Marker_Falls_Back_To_Cwd_Basename()
    {
        var dir = Path.Combine(this._tempRoot, "submodule-style");
        Directory.CreateDirectory(dir);

        File.WriteAllText(
            Path.Combine(dir, ".git"),
            "gitdir: /some/other/path/.git/modules/submodule-style");

        Assert.Equal("submodule-style", GitRepoResolver.ResolveRepoName(dir));
    }

    [Fact]
    public void Non_Git_Directory_Returns_Cwd_Basename()
    {
        var dir = Path.Combine(this._tempRoot, "just-a-folder");
        Directory.CreateDirectory(dir);

        Assert.Equal("just-a-folder", GitRepoResolver.ResolveRepoName(dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void Returns_Null_For_Empty_Or_Root_Cwd(String? cwd)
    {
        Assert.Null(GitRepoResolver.ResolveRepoName(cwd));
    }

    [Fact]
    public void Handles_Cwd_Ending_In_Multiple_Slashes()
    {
        var repo = Path.Combine(this._tempRoot, "multislash");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));

        Assert.Equal("multislash", GitRepoResolver.ResolveRepoName(repo + "//"));
    }
}
