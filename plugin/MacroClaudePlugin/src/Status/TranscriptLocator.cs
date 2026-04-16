using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace Loupedeck.MacroClaudePlugin.Status;

// Finds a Claude Code session transcript under ~/.claude/projects and
// caches the result per session id.
//
// Fast path: compute `~/.claude/projects/<Encode(cwd)>/<sid>.jsonl`
// and issue a single File.Exists. On a modern macOS filesystem this is
// a single getattr syscall — well under a millisecond.
//
// Slow path (fallback): if the direct path does not exist — either
// because Claude Code's encoding convention changed or because the
// transcript lives in an unusual location — recursively enumerate
// every JSONL file under projectsDir and pick the freshest one
// matching `<sid>.jsonl`. The result (including null if nothing is
// found) is memoized so this cost is paid at most once per session.
//
// Cache eviction is explicit via Forget() — the StatusReader calls
// it on every path where a session is considered gone.
public sealed class TranscriptLocator(String projectsDir)
{
    private readonly String _projectsDir = projectsDir;
    private readonly ConcurrentDictionary<String, String?> _cache
        = new(StringComparer.Ordinal);

    public String? Locate(String sessionId, String cwd)
    {
        if (this._cache.TryGetValue(sessionId, out var cached))
        {
            return cached;
        }

        var resolved = this.Resolve(sessionId, cwd);

        // Only cache hits. A miss on the first tick just means the
        // session's JSONL transcript hasn't been flushed yet — Claude
        // Code writes it asynchronously after it starts. Caching a
        // null would keep that session's JsonlMtimeAt stuck at null
        // forever, starving StateResolver of the transcript-mtime
        // signal it uses to detect thinking/stuck transitions.
        if (resolved is not null)
        {
            this._cache[sessionId] = resolved;
        }
        return resolved;
    }

    public void Forget(String sessionId) => this._cache.TryRemove(sessionId, out _);

    private String? Resolve(String sessionId, String cwd)
    {
        if (!Directory.Exists(this._projectsDir))
        {
            return null;
        }

        if (!String.IsNullOrEmpty(cwd))
        {
            var encoded = TranscriptPathEncoder.Encode(cwd);
            var direct = Path.Combine(this._projectsDir, encoded, sessionId + ".jsonl");
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        // Fallback. This is the old O(N) behaviour that we had before
        // TranscriptPathEncoder existed — keep it for robustness but
        // never expect to execute it in the steady state.
        try
        {
            return Directory
                .EnumerateFiles(this._projectsDir, sessionId + ".jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
