# CLAUDE.md — project-specific guidance for macro-claude

These instructions are additive to `~/CLAUDE.md`. Anything in here overrides
the global defaults for this repository only.

## What this project is

macro-claude shows the live state of running Claude Code sessions on a
Logitech MX Creative Console (MXCC) macropad. It has three moving parts that
communicate exclusively through files under `~/.claude/`:

1. **`hooks/`** — bash scripts invoked by Claude Code hook events. They
   write a per-session JSON file to `~/.claude/session-status/<sid>.json`.
2. **`plugin/MacroClaudePlugin/`** — C# 8 / .NET 8 plugin loaded by Logi
   Plugin Service (LPS). Watches three source-of-truth directories, polls
   CPU, resolves state, renders 80×80 button bitmaps, dispatches focus
   requests on press.
3. **`vscode-extension/`** — TypeScript companion extension. Exposes a
   local HTTP `/focus` endpoint so the plugin can raise a specific
   integrated terminal by PID.

There is a fourth directory, **`plugin/MacroClaudePlugin.Tests/`**, with an
xunit project that covers pure logic via linked-source compilation.

## Strict-first non-negotiables

These are hard requirements enforced by CI and the strict linter config.
**Do not relax any of these to "make the build green"** — fix the code
instead, or open a discussion before touching the rule.

- `TreatWarningsAsErrors=true`, `AnalysisMode=All`, `AnalysisLevel=latest`,
  `EnforceCodeStyleInBuild=true`. See `Directory.Build.props` at the repo
  root.
- `Nullable=enable` on every C# project. No `#nullable disable` pragmas.
- File-scoped namespaces in every new file (IDE0161 is an error).
- StyleCop.Analyzers + Roslynator.Analyzers + Roslynator.Formatting.Analyzers
  are imported via the root `Directory.Build.props`. The plugin csproj
  chains to that via `Import Project="..\..\..\Directory.Build.props"`.
  **Exception**: the `plugin/MacroClaudePlugin.Tests/` csproj does NOT
  inherit the analyzer packs directly — it consumes the pure-logic files as
  linked sources. That keeps the test project free of LPS-specific build
  targets.
- TypeScript in `vscode-extension/` must pass `tsc --noEmit` with
  `strict: true`, `noUncheckedIndexedAccess: true`,
  `exactOptionalPropertyTypes: true`, plus the typescript-eslint
  `strict-type-checked` preset.
- Bash hooks must pass
  `shellcheck --severity=style --external-sources --enable=all`.

If you find yourself silencing a rule in `.editorconfig`, stop and audit
whether the suppression already exists at the root. The root
`.editorconfig` contains a deliberate list of pragmatic exceptions — grow
that list only when the rule is genuinely noise for this codebase.

## Loupedeck / LPS gotchas (stuff that cost us hours)

### 1. `MacroClaudeApplication.cs` MUST exist even when `HasNoApplication=true`

LPS refuses to load any plugin assembly that does not contain at least one
concrete subclass of `Loupedeck.ClientApplication`. The failure mode is:

```
ERROR | Cannot load plugin from '.../MacroClaudePlugin.dll'
WARN  | Plugin 'MacroClaude' added to disabled plugins list
```

The load attempt returns in ~7 ms (before `Plugin.Load` can run) and no
exception is logged anywhere. This is **not** documented by Logitech. It is
a hard requirement buried in LPS reflection discovery.

An `AssemblyLoadContext.LoadFromAssemblyPath` load of the same DLL from a
standalone .NET host succeeds — the constraint is LPS-specific.

**Rule**: Never delete `plugin/MacroClaudePlugin/src/MacroClaudeApplication.cs`.
It is intentionally a stub (empty process name, empty bundle name,
`ClientApplicationStatus.Unknown`). `MacroClaudePlugin.UsesApplicationApiOnly`
and `MacroClaudePlugin.HasNoApplication` keep macro-claude a universal
plugin regardless. The relevant commit is `e48d86c`.

### 2. LPS does NOT surface any useful error message

When the plugin fails to load, `~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/MacroClaude.log`
contains only the "Cannot load plugin" line. The macOS unified log
(`log show --predicate 'process == "LogiPluginService"'`) is mostly
`SecKeyVerifySignature` noise — trust evaluation fires for every plugin
load attempt including successful ones, so a `Trust evaluate failure:
[leaf MissingIntermediate]` line is **not** an error and does not indicate
the actual problem.

**Diagnosis pattern**: if LPS fails to load the plugin, build a minimal
.NET console app that calls `AssemblyLoadContext.LoadFromAssemblyPath` on
the built DLL. If that succeeds, the issue is LPS-specific (convention,
metadata, missing class), not a runtime loader fault.

