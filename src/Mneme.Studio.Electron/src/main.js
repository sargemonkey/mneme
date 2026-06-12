'use strict';

const { app, BrowserWindow, ipcMain, dialog } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { spawn } = require('node:child_process');
const { DatabaseSync } = require('node:sqlite');

// --- Config (env-overridable, with sensible defaults) -----------------------
//
// MNEME_WORKSTREAM_ID  default: copilot-cli (so it points at the same DB the
//                      Copilot CLI MCP server writes to — live-view of the
//                      same memory the user is building up via chat).
// MNEME_SQLITE_PATH    default: ~/.mneme/copilot.db
// MNEME_USER_ID        default: $env:USERNAME or $env:USER
// MNEME_CLI_PATH       default: ../../Mneme.Cli/bin/Debug/net8.0/Mneme.Cli.exe
//                      relative to this file.
const homedir = os.homedir();
const cfg = {
    workstream: process.env.MNEME_WORKSTREAM_ID || 'copilot-cli',
    sqlitePath: process.env.MNEME_SQLITE_PATH || path.join(homedir, '.mneme', 'copilot.db'),
    userId:     process.env.MNEME_USER_ID     || os.userInfo().username,
    cliPath:    process.env.MNEME_CLI_PATH    || path.resolve(
                    __dirname,
                    '..',
                    '..',
                    'Mneme.Cli',
                    'bin',
                    'Debug',
                    'net8.0',
                    process.platform === 'win32' ? 'Mneme.Cli.exe' : 'Mneme.Cli'),
};

let db = null;
let mainWindow = null;
let pollTimer = null;
let lastMtime = 0;
let lastRowCount = -1;

function openDb() {
    if (db) return db;
    if (!fs.existsSync(cfg.sqlitePath)) {
        return null;
    }
    db = new DatabaseSync(cfg.sqlitePath, { readOnly: true });
    db.exec('PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;');
    return db;
}

function readEvents(limit = 200) {
    const conn = openDb();
    if (!conn) return [];
    const rows = conn.prepare(`
        SELECT e.event_id, e.workstream_id, e.event_channel, e.category, e.classification,
               e.valid_at, e.created_at, e.payload_json,
               r.revoked_at AS revoked_at
        FROM memory_events e
        LEFT JOIN memory_revocations r ON r.event_id = e.event_id
        WHERE e.workstream_id = ?
        ORDER BY e.created_at DESC
        LIMIT ?
    `).all(cfg.workstream, limit);
    return rows.map(r => ({
        event_id: r.event_id,
        category: ['Evidence','Fact','Decision','Hypothesis','Goal','Action','Outcome'][r.category] ?? `cat${r.category}`,
        classification: ['Public','Internal','Confidential','Secret','Pii'][r.classification] ?? `cls${r.classification}`,
        channel: r.event_channel === 0 ? 'Epistemic' : 'Technical',
        valid_at: r.valid_at,
        created_at: r.created_at,
        payload_json: r.payload_json,
        is_revoked: r.revoked_at != null,
    }));
}

function readCurations(limit = 200) {
    const conn = openDb();
    if (!conn) return [];
    const rows = conn.prepare(`
        SELECT event_id, target_event_id, curation_type, curator, rationale,
               occurred_at, reverted_by
        FROM curation_events
        WHERE workstream_id = ?
        ORDER BY occurred_at DESC
        LIMIT ?
    `).all(cfg.workstream, limit);
    const typeNames = ['Amended','Annotated','Pinned','Demoted','Split','Merged','Reverted'];
    return rows.map(r => ({
        curation_event_id: r.event_id,
        target_event_id: r.target_event_id,
        type: typeNames[r.curation_type] ?? `type${r.curation_type}`,
        curator: r.curator,
        rationale: r.rationale,
        occurred_at: r.occurred_at,
        is_reverted: r.reverted_by != null,
    }));
}

function readMetrics() {
    const conn = openDb();
    if (!conn) return { events: 0, revoked: 0, curations: 0, ready: false };
    const total = conn.prepare('SELECT COUNT(*) AS n FROM memory_events WHERE workstream_id = ?').get(cfg.workstream).n;
    const rev = conn.prepare('SELECT COUNT(*) AS n FROM memory_revocations WHERE workstream_id = ?').get(cfg.workstream).n;
    const cur = conn.prepare('SELECT COUNT(*) AS n FROM curation_events WHERE workstream_id = ?').get(cfg.workstream).n;
    const byCat = conn.prepare(`SELECT category, COUNT(*) AS n FROM memory_events WHERE workstream_id = ? GROUP BY category`).all(cfg.workstream);
    return { events: total, revoked: rev, curations: cur, byCategory: byCat, ready: true };
}

