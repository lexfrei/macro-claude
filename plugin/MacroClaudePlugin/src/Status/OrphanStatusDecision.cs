using System;
using System.Collections.Generic;

namespace Loupedeck.MacroClaudePlugin.Status;

// Pure decision function for reaping orphan session-status files whose
// matching ~/.claude/sessions/<pid>.json never arrived — the classic
// aftermath of a hard reboot or an unclean Claude Code exit. The
// accumulator is stuck with Pid=0 forever, so ReapDeadPidSessions
// (Pid>0 only) and ReconcileSessionStatusDirectory (file-on-disk only)
// both pass it by. Without this sweep the stuck slot would show
// indefinitely.
//
// Split out of StatusReader so the rule is unit-testable without
// spinning up a live FileSystemWatcher + timer + directory, mirroring
// the ReconcileDecision pattern.
internal static class OrphanStatusDecision
{
    // Returns the session_ids that should be reaped. Rules:
    //   * Pid>0 is not our concern — ReapDeadPidSessions handles that.
    //   * HasStatusFile=false means an accumulator created from
    //     sessions/<pid>.json before its first hook fired; reaping
    //     would kill a live session on the next tick.
    //   * Liveness is max(HookHeartbeatAt, JsonlMtimeAt). Mirrors the
    //     "authoritative heartbeat" StatusReader.Emit already uses, so
    //     a session still streaming transcript output without fresh
    //     hooks (hook script uninstalled mid-session, extended
    //     thinking that buffers hook writes) is not mistakenly
    //     reaped.
    //   * Both signals null means no timing evidence at all; leave it
    //     for the file-on-disk path instead of guessing.
    //   * Strict > comparison: age exactly at the threshold is still
    //     considered recent, to keep the boundary predictable.
    public static IEnumerable<String> SessionsToReap(
        IEnumerable<OrphanCandidate> candidates,
        DateTimeOffset now,
        TimeSpan staleThreshold)
    {
        foreach (var c in candidates)
        {
            if (String.IsNullOrEmpty(c.SessionId))
            {
                continue;
            }
            if (c.Pid > 0)
            {
                continue;
            }
            if (!c.HasStatusFile)
            {
                continue;
            }
            var lastActivity = LatestOf(c.HookHeartbeatAt, c.JsonlMtimeAt);
            if (lastActivity is null)
            {
                continue;
            }
            if (now - lastActivity.Value <= staleThreshold)
            {
                continue;
            }
            yield return c.SessionId;
        }
    }

    // Exposed as internal so StatusReader.Emit can reuse the same
    // liveness rule. Keeping one definition avoids a drift risk:
    // the reaper and the snapshot emitter must agree on what "last
    // activity" means, otherwise a session could be snapshotted as
    // live yet reaped for staleness on the same tick.
    internal static DateTimeOffset? LatestOf(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null)
        {
            return b;
        }
        if (b is null)
        {
            return a;
        }
        return a.Value > b.Value ? a : b;
    }
}

internal readonly record struct OrphanCandidate(
    String SessionId,
    Int32 Pid,
    Boolean HasStatusFile,
    DateTimeOffset? HookHeartbeatAt,
    DateTimeOffset? JsonlMtimeAt);