### 3. LPS holds an in-memory "disabled plugins" list

Once LPS fails to load a plugin, it marks it disabled for the lifetime of
the LPS process. Subsequent rebuilds don't retry. The cleanest recovery is:

```bash
# from a fresh build after the real fix
touch "$HOME/Library/Application Support/Logi/LogiPluginService/Plugins/MacroClaudePlugin.link"
```

Touching the `.link` file triggers the LPS FileSystemWatcher on the plugins
directory and forces a re-scan. If that doesn't work, the nuclear option
is `launchctl kickstart -k gui/$(id -u)/application.com.logi.pluginservice.*`
(LPS is managed by launchd and restarts automatically).

### 4. LPS Postbuild reload uses a URL scheme

The generated csproj has a Postbuild target that runs
`open loupedeck:plugin/MacroClaude/reload`. This is the documented reload
mechanism. It works only when LPS has not disabled the plugin. If the
plugin is disabled, fall back to touching the `.link` file.

### 5. `CopyLocalLockFileAssemblies=true` is not the enemy

The generated Loupedeck csproj sets `CopyLocalLockFileAssemblies=true`.
This copies many Loupedeck internal DLLs (`SkiaSharp`, `YamlDotNet`,
`websocket-sharp`, `ExCSS`, `Svg.*`) into `bin/Debug/bin/`. LPS already
has these loaded as part of its own process — the copies are effectively
redundant but harmless.

Do NOT disable `CopyLocalLockFileAssemblies=true` on the plugin csproj —
it breaks the Postbuild `CopyPackage` target which needs the
`bin/{Configuration}/bin/` layout.

### 6. `deps.json` with analyzer references is fine

An early diagnostic theory was that `StyleCop.Analyzers` listed in
`MacroClaudePlugin.deps.json` was causing load failures. It wasn't — the
real cause was #1 above. `deps.json` listing analyzer packages is a
cosmetic artefact of `PrivateAssets="all"` not propagating through
`CopyLocalLockFileAssemblies`. It does not break LPS load.

## The state resolver — read this before touching it

The state model is intentional and locked by unit tests. Six states, each
tied to a composite of four signals:

| State    | Meaning                                        | Color (RGB)      |
| -------- | ---------------------------------------------- | ---------------- |
| gone     | No PID in `~/.claude/sessions/`                | (30, 30, 30)     |
| idle     | Last hook = `Stop` or `SessionStart`           | (30, 150, 70)    |
| working  | UserPromptSubmit/Pre/PostToolUse + heartbeat < 3s | (30, 100, 200) |
| thinking | Heartbeat 3–30s + cpu > 1.0%                   | (30, 180, 200)   |
| stuck    | Heartbeat > 30s + cpu < 0.5% (or middle band)  | (220, 120, 30)   |
| error    | `StopFailure` hook OR JSONL tail has `[Request interrupted by user]` | (200, 50, 50) |

The **why** of combining four signals:

1. Claude Code does not fire a hook on `Esc` / `Ctrl+C` interrupt. The SE
   executor short-circuits on `signal.aborted`. The ONLY durable marker of
   a user interrupt is the string `[Request interrupted by user]` in the
   session JSONL transcript.
2. Extended thinking can run 30–60 seconds without writing anything to the
   JSONL — thinking blocks are written in a single chunk at the end. So
   JSONL mtime alone cannot detect a thinking session, you need CPU usage.
3. Without CPU signal, all interrupts look indistinguishable from long
   thinking sessions.

Thresholds are constants on `StateResolver`:

```csharp
public static readonly TimeSpan FreshHeartbeatWindow = TimeSpan.FromSeconds(3);
public static readonly TimeSpan StaleHeartbeatWindow = TimeSpan.FromSeconds(30);
public const Double CpuActiveThreshold = 1.0;
public const Double CpuIdleThreshold = 0.5;
```

If you change these, update the unit tests in
`plugin/MacroClaudePlugin.Tests/StateResolverTests.cs` explicitly — do not
rely on the tests accidentally still passing.

## Hook wire format

The bash hook script (`hooks/session-monitor.sh`) reads the hook JSON from
stdin and writes `~/.claude/session-status/<sid>.json` with this exact
schema:

```json
{
  "session_id":     "<uuid>",
  "cwd":            "/abs/path",
  "status":         "idle" | "working" | "error",
  "last_event":     "Stop" | "UserPromptSubmit" | "PreToolUse" | ...,
  "last_event_s":   1775915141,
  "heartbeat_s":    1775915141,
  "turn_started_s": 1775912393,
  "idle_since_s":   null
}
```

