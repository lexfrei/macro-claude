# macro-claude

Show running Claude Code session status on a Logitech MX Creative Console
and focus the owning terminal with a keypress.

Built for one developer (the author). **Not a general-purpose tool.**

## Goal

Running several Claude Code sessions in parallel (iTerm2 and VS Code
integrated terminals), it is hard to notice which one has gone idle and is
waiting for input. macro-claude shows the live state of every session on the
MX Creative Console's LCD keys and lets you jump to the terminal of any
session with a single key press.

## State model

Six resolved states, produced by a composite of four signals:

| State    | Meaning                                                       | Visual      |
| -------- | ------------------------------------------------------------- | ----------- |
| gone     | Session process no longer exists                              | dark        |
| idle     | Assistant turn finished, waiting for user prompt              | green       |
| working  | Turn in progress, heartbeat fresh (tool call / stream active) | blue        |
| thinking | Turn in progress, silent but process is CPU-busy              | cyan        |
| stuck    | Turn in progress, heartbeat stale, no CPU activity            | orange      |
| error    | Last turn ended in StopFailure or JSONL `[Request interrupted by user]` | red |

Each button shows: state color, session name (short), turn elapsed time
(`MM:SS` or `HH:MM:SS`), and a Claude mascot icon.

### Why a composite

Claude Code does not fire hooks on user interrupts (`Esc`, `Ctrl+C`). The
only marker is a `[Request interrupted by user]` line in the JSONL transcript.
Extended thinking also does not update JSONL `mtime` for 30–60 seconds at a
time — thinking blocks are written in one chunk at the end. So we combine
four signals:

1. **Hook events** → `~/.claude/session-status/<session_id>.json`
2. **JSONL mtime** → `~/.claude/projects/**/<session_id>.jsonl`
3. **Process CPU** → `ps` lookup from `~/.claude/sessions/<PID>.json`
4. **JSONL tail** → last record, to detect the interrupt marker

The resolver lives in the plugin and runs once per second.

## Components

```text
macro-claude/
├── hooks/            # bash — runs inside Claude Code, writes event log
├── plugin/           # C# / .NET 8 — Logi Actions SDK plugin for MX Creative Console
└── vscode-extension/ # TypeScript — HTTP bridge so the plugin can focus integrated terminals
```

### hooks/

Bash script called by Claude Code for every hook event. Writes a compact
JSON per session to `~/.claude/session-status/<session_id>.json`. Events:
`SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`,
`StopFailure`, `SessionEnd`. One file per session, deleted on `SessionEnd`.

### plugin/

C# plugin loaded by Logi Plugin Service. Reads the event log via
`FileSystemWatcher`, polls `ps` for CPU usage, renders 80×80 PNGs via
`BitmapBuilder`, and dispatches focus requests to the correct terminal:

- **iTerm2** — direct WebSocket client to iTerm2 API over Unix domain socket
  (protobuf, `api.iterm2.com` subprotocol).
- **VS Code** — HTTP POST to the companion extension's local bridge server.

### vscode-extension/

Minimal VS Code extension exposing a local HTTP endpoint. On request
`POST /focus` with `{pid | session_id}`, it finds the terminal via
`vscode.window.terminals`, shows it, and raises the window.

## Dependencies (runtime)

- macOS 13+
- [Logi Options+](https://www.logitech.com/software/logi-options-plus.html)
  with Logi Plugin Service running
- Claude Code CLI with hooks configured
- `jq` (used by hooks script)
- iTerm2 with **Enable Python API** turned on (for iTerm2 focus to work)
- VS Code with the companion extension installed (for VS Code focus)

## Dependencies (build)

- .NET 8 SDK
- Node.js + npm
- `shellcheck` (bash linter)
- Optional: `shfmt` (bash formatter)

## Build

```bash
make lint          # run all linters
make lint-shell    # shellcheck only
make lint-plugin   # dotnet build with analyzers-as-errors
make lint-vscode   # eslint + typecheck
```

## Status

Work in progress. Not even alpha.
