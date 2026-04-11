# macro-claude

[![CI](https://github.com/lexfrei/macro-claude/actions/workflows/ci.yml/badge.svg)](https://github.com/lexfrei/macro-claude/actions/workflows/ci.yml)
[![Release](https://github.com/lexfrei/macro-claude/actions/workflows/release.yml/badge.svg)](https://github.com/lexfrei/macro-claude/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Live status of every running Claude Code session on a Logitech MX Creative
Console, with single-key focus to the terminal that owns the session.

macOS only. Tested against Claude Code CLI 2.1.101 on macOS 13+.

## What it does

If you run several Claude Code sessions in parallel (iTerm2, VS Code
integrated terminals, both at once), it is hard to notice which one has
quietly gone idle and is waiting for your next prompt. macro-claude makes
every running session a coloured key on the MX Creative Console:

- green — idle, assistant turn finished, waiting for you
- blue — working, heartbeat is fresh, something is happening right now
- cyan — thinking, transcript has gone quiet but the process is CPU-busy
- orange — stuck, heartbeat is stale and CPU is near zero
- red — error, last turn ended in `StopFailure` or was interrupted
- dark gray — slot not in use

Each key also shows the short project name and the elapsed turn time
(`MM:SS` or `HH:MM:SS`, so an 8-hour turn is obvious at a glance).

Pressing a key focuses the terminal that owns that session. For VS Code
that's the exact integrated terminal via a companion extension; for
iTerm2 it raises the application so you can pick the tab manually (full
session-level focus via the iTerm2 protobuf API is on the roadmap).

## How it works

macro-claude is not a single program. It is a small pipeline with four
moving parts that share state via files under `~/.claude/`:

```text
Claude Code CLI ─┬─ Stop / UserPromptSubmit / Pre/PostToolUse / StopFailure
                 │  hooks fire during every turn
                 ▼
        hooks/session-monitor.sh ───► ~/.claude/session-status/<sid>.json
                                                       │
                                                       ▼
                                        Logi Plugin Service (LPS)
                                                       │
                                                       ▼
                                 plugin/MacroClaudePlugin C# plugin
                                                       │
              ┌────────────────────────────────────────┼──────────────────────┐
              ▼                                        ▼                      ▼
     FileSystemWatcher on                 ps polling on PIDs       JSONL tail scan
     ~/.claude/session-status/            from sessions/*.json     for "[Request
     and ~/.claude/sessions/              (once per second)        interrupted by user]"
              │                                        │                      │
              └────────────────────┬───────────────────┘──────────────────────┘
                                   ▼
                           StateResolver.Determine(...)
                                   │
                                   ▼
                         SessionSnapshot (state + elapsed)
                                   │
                                   ▼
                 SessionStatusCommand.GetCommandImage(...) → 80×80 PNG
                                   │
                                   ▼
                         pressed → FocusDispatcher
                                   │
                   ┌───────────────┼───────────────┐
                   ▼                               ▼
        VS Code bridge extension          NativeActivator (libobjc)
        POST /focus {pid}                 NSRunningApplication.activate
        → terminal.show()                 → raises iTerm2 window
```

### The four signals behind every state

Claude Code does not fire a hook when you hit **Esc** or **Ctrl+C** to
interrupt a turn, and extended thinking can run 30–60 seconds without
writing anything to the JSONL transcript. A naive "hook heartbeat only"
resolver would mark interrupted sessions as `working` forever and
long-thinking sessions as `stuck`. macro-claude avoids both by combining:

1. **Hook events** — latest event name and timestamps from
   `~/.claude/session-status/<session_id>.json`, written by the bash hook
2. **JSONL mtime** — transcript modified time from
   `~/.claude/projects/**/<session_id>.jsonl`
3. **Process CPU** — `ps -o pcpu=` on the PID recorded in
   `~/.claude/sessions/<pid>.json`
4. **JSONL tail** — last 4 KB of the transcript, scanned for the
   `[Request interrupted by user]` marker that Claude Code leaves behind
   when you abort

The final state is resolved by `StateResolver.Determine(...)`, a pure
function with 10+ unit tests pinning the behaviour for every combination.
Thresholds are:

- fresh heartbeat window — 3 seconds
- stale heartbeat window — 30 seconds
- CPU active threshold — 1.0 %
- CPU idle threshold — 0.5 %

## Components

```text
macro-claude/
├── hooks/                        bash   session monitor + idempotent installer
├── plugin/
│   ├── MacroClaudePlugin/        C# 8   Logi Actions SDK plugin (the macropad side)
│   └── MacroClaudePlugin.Tests/  C# 8   xunit tests for pure logic (41 tests)
└── vscode-extension/             TS     companion extension (HTTP bridge for focus)
```

### hooks/

Two bash scripts, both pass `shellcheck --severity=style --enable=all`:

- **`session-monitor.sh`** — hook entry point. Reads the hook JSON from
  stdin, writes a per-session status file at
  `~/.claude/session-status/<sid>.json`. Also deletes the file on
  `SessionEnd`.
- **`install.sh`** — idempotent installer. Merges `session-monitor.sh`
  into `~/.claude/settings.json` under the relevant hook keys without
  touching any existing hook entries. Takes a `--uninstall` flag.

### plugin/MacroClaudePlugin/

C# 8 plugin loaded by Logi Plugin Service. Strict lint policy applied
project-wide:

- `TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisMode=All`,
  `EnforceCodeStyleInBuild=true`
- StyleCop.Analyzers, Roslynator.Analyzers, Roslynator.Formatting.Analyzers
- Every source file uses file-scoped namespace and is nullable-safe

Key classes:

- `Status/SessionSnapshot` — immutable record of a resolved session
- `Status/StateResolver` — pure function, the state machine itself
- `Status/StatusReader` — `FileSystemWatcher` on two directories, CPU
  poller on a 1 Hz timer, transcript mtime + interrupt-marker scanner,
  emits `SessionSnapshot` via events
- `Status/SlotAssigner` — thread-safe first-come first-served assignment
  of `session_id → slot index` (0..8)
- `Status/SlotBus` — static event bus used to bridge the gap between
  `Plugin.Load` and the `PluginDynamicCommand` instance
- `Actions/SessionStatusCommand` — `PluginDynamicCommand` with 9 slot
  parameters. Each parameter renders an 80×80 bitmap via `BitmapBuilder`
  and dispatches `RunCommand` through `FocusDispatcher`
- `Focus/FocusDispatcher` — routes to VS Code HTTP bridge or iTerm2
  app-level activate
- `Focus/NativeActivator` — minimal P/Invoke bridge to `libobjc.A.dylib`
  for `NSRunningApplication.activateWithOptions:` — no `osascript`,
  no `net8.0-macos` workload

### plugin/MacroClaudePlugin.Tests/

xunit test project targeting `net8.0`. Consumes the pure-logic files as
linked sources rather than via a ProjectReference so it stays free of
Loupedeck build targets.

```text
  Passed!  - Failed: 0, Passed: 41, Total: 41, Duration: 14 ms
```

### vscode-extension/

VS Code extension in TypeScript, written against strict `tsconfig.json`
(`noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`) and the
`typescript-eslint` `strict-type-checked` preset.

Publishes a local HTTP bridge on a random port. Per-window lock files
live at `~/.claude/macro-claude-bridge/<sessionId>.lock` with an auth
token. `POST /focus {pid: number}` finds the terminal via
`vscode.window.terminals`, calls `terminal.show()`, and returns.

## Runtime requirements

- macOS 13 or newer
- [Logi Options+](https://www.logitech.com/software/logi-options-plus.html)
  with Logi Plugin Service running and an MX Creative Console connected
- Claude Code CLI 2.x with hooks configured
- `jq` — used by the bash hook script. `brew install jq` if not present.
- Optional: VS Code for integrated-terminal focus
- Optional: iTerm2 with *Enable Python API* turned on for future
  session-level focus

## Build requirements

- .NET 8 SDK — `brew install dotnet@8`
- Node.js 20 + npm — for the VS Code extension
- `shellcheck` — bash linter, `brew install shellcheck`
- Optional: `shfmt` — bash formatter

## Install

### 1. Build the plugin and install it into Logi Plugin Service

```bash
# from repo root
dotnet build plugin/MacroClaudePlugin/src/MacroClaudePlugin.csproj --configuration Release
```

The build's PostBuild target writes a `.link` file into
`~/Library/Application Support/Logi/LogiPluginService/Plugins/` that
points at the built DLL. Logi Plugin Service picks the plugin up
automatically — no `.lplug4` install needed for local use.

To produce a distributable `.lplug4` package:

```bash
dotnet logiplugintool pack plugin/MacroClaudePlugin/bin/Release dist/MacroClaudePlugin.lplug4
dotnet logiplugintool verify dist/MacroClaudePlugin.lplug4
```

### 2. Install the hooks into your Claude Code settings

```bash
bash hooks/install.sh
```

This is idempotent and creates a timestamped backup of your existing
`~/.claude/settings.json` before writing. Uninstall with
`bash hooks/install.sh --uninstall`.

### 3. Install the VS Code extension (optional, only needed for VS Code)

```bash
cd vscode-extension
npm install
npm run compile
npx vsce package --no-dependencies
code --install-extension macro-claude-bridge-0.0.1.vsix
```

### 4. Configure the macropad

Open *Logi Options+*, find the **Claude** group, and drag **Claude
Session** onto the keys you want. Each slot (0..8) will render its
assigned session when one appears; empty slots show a placeholder.

## Tests and linting

```bash
make test              # dotnet test on pure logic (41 tests)
make lint-shell        # shellcheck hooks
make lint-vscode       # eslint + tsc --noEmit on the vscode extension
```

## Continuous integration

The `.github/workflows/ci.yml` pipeline runs on every push to `main` and
every pull request with three parallel jobs on `ubuntu-latest`:

1. **shellcheck hooks** — strict mode (`--severity=style --enable=all`)
2. **dotnet test (pure logic)** — restore, build, and run xUnit tests
   against `plugin/MacroClaudePlugin.Tests/`. Test results upload as
   `.trx` artifacts.
3. **vscode-extension** — `npm ci`, `tsc --noEmit`, eslint
   strict-type-checked, `esbuild` compile, and `vsce package` sanity
   check. The resulting VSIX uploads as a workflow artifact on every
   green run, so you always have a fresh extension build to grab.

**What CI does NOT build:** the macropad plugin itself. The plugin csproj
references `PluginApi.dll` from a macOS-only Logi Plugin Service install
(`/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle/`).
Automating an LPS installation inside a GitHub-hosted macOS runner is
brittle, so plugin builds stay local. CI still validates everything that
can be validated without `PluginApi.dll` — which is all the pure logic
under test.

## Releases

Releases are cut by pushing a tag matching `v*`:

```bash
# make sure you're green locally
make test
make lint

# tag and push
git tag --sign v1.0.0 --message "Release v1.0.0"
git push origin v1.0.0
```

That triggers `.github/workflows/release.yml`, which re-runs the full
CI gate, builds the VSIX, creates a GitHub release with installation
notes, and attaches the `macro-claude-bridge-*.vsix` to it.

### Adding the `.lplug4` to the release

Because CI cannot build the macropad plugin, the `.lplug4` must be
attached manually from a local macOS machine after the release workflow
completes:

```bash
# from the tagged commit, on a machine with LPS installed
make release-plugin               # produces dist/MacroClaudePlugin.lplug4
make release-upload TAG=v1.0.0    # uploads everything in dist/ to the release
```

`make release-upload` uses `gh release upload --clobber` so it is safe
to re-run. `make release` is an umbrella target that builds both the
plugin and the extension into `dist/` in one step.

## Troubleshooting

**The plugin loads but no keys react.** Check
`~/.claude/session-status/`. If it is empty, hooks are not firing —
re-run `bash hooks/install.sh` and verify `~/.claude/settings.json` has
the `Stop`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, and
`StopFailure` hook events pointing at `session-monitor.sh`.

**The plugin does not appear in Logi Options+ at all.** Check
`~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/MacroClaude.log`.
A successful load shows:

```text
INFO  | 1 dynamic actions loaded
INFO  | macro-claude: StatusReader started
INFO  | Plugin 'MacroClaude' version '1.0' loaded from ...
```

A failure shows:

```text
ERROR | Cannot load plugin from '...'
WARN  | Plugin 'MacroClaude' added to disabled plugins list
```

The most common cause is that the assembly has no concrete
`ClientApplication` subclass — Logi Plugin Service requires one even
for universal plugins. Make sure `MacroClaudeApplication.cs` exists and
extends `ClientApplication`.

**Pressing a key does nothing for VS Code.** Make sure the companion
extension is installed and that VS Code is open. Check
`~/.claude/macro-claude-bridge/` for a `<sessionId>.lock` file — if
missing, the extension did not activate. Open *Output → Extension Host*
in VS Code.

**Everything is off by one second.** All timestamps in
`~/.claude/session-status/` are Unix seconds, not milliseconds. The
`turn_started_s` field is always the start of the **current** turn, not
wall-clock elapsed session age.

## Privacy

The plugin only reads files that Claude Code already writes to
`~/.claude/`:

- `~/.claude/sessions/<pid>.json` — PID, session id, cwd
- `~/.claude/session-status/<sid>.json` — hook events (written by us)
- `~/.claude/projects/**/<sid>.jsonl` — last 4 KB only, scanned for the
  interrupt marker string

Nothing is sent off-machine. No telemetry. No network calls except the
localhost-only HTTP bridge to the VS Code companion extension.

## Status

Working end-to-end: plugin loads into Logi Plugin Service, StatusReader
watches all three source-of-truth directories, hooks are installed,
FocusDispatcher handles VS Code focus end-to-end. iTerm2 session-level
focus is still app-level only — full protobuf client is tracked for v2.

## License

MIT. See `LICENSE` or `LoupedeckPackage.yaml`.
