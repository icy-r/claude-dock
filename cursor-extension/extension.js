// Claude Dock Focus: watches ~/.claude-dock/focus-request.json (written by the Claude Dock
// Command Palette extension). If one of this window's terminals is in the requested process
// chain, show that terminal and answer with this window's workspace name so the dock can bring
// the right window to the front. Runs as a UI extension, so it works for local and Remote-WSL windows.
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

const dir = path.join(os.homedir(), '.claude-dock');
const requestFile = path.join(dir, 'focus-request.json');
let lastId = '';

async function handleRequest() {
  let req;
  try {
    req = JSON.parse(fs.readFileSync(requestFile, 'utf8'));
  } catch {
    return;
  }
  if (!req || !req.id || req.id === lastId || Date.now() - req.ts > 5000) {
    return;
  }
  const pids = new Set(req.pids || []);
  for (const terminal of vscode.window.terminals) {
    let pid;
    try {
      pid = await terminal.processId;
    } catch {
      continue;
    }
    if (pid && pids.has(pid)) {
      lastId = req.id;
      terminal.show(false);
      const answer = { ok: true, workspaceName: vscode.workspace.name || '', terminal: terminal.name };
      fs.writeFileSync(path.join(dir, `focus-response-${req.id}.json`), JSON.stringify(answer));
      return;
    }
  }
}

function activate(context) {
  fs.mkdirSync(dir, { recursive: true });
  let pending;
  const watcher = fs.watch(dir, (_event, file) => {
    if (file === 'focus-request.json') {
      clearTimeout(pending);
      pending = setTimeout(handleRequest, 30);
    }
  });
  context.subscriptions.push({ dispose: () => watcher.close() });
}

function deactivate() {}

module.exports = { activate, deactivate };
