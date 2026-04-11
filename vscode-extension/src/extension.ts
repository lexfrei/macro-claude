import { execFile } from 'node:child_process';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as os from 'node:os';
import * as path from 'node:path';
import { promisify } from 'node:util';

import * as vscode from 'vscode';

const execFileAsync = promisify(execFile);

/**
 * Directory that holds one lock file per running VS Code window.
 * File name = vscode.env.sessionId so concurrent windows do not collide.
 */
const LOCK_DIR = path.join(os.homedir(), '.claude', 'macro-claude-bridge');

interface LockFile {
  readonly port: number;
  readonly authToken: string;
  readonly pid: number;
  readonly sessionId: string;
  readonly startedAt: string;
}

interface FocusRequest {
  readonly pid: number;
}

interface FocusResponse {
  readonly focused: boolean;
  readonly terminalName?: string;
  /**
   * Workspace name as reported by `vscode.workspace.name`. Used by the
   * plugin to raise the correct window of multi-window VS Code via
   * Accessibility API — NSRunningApplication.activate can only raise
   * "the foremost window in history" for multi-window apps, so we
   * need a matchable title substring to pick the right one.
   */
  readonly workspaceName?: string;
  /**
   * Absolute filesystem path of the first workspace folder in the
   * window that owns the target PID. The plugin passes this to
   * `open vscode://file/<path>` which routes LaunchServices to the
   * VS Code window already holding that folder — this is robust to
   * fullscreen Spaces and survives claude being launched from a
   * subdirectory of the workspace (where using cwd directly would
   * open a fresh subdirectory-rooted window instead).
   */
  readonly workspaceRoot?: string;
}

interface BridgeState {
  readonly server: http.Server;
  readonly lockPath: string;
  readonly authToken: string;
}

let bridge: BridgeState | undefined;

export function activate(context: vscode.ExtensionContext): void {
  // Best-effort: drop stale lock files from previous extension hosts
  // that crashed or were terminated without running their `dispose`
  // callback. Reload Window is the common case — the old extension
  // host dies, never deletes its lock, and the new host would
  // otherwise pile a new lock on top. We delete any lock whose
  // recorded `pid` is no longer alive, but never touch locks for
  // live processes (they may belong to other windows).
  cleanupStaleLocks();

  const authToken = crypto.randomUUID();
  const configuredPort = vscode.workspace
    .getConfiguration('macroClaude.bridge')
    .get<number>('port', 0);

  const server = http.createServer((req, res) => {
    void handleRequest(req, res, authToken).catch((err: unknown) => {
      console.error('macro-claude bridge: unhandled request error', err);
      if (!res.headersSent) {
        res.writeHead(500, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ error: 'internal' }));
      }
    });
  });

  server.on('error', (err: Error) => {
    console.error('macro-claude bridge: server error', err);
  });

  server.listen(configuredPort, '127.0.0.1', () => {
    const address = server.address();
    if (typeof address !== 'object' || address === null) {
      console.error('macro-claude bridge: failed to bind HTTP socket');
      return;
    }
    const lockPath = writeLockFile(address.port, authToken);
    bridge = { server, lockPath, authToken };
    console.warn(`macro-claude bridge: listening on 127.0.0.1:${address.port.toString()}`);
  });

  context.subscriptions.push({
    dispose: (): void => {
      tearDown();
    },
  });

  context.subscriptions.push(
    vscode.commands.registerCommand('macroClaude.focusTerminalByPid', (pidArg: unknown) => {
      if (typeof pidArg !== 'number' || !Number.isInteger(pidArg) || pidArg <= 0) {
        void vscode.window.showErrorMessage('macro-claude: invalid PID argument');
        return;
      }
      void focusTerminalByPid(pidArg).then((result) => {
        if (!result.focused) {
          void vscode.window.showWarningMessage(
            `macro-claude: no terminal for PID ${pidArg.toString()}`,
          );
        }
      });
    }),
  );
}

export function deactivate(): void {
  tearDown();
}

function tearDown(): void {
  if (bridge === undefined) {
    return;
  }
  try {
    bridge.server.close();
  } catch (err: unknown) {
    console.error('macro-claude bridge: error closing server', err);
  }
  try {
    fs.unlinkSync(bridge.lockPath);
  } catch {
    // Lock file may already be gone — ignore.
  }
  bridge = undefined;
}

