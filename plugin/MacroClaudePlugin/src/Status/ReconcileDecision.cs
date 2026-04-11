using System;
using System.Collections.Generic;

namespace Loupedeck.MacroClaudePlugin.Status;

// Pure decision function for StatusReader.ReconcileSessionStatusDirectory.
// Split out of StatusReader itself so the rule can be unit-tested
// without spinning up a live FileSystemWatcher + timer + directory.
internal static class ReconcileDecision
{
    // Given a snapshot of currently-tracked sessions, a map of
    // session_id → "has ever had a hook-written status file",
    // and the set of session_ids whose status file is currently on
    // disk, return the session_ids that should be removed from
    // the in-memory store.
    //
    // Rules:
    //   * A session still present on disk is NOT reaped.
    //   * A session that has never had a status file is NOT reaped —
    //     it was created from a sessions/<pid>.json observation
    //     before the hook fired, and reaping it would kill a live
    //     session on the very next tick.
    //   * A session with HasStatusFile=true whose file has
    //     disappeared IS reaped — FSEvents missed the delete and
    //     this is the catch-up path.
    public static IEnumerable<String> SessionsToReap(
        IEnumerable<String> currentSessionIds,
        IReadOnlyDictionary<String, Boolean> hasStatusFile,
        IReadOnlySet<String> onDisk)
    {
        foreach (var sessionId in currentSessionIds)
        {
            if (String.IsNullOrEmpty(sessionId))
            {
                continue;
            }
            if (onDisk.Contains(sessionId))
            {
                continue;
            }
            if (!hasStatusFile.TryGetValue(sessionId, out var hadFile) || !hadFile)
            {
                continue;
            }
            yield return sessionId;
        }
    }
}
