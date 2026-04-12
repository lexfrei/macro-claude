# Changelog

All notable changes to macro-claude are tracked here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `orphanReapSeconds` override in `~/.claude/macro-claude.json` for the
  new orphan-status sweep. Defaults to 300 s (5 min); reap fires from
  the 1 Hz poll tick.
- iTerm2 session-level focus via the iTerm2 Python API protobuf client
  (Unix domain socket + manual WebSocket handshake + Google.Protobuf).
  On press, `ITerm2Client.FocusSessionByPidAsync` walks every
  window/tab/session, matches by `jobPid`, and sends `ActivateRequest`
  with `order_window_front`, `select_tab`, `select_session` so the
  exact split is raised. Falls through to app-level activate if the
  API is off, the socket is missing, or the cookie is denied.
- Vendored `plugin/MacroClaudePlugin/src/Proto/api.proto` (1642 lines)
  from gnachman/iTerm2. Grpc.Tools + Google.Protobuf packages wire up
  protoc codegen at build time.
- Multi-page slot expansion from 9 to 27. Slots 1–9 / 10–18 / 19–27
  are grouped in the Logi Options+ command picker as "Claude /
  Page 1", "Page 2", "Page 3".
- Optional per-machine threshold overrides via
  `~/.claude/macro-claude.json`. All four thresholds are individually
  optional; non-positive or missing values fall back to defaults:

  ```json
  {
    "freshHeartbeatSeconds": 3,
    "staleHeartbeatSeconds": 30,
    "cpuActiveThreshold": 1.0,
    "cpuIdleThreshold": 0.5
  }
  ```

- `StateResolverConfig` record with `TryLoadFromFile` static loader
  and unit tests for parse / partial / invalid JSON / bounds
  rejection / unknown-field tolerance.
- GitHub Actions `ci.yml` (shellcheck, dotnet test, VSIX build) and
  `release.yml` (gated on CI, creates GitHub release with VSIX
  attached).
- `LICENSE` (MIT), top-level `CLAUDE.md` with project-specific
  guidance for future sessions, and `CHANGELOG.md` (this file).
- Makefile targets: `test`, `release`, `release-plugin`, `release-vsix`,
  `release-upload TAG=v1.0.0`.
- Branded `Icon256x256.png` rendered from Pillow as a 3×3 coloured grid
  matching the state palette used by the button renderer.

### Changed
- `StateResolver.Determine` gains an optional `StateResolverConfig?`
  trailing parameter. Passing `null` gives identical behaviour to
  the pre-change hardcoded path.
- `StatusReader` adds a second constructor overload that accepts an
  explicit `StateResolverConfig?`. The single-argument overload now
  auto-loads `~/.claude/macro-claude.json` through a static helper.
- `SessionStatusCommand.MaxSlots` bumped from 9 to 27.
- `README.md` rewritten for ship quality with CI / Release sections
  and a troubleshooting FAQ covering VS Code bridge, LPS load
  failures, and iTerm2 API gotchas.
- `FocusDispatcher.FocusAsync` cascade now inserts iTerm2
  session-level focus between the VS Code HTTP bridge and the
  app-level activate fallback.
- Quick Focus tile text label no longer prefixes the session's
  short name with an arrow glyph. The bitmap icon already carries
  the "where to focus" affordance via its ⏎ / ▶ / · glyph, so
  duplicating it in the text crowded the label.

### Fixed
- Stale `~/.claude/session-status/<sid>.json` files with no matching
  `~/.claude/sessions/<pid>.json` (typical after a hard reboot or any
  unclean Claude Code exit) are no longer shown as a "stuck" slot
  forever. A new `OrphanStatusDecision` sweep reaps accumulators whose
  `Pid=0`, `HasStatusFile=true`, and `max(HookHeartbeatAt,
  JsonlMtimeAt)` is older than `orphanReapSeconds`, and deletes the
  stale file from disk. Liveness mirrors the max-of-hook-and-jsonl
  rule `StatusReader.Emit` already uses, so a session still streaming
  transcript output without fresh hooks is not mistakenly reaped. The
  two existing reap paths — `ReapDeadPidSessions` (Pid>0) and
  `ReconcileSessionStatusDirectory` (file-on-disk) — could not reach
  this case.
- **Plugin load failures caused by missing `MacroClaudeApplication`
  stub.** LPS silently refuses to load any assembly that does not
  contain a concrete `ClientApplication` subclass, even when
  `HasNoApplication = true` on the Plugin. The ef198d3 refactor
  deleted the stub as "unused code" and broke the plugin. Restored
  in `e48d86c` with a comment pointing at this gotcha.
- `chmod +x --` in `hooks/install.sh` fails on macOS BSD chmod which
  does not accept `--` as an end-of-options separator. The global
  CLAUDE.md "full flag names" rule explicitly documents BSD
  exceptions.
- `deps.json` bloat from analyzer PackageReferences — root cause was
  a red herring, the real culprit was the missing
  `MacroClaudeApplication` stub. `deps.json` listing analyzer
  packages is cosmetic and does not break LPS load.

### Known limitations
- The macropad plugin `.lplug4` cannot be built in CI because the
  plugin csproj references `PluginApi.dll` from a macOS-only Logi
  Plugin Service install. Local build only — see README §Releases.
- iTerm2 session-level focus needs *Settings → General → Magic →
  Enable Python API* turned on, and the user must accept a one-time
  AppleScript cookie prompt on first focus request after plugin load.
- Claude Code running inside `tmux` or backgrounded via `&` will not
  match the iTerm2 `jobPid` variable because that returns the
  session's current foreground PID.
- There is no real Claude mascot artwork yet — the `Icon256x256.png`
  is a placeholder grid.
