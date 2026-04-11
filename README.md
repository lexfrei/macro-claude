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

Pressing a key focuses the exact terminal that owns that session. For
VS Code that's the exact integrated terminal via a companion extension;
for iTerm2 it's the exact session (tab + split) via the iTerm2 Python
API protobuf client, with automatic fallback to app-level activate if
the iTerm2 API is off or the socket is missing.

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
                 ClaudeSlotNCommand.GetCommandImage(...) → 80×80 PNG
                                   │
                                   ▼
                         pressed → FocusDispatcher cascade
                                   │
       ┌───────────────────────────┼────────────────────────┐
       ▼                           ▼                        ▼
VS Code bridge (HTTP)      ITerm2Client (protobuf)   NativeActivator
POST /focus {pid}          over Unix socket,         (libobjc)
→ terminal.show() in       session ActivateRequest   NSRunningApplication
  owning window            by jobPid                 .activate — plain
       │                           │                  app-level raise
       ▼                           ▼                        ▲
VSCodeUrlActivator         ITerm2AppleScriptActivator        │
/usr/bin/open              osascript to iTerm2               │
vscode://file/<root>       dictionary, select tab            │
(reaches windows in        by tty — reaches sessions         │
 other fullscreen          in other fullscreen Spaces        │
 Spaces)                                                     │
       │                                                     │
       ▼                                                     │
AppleScriptActivator ────────────────────────────────────────┘
System Events AXRaise by window title
(single-Space fallback)
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

- `Status/SessionSnapshot` — immutable record of a resolved session,
  including the worktree-aware `RepoName`
- `Status/StateResolver` — pure function, the state machine itself
- `Status/StatusReader` — `FileSystemWatcher` on two directories, CPU
  poller on a 1 Hz timer, transcript mtime + interrupt-marker scanner,
  a reconciliation sweep that drops sessions whose files disappear
  behind FSEvents' back, and a pid resolver that retries the sessions
  directory when hook events race ahead of Claude Code writing
  `sessions/<pid>.json`. Emits `SessionSnapshot` via events.
- `Status/SlotAssigner` — thread-safe first-come first-served assignment
  of `session_id → slot index` (0..8)
- `Status/SlotBus` — static event bus between `Plugin.Load` and the
  nine slot commands. Protected by an owner token so a zombie Plugin
  instance that LPS failed to fully unload cannot corrupt the
  snapshot store after a reload.
- `Status/GitRepoResolver` — filesystem-only resolution of the main
  worktree basename from a session cwd, so buttons for a git
  worktree show the repo name instead of the branch
- `Actions/SlotCommandBase` + nine `ClaudeSlot1Command..ClaudeSlot9Command`
  subclasses — each is a separate `PluginDynamicCommand` pinned to a
  fixed slot index, because the MX Creative Keypad UI in Logi
  Options+ does not expand `AddParameter` into separate draggable
  entries the way the Loupedeck Live UI does
- `Focus/FocusDispatcher` — the five-path cascade below
- `Focus/NativeActivator` — P/Invoke bridge to `libobjc.A.dylib` for
  `NSRunningApplication.activateWithOptions:`. Used for plain
  bundle-level activation, and as the dead-last fallback when the
  URL-scheme and AppleScript paths below all fail
- `Focus/VSCodeUrlActivator` — shells out to `/usr/bin/open
  vscode://file/<workspaceRoot>`. macOS LaunchServices routes the URL
  to the existing VS Code window holding that folder, including
  windows on different fullscreen Spaces (which AX API cannot reach)
- `Focus/AppleScriptActivator` — System Events `AXRaise` by window
  title. Used as a single-Space fallback for VS Code when the URL
  path cannot determine the workspace root
- `Focus/ITerm2Client` — protobuf over the iTerm2 Python API Unix
  socket, primary path for iTerm2 session-level focus **if** the
  user has turned the Python API on in iTerm2 Settings
- `Focus/ITerm2AppleScriptActivator` — `osascript` to iTerm2's own
  AppleScript dictionary. Zero-configuration fallback used when the
  Python API is off; walks window/tab/session by TTY and selects the
  matching session. Works across fullscreen Spaces because the
  AppleScript commands run inside iTerm2's own process.

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
token; stale locks from crashed extension hosts are cleaned up on
next activation. `POST /focus {pid: number}` walks the target PID's
ancestor chain via `ps -axo pid=,ppid=` and matches against every
integrated terminal's shell PID **and** this window's own extension
host PID (for sessions launched by the Anthropic Claude Code VS Code
extension — those children live under the extension host, not a
shell). On a match it returns `{focused, terminalName, workspaceName,
workspaceRoot}`; the plugin then asks `open vscode://file/<root>`
to raise the correct window, which works across fullscreen Spaces.
For multi-root `.code-workspace` windows it returns the path to the
`.code-workspace` file instead of the first folder, so the URL
activates the real multi-root workspace rather than a new
single-folder window.

## Runtime requirements

