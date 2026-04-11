#nullable enable
namespace Loupedeck.MacroClaudePlugin.Status
{
    using System;

    // Pure function that decides which SessionState applies given four
    // composite signals:
    //
    //   1. hook events   — lastEvent + heartbeatAt (max of last_event_s and
    //                      JSONL mtime)
    //   2. JSONL mtime   — folded into heartbeatAt
    //   3. CPU activity  — cpuPercent of the PID
    //   4. JSONL tail    — interruptedMarker: whether the transcript ends
    //                      with "[Request interrupted by user]"
    //
    // Rules, in priority order:
    //
    //   error    — interrupted marker OR StopFailure hook
    //   idle     — SessionStart or Stop hook, or unknown/missing event
    //   working  — working-class hook fired AND heartbeat age < 3s
    //   stuck    — heartbeat age > 30s AND cpu < 0.5%
    //   thinking — cpu > 1.0% (JSONL silent but process is busy)
    //   stuck    — fallback when heartbeat is stale and CPU is near zero
    //
    // The resolver never returns Gone — that state comes from the absence
    // of a session entry in ~/.claude/sessions/<PID>.json and is handled
    // by the caller (StatusReader).
    public static class StateResolver
    {
        // Threshold tuning. Kept as fields so tests can bind them if needed.
        public static readonly TimeSpan FreshHeartbeatWindow = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan StaleHeartbeatWindow = TimeSpan.FromSeconds(30);
        public const Double CpuActiveThreshold = 1.0;
        public const Double CpuIdleThreshold = 0.5;

        public static SessionState Determine(
            String? lastEvent,
            DateTimeOffset? heartbeatAt,
            Double cpuPercent,
            Boolean interruptedMarker,
            DateTimeOffset now)
        {
            if (interruptedMarker || lastEvent == "StopFailure")
            {
                return SessionState.Error;
            }

            if (lastEvent is null or "Stop" or "SessionStart")
            {
                return SessionState.Idle;
            }

            // Only treat specific events as "something is happening now".
            if (lastEvent is not ("UserPromptSubmit" or "PreToolUse" or "PostToolUse"))
            {
                return SessionState.Idle;
            }

            var heartbeatAge = heartbeatAt.HasValue
                ? now - heartbeatAt.Value
                : TimeSpan.MaxValue;

            if (heartbeatAge < FreshHeartbeatWindow)
            {
                return SessionState.Working;
            }

            if (heartbeatAge > StaleHeartbeatWindow && cpuPercent < CpuIdleThreshold)
            {
                return SessionState.Stuck;
            }

            if (cpuPercent > CpuActiveThreshold)
            {
                return SessionState.Thinking;
            }

            // Heartbeat is in the middle band (3–30s) and CPU is neither
            // clearly busy nor clearly idle — treat as stuck to grab attention.
            return SessionState.Stuck;
        }
    }
}