function workstreams() {
    const conn = openDb();
    if (!conn) return [cfg.workstream];
    const rows = conn.prepare('SELECT DISTINCT workstream_id FROM memory_events ORDER BY workstream_id').all();
    const list = rows.map(r => r.workstream_id);
    if (!list.includes(cfg.workstream)) list.unshift(cfg.workstream);
    return list;
}

function runCli(args) {
    return new Promise((resolve, reject) => {
        if (!fs.existsSync(cfg.cliPath)) {
            return reject(new Error(`Mneme.Cli not found at ${cfg.cliPath}. Build it: dotnet build src/Mneme.Cli/Mneme.Cli.csproj`));
        }
        const child = spawn(cfg.cliPath, args, {
            env: {
                ...process.env,
                MNEME_WORKSTREAM_ID: cfg.workstream,
                MNEME_SQLITE_PATH:   cfg.sqlitePath,
                MNEME_USER_ID:       cfg.userId,
            },
        });
        let stdout = '';
        let stderr = '';
        child.stdout.on('data', d => { stdout += d.toString(); });
        child.stderr.on('data', d => { stderr += d.toString(); });
        child.on('error', reject);
        child.on('close', code => {
            const trimmed = stdout.trim();
            if (code !== 0) {
                return reject(new Error(stderr.trim() || `Mneme.Cli exited with ${code}`));
            }
            try { resolve(JSON.parse(trimmed.split('\n').pop())); }
            catch { resolve({ ok: true, raw: trimmed }); }
        });
    });
}

function startPolling() {
    if (pollTimer) return;
    pollTimer = setInterval(() => {
        try {
            const mt = fs.existsSync(cfg.sqlitePath) ? fs.statSync(cfg.sqlitePath).mtimeMs : 0;
            // Cheap row-count check on top of mtime so we also see new rows
            // committed while the file existed already.
            const conn = openDb();
            const rc = conn ? conn.prepare('SELECT COUNT(*) AS n FROM memory_events WHERE workstream_id = ?').get(cfg.workstream).n : 0;
            if (mt !== lastMtime || rc !== lastRowCount) {
                lastMtime = mt;
                lastRowCount = rc;
                if (mainWindow && !mainWindow.isDestroyed()) {
                    mainWindow.webContents.send('mneme:changed');
                }
            }
        } catch (e) {
            console.error('poll error', e);
        }
    }, 1000);
}

function setWorkstream(ws) {
    cfg.workstream = ws;
    lastRowCount = -1;
}
function setSqlitePath(p) {
    if (db) { db.close(); db = null; }
    cfg.sqlitePath = p;
    lastMtime = 0; lastRowCount = -1;
}

function createWindow() {
    mainWindow = new BrowserWindow({
        width: 1400, height: 900,
        title: 'Mneme.Studio',
        webPreferences: {
            preload: path.join(__dirname, 'preload.js'),
            contextIsolation: true,
            nodeIntegration: false,
        },
    });
    mainWindow.removeMenu();
    mainWindow.loadFile(path.join(__dirname, 'renderer.html'));
}

app.whenReady().then(() => {
    ipcMain.handle('mneme:config',       () => ({ ...cfg }));
    ipcMain.handle('mneme:metrics',      () => readMetrics());
    ipcMain.handle('mneme:events',       (_, n) => readEvents(n || 200));
    ipcMain.handle('mneme:curations',    (_, n) => readCurations(n || 200));
    ipcMain.handle('mneme:workstreams',  () => workstreams());
    ipcMain.handle('mneme:setWorkstream',(_, ws) => { setWorkstream(ws); return true; });
    ipcMain.handle('mneme:pickDatabase', async () => {
        const r = await dialog.showOpenDialog(mainWindow, {
            title: 'Select Mneme SQLite database',
            properties: ['openFile'],
            filters: [{ name: 'SQLite', extensions: ['db', 'sqlite', 'sqlite3'] }],
        });
        if (r.canceled || r.filePaths.length === 0) return null;
        setSqlitePath(r.filePaths[0]);
        return cfg.sqlitePath;
    });
    ipcMain.handle('mneme:cli', async (_, args) => {
        if (!Array.isArray(args)) throw new Error('args must be string[]');
        return await runCli(args);
    });
    startPolling();
    createWindow();
});

app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit();
});
