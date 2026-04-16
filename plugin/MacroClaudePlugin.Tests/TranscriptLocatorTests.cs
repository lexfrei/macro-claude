using System;
using System.IO;

using Loupedeck.MacroClaudePlugin.Status;

using Xunit;

namespace Loupedeck.MacroClaudePlugin.Tests;

// Integration-ish tests on a temp directory. These lock the two
// paths TranscriptLocator must cover:
//
//   1. Direct — derive the project dir via TranscriptPathEncoder and
//      check exactly one file. This is the hot path that fires on
//      every poll tick in production.
//   2. Recursive fallback — if the direct path does not exist (new
//      Claude Code encoding convention we have not seen, rare
//      symlink layouts), fall back to a one-shot recursive scan.
//      The result is cached so the recursive cost is paid once per
//      session lifetime.
public sealed class TranscriptLocatorTests : IDisposable
{
    private readonly String _root;
    private readonly String _projectsDir;

    public TranscriptLocatorTests()
    {
        this._root = Path.Combine(Path.GetTempPath(), "macro-claude-tests-" + Guid.NewGuid().ToString("N"));
        this._projectsDir = Path.Combine(this._root, "projects");
        Directory.CreateDirectory(this._projectsDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup — running tests in parallel or a
            // filesystem glitch should not fail the suite.
        }
    }

    [Fact]
    public void Locate_Returns_Direct_Path_Without_Recursive_Scan()
    {
        var cwd = "/tmp/proj";
        var encoded = TranscriptPathEncoder.Encode(cwd);
        var sid = "sid-direct";
        var expected = Path.Combine(this._projectsDir, encoded, sid + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, "{}");

        var locator = new TranscriptLocator(this._projectsDir);

        var actual = locator.Locate(sid, cwd);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Locate_Caches_Result_Between_Calls()
    {
        var cwd = "/tmp/proj";
        var sid = "sid-cache";
        var path = Path.Combine(this._projectsDir, TranscriptPathEncoder.Encode(cwd), sid + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");

        var locator = new TranscriptLocator(this._projectsDir);

        var first = locator.Locate(sid, cwd);

        // Delete the file on disk. A non-cached implementation would
        // return null on the next call. The cache must survive and
        // keep returning the path until Forget() is called.
        File.Delete(path);

        var second = locator.Locate(sid, cwd);

        Assert.Equal(first, second);
        Assert.NotNull(second);
    }

    [Fact]
    public void Locate_Falls_Back_To_Recursive_Search_When_Direct_Path_Missing()
    {
        var cwd = "/weird/cwd";
        var sid = "sid-fallback";
        var unexpectedDir = Path.Combine(this._projectsDir, "completely-different-name");
        Directory.CreateDirectory(unexpectedDir);
        var expected = Path.Combine(unexpectedDir, sid + ".jsonl");
        File.WriteAllText(expected, "{}");

        var locator = new TranscriptLocator(this._projectsDir);

        var actual = locator.Locate(sid, cwd);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Locate_Returns_Null_But_Does_Not_Cache_Miss()
    {
        var cwd = "/tmp/nosuch";
        var sid = "sid-miss";

        var locator = new TranscriptLocator(this._projectsDir);

        var first = locator.Locate(sid, cwd);
        Assert.Null(first);

        // A young session whose session-status file is on disk
        // seconds before Claude Code has flushed the first JSONL
        // transcript would otherwise have its JsonlMtimeAt stuck
        // at null forever. Re-checking the filesystem on every
        // subsequent tick costs one File.Exists syscall, which is
        // cheap next to the other per-tick work (ps fork+exec,
        // JSONL tail read). Only hits are worth caching.
        var late = Path.Combine(this._projectsDir, TranscriptPathEncoder.Encode(cwd), sid + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(late)!);
        File.WriteAllText(late, "{}");

        var second = locator.Locate(sid, cwd);
        Assert.Equal(late, second);
    }

    [Fact]
    public void Forget_Evicts_Cached_Entry()
    {
        var cwd = "/tmp/evict";
        var sid = "sid-evict";
        var path = Path.Combine(this._projectsDir, TranscriptPathEncoder.Encode(cwd), sid + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");

        var locator = new TranscriptLocator(this._projectsDir);

        var first = locator.Locate(sid, cwd);
        Assert.Equal(path, first);

        File.Delete(path);
        locator.Forget(sid);

        var afterForget = locator.Locate(sid, cwd);
        Assert.Null(afterForget);
    }
}
