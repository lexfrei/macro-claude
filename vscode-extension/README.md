# macro-claude VS Code Bridge

Companion extension for [macro-claude](https://github.com/lexfrei/macro-claude) —
a Logi MX Creative Console plugin that shows live Claude Code session
status on macropad keys.

This extension publishes a local HTTP bridge on `127.0.0.1` so the
macropad plugin can focus a specific integrated terminal by process ID
when you press its key. The bridge binds to a random port and writes
`~/.claude/macro-claude-bridge/<vscode.env.sessionId>.lock` with a
per-window auth token, so multiple VS Code windows are handled
independently.

The extension does nothing on its own — it only becomes useful when
the macro-claude macropad plugin is installed and hooked up.

## Privacy

The bridge binds to `127.0.0.1` only, accepts requests authenticated by
a random-UUID token from the lock file, and does not make any outbound
connections. No telemetry. The only data it reads is
`vscode.window.terminals` and each terminal's `processId`.

## Building locally

```sh
npm ci
npm run compile
npx vsce package --no-dependencies --allow-missing-repository
code --install-extension macro-claude-bridge-*.vsix
```

## License

MIT. See the root [LICENSE](../LICENSE) file in the macro-claude repo.
