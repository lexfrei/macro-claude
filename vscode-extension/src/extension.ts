import * as http from 'node:http';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import * as vscode from 'vscode';

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
}

interface BridgeState {
  readonly server: http.Server;
  readonly lockPath: string;
  readonly authToken: string;
}

let bridge: BridgeState | undefined;

export function activate(context: vscode.ExtensionContext): void {
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
  const match = resolved.find((entry) => entry.pid === targetPid);
  if (match === undefined) {
    return { focused: false };
  }
  // preserveFocus=false — steal focus to the terminal panel.
  match.terminal.show(false);
  return { focused: true, terminalName: match.terminal.name };
}

async function readBody(req: http.IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of req) {
    chunks.push(chunk as Buffer);
  }
  return Buffer.concat(chunks).toString('utf-8');
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