Timestamps are **Unix seconds**, not milliseconds. macOS `date +%s` only
returns seconds, and we do not need millisecond precision for state
resolution. If you add fields, keep the `_s` suffix convention for unix
time fields so the plugin parser can be trivially extended.

The plugin's `StatusReader.TryReadStatusFile` is deliberately tolerant of
partial writes (`catch (IOException)` is intentional — the hook and the
reader race on every event). Do not tighten that exception handling.

## Components by directory

### `hooks/`

- `session-monitor.sh` — the hook entry point. Do not add CLI flags or
  positional args to this script; its signature is "read JSON from stdin,
  write JSON to session-status/". Hook events are dispatched by name from
  the JSON's `hook_event_name` field.
- `install.sh` — idempotent installer. Backs up `settings.json` before
  every run. Supports `--uninstall`. Must remain idempotent — re-running
  after a clean install should be a no-op.

Both scripts use `set -o errexit -o nounset -o pipefail` and pass
shellcheck strict mode. **macOS BSD `chmod` does NOT support `--` as an
end-of-options separator** — the global CLAUDE.md says "full flag names"
but that BSD tool is an exception. Use `chmod +x <path>` without `--`.
See commit `f2fe723`.

### `plugin/MacroClaudePlugin/`

Directory layout under `src/`:

```
src/
├── Actions/
│   └── SessionStatusCommand.cs    PluginDynamicCommand with 9 slot parameters
├── Focus/
│   ├── FocusDispatcher.cs         VS Code bridge HTTP + iTerm2 app activate
│   └── NativeActivator.cs         P/Invoke libobjc.A.dylib
├── Helpers/
│   ├── PluginLog.cs               thin static adapter over PluginLogFile
│   └── PluginResources.cs         thin static adapter over Assembly resources
├── Status/
│   ├── SessionSnapshot.cs         immutable record + SessionState enum
│   ├── StateResolver.cs           pure function, the state machine
│   ├── StatusReader.cs            FileSystemWatcher + CPU poller + JSONL scanner
│   ├── SlotAssigner.cs            thread-safe session_id → slot index mapping
│   └── SlotBus.cs                 static event bus bridging Plugin.Load ↔ Command
├── MacroClaudePlugin.cs           entry point (Plugin subclass)
├── MacroClaudeApplication.cs      REQUIRED STUB — see gotcha #1
├── MacroClaudePlugin.csproj
├── Directory.Build.props          imports the repo-root Directory.Build.props
├── .editorconfig                  root=false, tune Logitech conventions locally
└── package/metadata/
    ├── LoupedeckPackage.yaml
    └── Icon256x256.png            3×3 grid in the state palette
```

### `plugin/MacroClaudePlugin.Tests/`

xunit test project that consumes `StateResolver.cs`, `SessionSnapshot.cs`,
and `SlotAssigner.cs` as **linked sources**, not via a ProjectReference.
This is deliberate — a ProjectReference to `MacroClaudePlugin` drags in
the PluginApi native reference and the LPS Postbuild targets, neither of
which belong in a test project.

```xml
<Compile Include="..\MacroClaudePlugin\src\Status\StateResolver.cs">
  <Link>Linked\StateResolver.cs</Link>
</Compile>
```

If you add a new file with pure logic to `plugin/MacroClaudePlugin/src/`
that you want tested, add a matching `<Compile Include>` here.

### `vscode-extension/`

Single-file TypeScript extension. Key design points:

- HTTP server binds `127.0.0.1` only. No public network surface.
- Per-window lock file at
  `~/.claude/macro-claude-bridge/<vscode.env.sessionId>.lock`. This
  isolates concurrent VS Code windows — the plugin enumerates all lock
  files and tries each bridge until one reports success.
- Auth via `x-macro-claude-auth` header with a `crypto.randomUUID()` token
  generated at activation. The token is written into the lock file so
  only local processes that can read the lock file can authenticate.

## Common commands

