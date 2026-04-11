#!/usr/bin/env bash
#
# macro-claude session monitor hook.
#
# Invoked by Claude Code via ~/.claude/settings.json hooks for events:
#   SessionStart, SessionEnd, UserPromptSubmit, PreToolUse, PostToolUse,
#   Stop, StopFailure.
#
# Writes a compact JSON file per session to $STATUS_DIR which the macropad
# plugin polls via FileSystemWatcher.
#
# Status file format:
#   {
#     "session_id":     "<uuid>",
#     "cwd":            "/abs/path",
#     "status":         "idle"|"working"|"error",
#     "last_event":     "Stop"|"UserPromptSubmit"|...,
#     "last_event_s":   <unix seconds>,
#     "heartbeat_s":    <unix seconds>,
#     "turn_started_s": <unix seconds>|null,
#     "idle_since_s":   <unix seconds>|null
#   }
#
# Final "thinking"/"stuck" resolution is done in the plugin based on
# heartbeat_s, CPU usage, and JSONL mtime — this script only records raw
# events so it never blocks Claude Code.

set -o errexit
set -o nounset
set -o pipefail

readonly STATUS_DIR="${HOME}/.claude/session-status"
mkdir -p -- "${STATUS_DIR}"

input="$(cat)"

session_id="$(jq --raw-output '.session_id // empty' <<<"${input}")"
event="$(jq --raw-output '.hook_event_name // empty' <<<"${input}")"
cwd="$(jq --raw-output '.cwd // empty' <<<"${input}")"

if [[ -z "${session_id}" || -z "${event}" ]]; then
  exit 0
fi

status_file="${STATUS_DIR}/${session_id}.json"
now_s="$(date +%s)"

# SessionEnd removes the status file — session no longer exists.
if [[ "${event}" == "SessionEnd" ]]; then
  # -f needed on macOS BSD rm (no --force long form)
  rm -f -- "${status_file}"
  exit 0
fi

status=""
case "${event}" in
  SessionStart | Stop)
    status="idle"
    ;;
  UserPromptSubmit | PreToolUse | PostToolUse)
    status="working"
    ;;
  StopFailure)
    status="error"
    ;;
  *)
    # Unknown event: nothing to record.
    exit 0
    ;;
esac

existing="{}"
if [[ -f "${status_file}" ]]; then
  existing="$(cat -- "${status_file}")"
fi

jq \
  --arg sid "${session_id}" \
  --arg cwd "${cwd}" \
  --arg status "${status}" \
  --arg event "${event}" \
  --argjson now "${now_s}" \
  '
    . as $existing
    | {
        session_id:     $sid,
        cwd:            $cwd,
        status:         $status,
        last_event:     $event,
        last_event_s:   $now,
        heartbeat_s:    $now,
        turn_started_s: (
          if $event == "UserPromptSubmit" then $now
          elif ($existing.turn_started_s // null) != null and $status == "working"
            then $existing.turn_started_s
          else null
          end
        ),
        idle_since_s:   (if $status == "idle" then $now else null end)
      }
  ' <<<"${existing}" >"${status_file}"