async function handleRequest(
  req: http.IncomingMessage,
  res: http.ServerResponse,
  authToken: string,
): Promise<void> {
  const provided = req.headers['x-macro-claude-auth'];
  if (typeof provided !== 'string' || provided !== authToken) {
    res.writeHead(401, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: 'unauthorized' }));
    return;
  }

  if (req.method !== 'POST' || req.url !== '/focus') {
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: 'not found' }));
    return;
  }

  const body = await readBody(req);
  const payload = parseFocusRequest(body);
  if (payload === undefined) {
    res.writeHead(400, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: 'invalid payload, expected {"pid": number}' }));
    return;
  }

  const result = await focusTerminalByPid(payload.pid);
  res.writeHead(result.focused ? 200 : 404, { 'content-type': 'application/json' });
  res.end(JSON.stringify(result));
}

function parseFocusRequest(body: string): FocusRequest | undefined {
  let raw: unknown;
  try {
    raw = JSON.parse(body);
  } catch {
    return undefined;
  }
  if (typeof raw !== 'object' || raw === null) {
    return undefined;
  }
  const pid = (raw as { pid?: unknown }).pid;
  if (typeof pid !== 'number' || !Number.isInteger(pid) || pid <= 0) {
    return undefined;
  }
  return { pid };
}

async function focusTerminalByPid(targetPid: number): Promise<FocusResponse> {
  const terminals = vscode.window.terminals;
  const resolved = await Promise.all(
    terminals.map(async (terminal) => {
      const pid = await terminal.processId;
      return { terminal, pid };
    }),
  );

  // Fast path: the caller handed us the exact shell PID that VS Code
  // reports for the terminal. Rare in the macro-claude flow because
  // Claude Code runs as a child of the shell, but cheap to check first.
  const direct = resolved.find((entry) => entry.pid === targetPid);
  if (direct !== undefined) {
    direct.terminal.show(false);
    return buildSuccess(direct.terminal.name);
  }

  // Walk upward from targetPid once and reuse the ancestor set for
  // both the integrated-terminal check and the extension-host check.
  // The set always contains at least targetPid itself — ancestorsOf
  // seeds the chain before calling ps — so there is no empty-set
  // early exit to take here.
  const ancestors = await ancestorsOf(targetPid);

  // Terminal path: targetPid is a descendant of a bash/zsh running
  // inside one of this window's integrated terminals. The shell PID
  // is what `terminal.processId` returns, so match against that.
  const viaAncestor = resolved.find(
    (entry) => typeof entry.pid === 'number' && ancestors.has(entry.pid),
  );
  if (viaAncestor !== undefined) {
    viaAncestor.terminal.show(false);
    return buildSuccess(viaAncestor.terminal.name);
  }

  // Extension-host path: the Anthropic Claude Code VS Code extension
  // spawns `claude` as a child of the Extension Host process (a.k.a.
  // "Code Helper (Plugin)"). Those subprocesses are NOT under a
  // terminal shell, so the terminal check above misses them entirely.
  // But `process.pid` from this extension is exactly the Extension
  // Host PID of *this window*. If it shows up in the target's ancestry
  // then the caller is running inside this window's extension host —
  // which means this is the correct window to raise.
  if (ancestors.has(process.pid)) {
    return buildSuccess('claude-code extension');
  }

  return { focused: false };
}

/**
 * Build a success response for the plugin, including whatever window
 * identity we can extract. exactOptionalPropertyTypes forbids
 * undefined-valued optional props, so we construct the response
 * incrementally and only attach keys when we actually have values.
 *
 * workspaceRoot selection priority:
 *   1. `vscode.workspace.workspaceFile` when it exists and is a
 *      `file://` URI — this is the path to a .code-workspace file.
 *      For multi-root workspaces that is the only identity `open
 *      vscode://file/<path>` routes to the existing window instead
 *      of creating a new single-folder window for the first root.
 *   2. `workspaceFolders[0].uri.fsPath` — falls back to the first
 *      (and often only) folder for regular single-folder windows.
 *
 * Untitled / in-memory workspaces have a `workspaceFile` with scheme
 * `untitled:` — we ignore those because LaunchServices cannot open
 * them via URL.
 */
function buildSuccess(terminalName: string): FocusResponse {
  const base: FocusResponse = { focused: true, terminalName };
  const workspaceName = vscode.workspace.name;
  const workspaceRoot = pickWorkspaceRoot();
  if (workspaceName !== undefined && workspaceRoot !== undefined) {
    return { ...base, workspaceName, workspaceRoot };
  }
  if (workspaceName !== undefined) {
    return { ...base, workspaceName };
  }
  if (workspaceRoot !== undefined) {
    return { ...base, workspaceRoot };
  }
  return base;
}

