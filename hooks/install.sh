#!/usr/bin/env bash
#
# Install macro-claude session monitor into ~/.claude/settings.json hooks.
#
# Idempotent: safe to run multiple times.  The script will only add the
# monitor command if it is not already present in each event's hook list.
#
# Usage:
#   bash hooks/install.sh             # install
#   bash hooks/install.sh --uninstall  # remove all macro-claude hook entries

set -o errexit
set -o nounset
set -o pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
readonly MONITOR_SCRIPT="${SCRIPT_DIR}/session-monitor.sh"
readonly SETTINGS_FILE="${HOME}/.claude/settings.json"

# Events we hook into. SessionEnd removes the status file, the others
# update it. PreToolUse/PostToolUse double as heartbeat pulses.
# Notification fires when Claude Code is waiting on the user — plan
# approval, permission prompt, etc. — and we surface that as its own
# state on the macropad so the user can tell a blocked turn apart
# from a finished turn.
readonly EVENTS=(
  SessionStart
  UserPromptSubmit
  PreToolUse
  PostToolUse
  Notification
  Stop
  StopFailure
  SessionEnd
)

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

ensure_prereqs() {
  [[ -f "${MONITOR_SCRIPT}" ]] || die "session-monitor.sh not found at ${MONITOR_SCRIPT}"
  # macOS BSD chmod does not accept `--` as an end-of-options separator.
  if [[ ! -x "${MONITOR_SCRIPT}" ]]; then
    chmod +x "${MONITOR_SCRIPT}"
    printf 'made %s executable\n' "${MONITOR_SCRIPT}"
  fi
  command -v jq >/dev/null 2>&1 || die "jq is required — brew install jq"
  [[ -f "${SETTINGS_FILE}" ]] || die "settings file not found at ${SETTINGS_FILE}"
}

backup_settings() {
  local ts backup
  ts="$(date +%Y%m%d-%H%M%S)"
  backup="${SETTINGS_FILE}.bak.${ts}"
  cp -- "${SETTINGS_FILE}" "${backup}"
  printf 'backup: %s\n' "${backup}"
}

# Install monitor command into every event, preserving existing hooks.
# Each hooks entry looks like:
#   { "hooks": [ { "type": "command", "command": "..." }, ... ] }
# We append a new entry containing only our command iff none of the
# existing entries already reference it.
install_hooks() {
  local current event tmp
  current="$(cat -- "${SETTINGS_FILE}")"

  for event in "${EVENTS[@]}"; do
    current="$(
      jq \
        --arg event "${event}" \
        --arg cmd "${MONITOR_SCRIPT}" \
        '
          .hooks //= {}
          | .hooks[$event] //= []
          | if (
              .hooks[$event]
              | map(.hooks // [] | map(.command // empty))
              | flatten
              | any(. == $cmd)
            )
            then .
            else .hooks[$event] += [
              { "hooks": [ { "type": "command", "command": $cmd } ] }
            ]
            end
        ' <<<"${current}"
    )"
  done

  tmp="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '${tmp}'" EXIT
  printf '%s\n' "${current}" | jq --indent 2 '.' >"${tmp}"
  mv -- "${tmp}" "${SETTINGS_FILE}"
  trap - EXIT

  printf 'installed macro-claude hook into events: %s\n' "${EVENTS[*]}"
}

uninstall_hooks() {
  local current event tmp
  current="$(cat -- "${SETTINGS_FILE}")"

  for event in "${EVENTS[@]}"; do
    current="$(
      jq \
        --arg event "${event}" \
        --arg cmd "${MONITOR_SCRIPT}" \
        '
          if (.hooks[$event] | type) != "array" then .
          else
            .hooks[$event] |= (
              map(
                .hooks |= (
                  if (. | type) == "array"
                  then map(select(.command != $cmd))
                  else .
                  end
                )
              )
              | map(select((.hooks | length) > 0))
            )
            | if (.hooks[$event] | length) == 0 then del(.hooks[$event]) else . end
          end
        ' <<<"${current}"
    )"
  done

  tmp="$(mktemp)"
  # shellcheck disable=SC2064
  trap "rm -f -- '${tmp}'" EXIT
  printf '%s\n' "${current}" | jq --indent 2 '.' >"${tmp}"
  mv -- "${tmp}" "${SETTINGS_FILE}"
  trap - EXIT

  printf 'uninstalled macro-claude hook from events: %s\n' "${EVENTS[*]}"
}

main() {
  ensure_prereqs
  backup_settings

  case "${1:-install}" in
    install)
      install_hooks
      ;;
    --uninstall | uninstall)
      uninstall_hooks
      ;;
    *)
      die "unknown argument: ${1}"
      ;;
  esac
}

main "$@"