- macOS 13 or newer
- [Logi Options+](https://www.logitech.com/software/logi-options-plus.html)
  with Logi Plugin Service running and an MX Creative Console connected
- Claude Code CLI 2.x with hooks configured
- `jq` — used by the bash hook script. `brew install jq` if not present.
- Optional: VS Code with the companion extension (in `vscode-extension/`)
  for point-accurate terminal focus. Works for both terminal-based
  Claude sessions and the Anthropic Claude Code VS Code extension.
- Optional: iTerm2. Session-level focus works out of the box via
  AppleScript — *no Python API setup required*. macOS may prompt
  once for Accessibility / Automation permission for Logi Plugin
  Service on first use; accept it. If you prefer the protobuf path,
  turning on *Settings → General → Magic → Enable Python API* lets
  the plugin use `ITerm2Client` first, which is a hair faster per
  press. Either path reaches fullscreen iTerm2 windows correctly.

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

Open *Logi Options+* and find the **Claude** category. There are three
sub-groups — **Claude / Page 1**, **Page 2**, **Page 3** — each with 9
slot commands (`Slot 1..9`). Drag whichever slots you want onto your
MX Creative Console keys. Sessions are assigned to slots on a first
come, first served basis as they appear, so a reasonable layout is to
place slots on one profile page and start macro-claude sessions as you
need them.

## Configuration (optional)

All state-resolver thresholds are tunable per machine by dropping a
`~/.claude/macro-claude.json` file. Every field is optional; a missing
or non-positive value falls back to the constant default used by
`StateResolver`.

```json
{
  "freshHeartbeatSeconds": 3,
  "staleHeartbeatSeconds": 30,
  "cpuActiveThreshold": 1.0,
  "cpuIdleThreshold": 0.5
}
```

| Field | Default | Meaning |
| ----- | ------- | ------- |
| `freshHeartbeatSeconds` | 3 | Heartbeat is "fresh" if age < N s; working |
| `staleHeartbeatSeconds` | 30 | Heartbeat is "stale" if age > N s; triggers stuck/thinking discrimination |
| `cpuActiveThreshold` | 1.0 | CPU ≥ N % with stale heartbeat → thinking |
| `cpuIdleThreshold` | 0.5 | CPU ≤ N % with stale heartbeat → stuck |

The config file is read once in `StatusReader`'s constructor. Changes
at runtime require a plugin reload (touch the `.link` file or restart
Logi Plugin Service).

Raise `freshHeartbeatSeconds` if your Claude Code sessions routinely
emit output in chunks wider than 3 seconds — otherwise they will
bounce between `working` and `thinking`. Lower `cpuActiveThreshold`
on machines with noisy background CPU, or raise it if the idle
baseline is high.

## Tests and linting

```bash
make test              # dotnet test on pure logic (50 tests)
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

**Pressing a key lands on a different VS Code window than the
target session.** The companion extension is missing or stale; the
plugin fell through to bundle-level activate which always picks the
most-recently-used window. Check:

1. `~/.claude/macro-claude-bridge/` contains a `<sessionId>.lock`
   for each VS Code window you have open. Missing means that window's
   extension did not activate — open *Output → Extension Host* in
   that window. Stale extra locks from crashed hosts are cleaned up
   automatically on next extension activation.
2. The companion extension is actually installed in VS Code. Run
   `code --list-extensions | grep macro-claude`.
3. After an upgrade, *all* open VS Code windows need **Developer:
   Reload Window** (⌘⇧P) so the new extension code gets loaded in
   each extension host.

**Pressing a key opens a *new* VS Code window instead of activating
the existing one.** The `workspaceRoot` returned by the bridge does
not match any window that is currently open with that folder. For
single-folder windows this can happen when Claude Code was launched
from a subdirectory of the workspace root — the session's `cwd`
points at the subdir, and LaunchServices opens that subdir as a new
window. The fix is in the extension: reload the VS Code window so
the extension returns the real `workspaceFolders[0]` fsPath (or, for
multi-root windows, the path to the `.code-workspace` file) instead
of `cwd`.

**Pressing a key raises iTerm2 but lands on the wrong tab.** Usually
fixes itself on LPS reload. If it keeps happening:

1. Check *System Settings → Privacy & Security → Automation* and
   make sure `Logi Plugin Service` is allowed to control `iTerm2`
   and `System Events`.
2. Check that `ps -o tty= -p <claude pid>` returns the tty the
   target session actually owns. If it returns `?`, the target
   process has no controlling terminal (e.g. a background
   subprocess) and AppleScript matching will not find it.
3. If you have the Python API turned on, check the
   `ITerm2Client.FocusSessionByPidAsync` path by enabling verbose
   LPS logs — `jobPid` matching fails for Claude Code running
   inside tmux or any shell where `claude` is not the foreground
   job.

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
FocusDispatcher handles VS Code and iTerm2 session-level focus via the
iTerm2 Python API protobuf client with graceful fallback to app-level
activate.

## License

MIT. See `LICENSE` or `LoupedeckPackage.yaml`.