function pickWorkspaceRoot(): string | undefined {
  const workspaceFile = vscode.workspace.workspaceFile;
  if (workspaceFile?.scheme === 'file') {
    return workspaceFile.fsPath;
  }
  const folders = vscode.workspace.workspaceFolders;
  return folders?.[0]?.uri.fsPath;
}

/**
 * Collect `targetPid` plus every ancestor up to init via a single
 * `ps -axo pid=,ppid=` call. One process-list snapshot is cheaper and
 * race-freer than N sequential `ps -o ppid= -p <pid>` calls.
 */
async function ancestorsOf(targetPid: number): Promise<Set<number>> {
  const chain = new Set<number>();
  chain.add(targetPid);

  let snapshot: Map<number, number>;
  try {
    snapshot = await readProcessTree();
  } catch (err: unknown) {
    console.error('macro-claude bridge: failed to read process tree', err);
    return chain;
  }

  let current: number | undefined = snapshot.get(targetPid);
  const safety = 64;
  for (let i = 0; i < safety && current !== undefined && current > 1; i++) {
    if (chain.has(current)) {
      break;
    }
    chain.add(current);
    current = snapshot.get(current);
  }
  return chain;
}

/**
 * Parse `ps -axo pid=,ppid=` output into a `{ pid → ppid }` map.
 */
async function readProcessTree(): Promise<Map<number, number>> {
  const { stdout } = await execFileAsync('/bin/ps', ['-axo', 'pid=,ppid=']);
  const tree = new Map<number, number>();
  for (const line of stdout.split('\n')) {
    const trimmed = line.trim();
    if (trimmed.length === 0) {
      continue;
    }
    const parts = trimmed.split(/\s+/);
    if (parts.length < 2) {
      continue;
    }
    const pidStr = parts[0];
    const ppidStr = parts[1];
    if (pidStr === undefined || ppidStr === undefined) {
      continue;
    }
    const pid = Number.parseInt(pidStr, 10);
    const ppid = Number.parseInt(ppidStr, 10);
    if (Number.isInteger(pid) && Number.isInteger(ppid) && pid > 0) {
      tree.set(pid, ppid);
    }
  }
  return tree;
}

async function readBody(req: http.IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of req) {
    chunks.push(chunk as Buffer);
  }
  return Buffer.concat(chunks).toString('utf-8');
}

function cleanupStaleLocks(): void {
  let entries: string[];
  try {
    entries = fs.readdirSync(LOCK_DIR);
  } catch {
    // Directory does not exist yet — nothing to clean.
    return;
  }

  for (const entry of entries) {
    if (!entry.endsWith('.lock')) {
      continue;
    }
    const full = path.join(LOCK_DIR, entry);
    let lockData: LockFile;
    try {
      const raw = fs.readFileSync(full, 'utf-8');
      lockData = JSON.parse(raw) as LockFile;
    } catch {
      // Corrupt lock — delete it.
      try {
        fs.unlinkSync(full);
      } catch {
        // Ignore unlink errors; we'll try again next activation.
      }
      continue;
    }
    if (typeof lockData.pid !== 'number') {
      continue;
    }
    if (isProcessAlive(lockData.pid)) {
      continue;
    }
    try {
      fs.unlinkSync(full);
    } catch {
      // Ignore unlink errors; we'll try again next activation.
    }
  }
}

/**
 * POSIX trick: `kill(pid, 0)` sends no signal but returns 0 if the
 * caller can reach the process, and throws ESRCH if it no longer
 * exists. Node maps this to `process.kill(pid, 0)`.
 */
function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

function writeLockFile(port: number, authToken: string): string {
  const sessionId = vscode.env.sessionId;
  const lockFile: LockFile = {
    port,
    authToken,
    pid: process.pid,
    sessionId,
    startedAt: new Date().toISOString(),
  };
  const lockPath = path.join(LOCK_DIR, `${sessionId}.lock`);
  fs.mkdirSync(LOCK_DIR, { recursive: true, mode: 0o700 });
  fs.writeFileSync(lockPath, JSON.stringify(lockFile, null, 2), { mode: 0o600 });
  return lockPath;
}