```bash
# --- build ---
dotnet build plugin/MacroClaudePlugin/src/MacroClaudePlugin.csproj
dotnet build plugin/MacroClaudePlugin/src/MacroClaudePlugin.csproj --configuration Release

# --- test ---
dotnet test plugin/MacroClaudePlugin.Tests/MacroClaudePlugin.Tests.csproj

# --- lint ---
shellcheck --severity=style --external-sources --enable=all hooks/*.sh
(cd vscode-extension && npm run typecheck && npm run lint)

# --- package ---
dotnet logiplugintool pack plugin/MacroClaudePlugin/bin/Release dist/MacroClaudePlugin.lplug4
dotnet logiplugintool verify dist/MacroClaudePlugin.lplug4
dotnet logiplugintool metadata dist/MacroClaudePlugin.lplug4

# --- hooks install / uninstall ---
bash hooks/install.sh
bash hooks/install.sh --uninstall

# --- LPS force reload ---
touch "$HOME/Library/Application Support/Logi/LogiPluginService/Plugins/MacroClaudePlugin.link"

# --- inspect LPS plugin log ---
cat "$HOME/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/MacroClaude.log"

# --- smoke test the hook script without installing ---
printf '{"session_id":"TEST","hook_event_name":"UserPromptSubmit","cwd":"/tmp"}\n' \
  | bash hooks/session-monitor.sh
cat ~/.claude/session-status/TEST.json
```

## Validation checklist before a PR

1. `dotnet build plugin/MacroClaudePlugin/src/MacroClaudePlugin.csproj` — 0 warnings, 0 errors
2. `dotnet test plugin/MacroClaudePlugin.Tests/MacroClaudePlugin.Tests.csproj` — all green
3. `shellcheck --severity=style --enable=all hooks/*.sh` — OK
4. `(cd vscode-extension && npm run typecheck && npm run lint)` — OK
5. Plugin loads into LPS: check
   `~/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/MacroClaude.log`
   for `Plugin 'MacroClaude' version '1.0' loaded from ...`
6. `~/.claude/session-status/` contains live files for active Claude Code
   sessions (verify a hook event is written in under a second)

## Tooling versions pinned

- **.NET 8 SDK** via `brew install dotnet@8`. `dotnet@8` is keg-only, so
  `PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"` is required in any shell
  that runs builds.
- **Node.js 20+** via any supported manager.
- **LogiPluginTool 6.1.4.22672** installed as a local dotnet tool via
  `.config/dotnet-tools.json`. Restore with `dotnet tool restore`.
- **xunit 2.9.2**, **Microsoft.NET.Test.Sdk 17.11.1**.
- **StyleCop.Analyzers 1.2.0-beta.556**, **Roslynator 4.13.1** — pinned in
  root `Directory.Build.props`.

## Things we intentionally do NOT do

- **No `osascript`/`open -a` shell-outs for window focus.** We use
  P/Invoke to `libobjc.A.dylib` instead (`Focus/NativeActivator.cs`). The
  goal is a self-contained plugin without runtime dependencies on macOS
  shell utilities.
- **No `net8.0-macos` target framework.** The `Microsoft.macOS.dll`
  assembly is 30 MB and is not needed for the small set of AppKit calls
  we make — P/Invoke to `libobjc.A.dylib` gives us the same result with
  ~120 lines of C# and zero additional packages.
- **No iTerm2 session-level focus yet.** The protobuf Unix-socket client
  for `api.iterm2.com` is tracked as v2 work. For now iTerm2 focus falls
  through to a bundle-id activate so the user lands in iTerm2 and picks
  the tab manually.
- **No ProjectReference from tests to plugin.** See "linked sources"
  above.
- **No forced `Nullable=disable` in any source file.** Do not add
  `#nullable disable` pragmas to silence warnings — fix the nullability.

## If something feels wrong

- `dotnet build` succeeds but LPS still shows the plugin disabled →
  always the `MacroClaudeApplication.cs` gotcha first. Check the file
  exists and extends `ClientApplication`.
- Hooks stop writing status files → `ls ~/.claude/session-status/`
  should show files mtime'd within the last second of any hook event.
  If stale or missing, `jq '.hooks' ~/.claude/settings.json` and verify
  the `session-monitor.sh` path is still present. Re-run
  `bash hooks/install.sh` if not.
- Button press does nothing for a VS Code terminal → check the lock file
  directory `~/.claude/macro-claude-bridge/`. A missing lock file means
  the companion extension didn't activate — check the VS Code Output
  panel → Extension Host.
- Strict analyzer fires on a rule you think should be disabled → check
  the root `.editorconfig` first. Most pragmatic suppressions are there
  already. Do NOT disable rules per-file.

## Commit conventions

Global CLAUDE.md rules apply. Project-specific additions:

- `feat(plugin)`, `fix(plugin)`, `refactor(plugin)`, `test(plugin)` for
  C# work.
- `feat(vscode-extension)`, `fix(vscode-extension)` for TypeScript.
- `feat(hooks)`, `fix(hooks)` for bash.
- `docs:` for README.
- `chore:` for config, tooling, lockfile updates.

Every commit should leave the repo with a green build, green tests, and
a plugin that loads into LPS.
