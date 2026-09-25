#!/usr/bin/env node
// ATT Prefab Editor — loopback web panel over the PrefabEditorCore console pipe.
//
//   node panel/server.js            # 127.0.0.1:1766 (PANEL_PORT / --port to change)
//
// LOOPBACK ONLY, by construction: the listener binds 127.0.0.1 and refuses any
// other bind host. Nothing new is exposed — remote players can use an SSH tunnel
// or a private Tailscale Serve proxy to this loopback listener:
//
//   ssh -L 1766:127.0.0.1:1766 user@your-server   ->   http://localhost:1766
//
// Auth to the game console: a fresh RS256 token signed from
// UserData/att_console/cli/<kid>.priv.xml (ATT_CONSOLE_DIR et al. respected),
// POSTed to /console on the port in console_port.txt (default 1764). The panel
// itself has no login. Its listener binds loopback only; the Host guard accepts
// loopback and *.ts.net for Tailscale Serve. Use Tailscale ACLs or SSH access as
// the admin gate, since anyone who can reach the panel has full console power.
//
// Endpoints (all JSON):
//   GET  /api/health                     signer + console reachability
//   GET  /api/prefabs?q=&spawnable=1&limit=   search the vendored catalog
//   POST /api/command        { command }              raw console escape hatch
//   POST /api/spawn          { player, prefab }       spawn <player> <prefab>
//   POST /api/select         { id }                   select <id>
//   POST /api/select/get     {}                       select get
//   POST /api/select/move    { x, y, z }              select move exact x,y,z
//   POST /api/select/rotate  { x, y, z }              select rotate exact x,y,z
//   POST /api/select/destroy {}                       select destroy
//   POST /api/destroy        { id }                   wacky destroy <id>
//
// Editor (GET /editor is the 3D viewport page; edit module JSON underneath):
//   GET  /api/edit/players                            edit players
//   POST /api/edit/scan   { player, radius } | { x, y, z, radius }
//   POST /api/edit/info   { id }                      edit info <id>
//   POST /api/edit/move   { id, x, y, z }             edit move (root, synced)
//   POST /api/edit/move-many { ids, dx, dy, dz }      move a checked group of roots
//   POST /api/edit/rotate { id, x, y, z }             edit rotate (euler)
//   POST /api/edit/rotate-many { ids, x, y, z }       rotate selected roots around the primary root
//   POST /api/edit/transform { id, x, y, z, ex, ey, ez } set position + rotation
//   POST /api/edit/scale { id, factor }                scale encoded prefab
//   POST /api/edit/ground { id }                      edit ground (raycast down)
//   POST /api/edit/delete { id, confirm: true }       two-step (server re-checks)
//   POST /api/edit/delete-many { ids, confirm }       preflighted batch delete
//   POST /api/edit/duplicate-many { ids, dx, dy, dz } batch duplicate
//   POST /api/edit/undo | /redo; GET /api/edit/history server session history
//   POST /api/edit/tostring { id }                    save string of one entity (one-shot)
//
// String library (capture/replay half):
//   GET  /api/strings                    the saved library
//   POST /api/strings/capture { id, name }   blueprint string <id> -> library (one-shot)
//   POST /api/strings/spawn   { player, name | string }   spawn string <player> <s>
//   POST /api/strings/delete  { name }
// Library lives in PANEL_STATE_DIR (default panel/state/, gitignored).

'use strict';

const fs = require('fs');
const http = require('http');
const path = require('path');
const { TokenSigner } = require('./lib/signer');
const { ConsolePipe } = require('./lib/consolePipe');

const DEFAULT_PANEL_PORT = 1766;

// ---- vendored catalog --------------------------------------------------------

function loadCatalog(file) {
    let raw;
    try {
        raw = JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch (e) {
        throw new Error('prefab catalog missing or unreadable (' + file + ') — the panel '
            + 'expects server-kit/data/prefabs.json next to the panel folder: ' + e.message);
    }
    const prefabs = raw.prefabs.map((p) => ({
        name: p.name,
        hash: p.hash,
        spawnable: !!p.spawnable,
        // pre-normalized search key: lowercase, separators dropped
        key: p.name.toLowerCase().replace(/[_\s()'-]/g, '')
    }));
    return { count: raw.count, spawnableCount: raw.spawnableCount, prefabs };
}

// Substring match on the normalized key ("iron ore" and "ironore" both hit
// Iron_Ore); a purely numeric query also matches on exact hash.
function searchPrefabs(catalog, q, spawnableOnly, limit) {
    const key = String(q || '').toLowerCase().replace(/[_\s()'-]/g, '');
    const asHash = /^\d+$/.test(String(q || '').trim()) ? parseInt(q, 10) : null;
    const out = [];
    for (const p of catalog.prefabs) {
        if (spawnableOnly && !p.spawnable) { continue; }
        if (key !== '' && !p.key.includes(key) && p.hash !== asHash) { continue; }
        out.push({ name: p.name, hash: p.hash, spawnable: p.spawnable });
        if (out.length >= limit) { break; }
    }
    return out;
}

// ---- console command builders (validated, node-testable) ----------------------

// Free text is never interpolated into a command unchecked: names/ids ride the
// catalog charset, numbers must parse, and anything else goes through the raw
// /api/command box where the operator owns what they typed (control chars still
// stripped so a payload can't smuggle a second line into the pipe).
const NAME_RE = /^[A-Za-z0-9 _()'.-]{1,80}$/;

function cleanCommand(s) {
    if (typeof s !== 'string') { throw new PanelError('command must be a string'); }
    const cmd = s.replace(/[\r\n\t\0]/g, ' ').trim();
    if (cmd === '') { throw new PanelError('empty command'); }
    return cmd;
}

function num(v, what) {
    const n = typeof v === 'number' ? v : parseFloat(v);
    if (!isFinite(n)) { throw new PanelError(what + ' must be a finite number'); }
    return n;
}

// A coordinate the console's float parser will definitely read back: finite,
// world-sized, 3 decimals, and never in exponential notation.
function coord(v, what) {
    const n = num(v, what);
    if (Math.abs(n) > 100000) { throw new PanelError(what + ' is outside the world (|v| > 100000)'); }
    return String(Math.round(n * 1000) / 1000);
}

function intId(v, what) {
    const n = typeof v === 'number' ? v : parseInt(v, 10);
    if (!Number.isInteger(n) || n < 0) { throw new PanelError(what + ' must be a non-negative integer'); }
    return n;
}

function nameOrId(v, what) {
    const s = String(v == null ? '' : v).trim();
    if (s === '' || !NAME_RE.test(s)) { throw new PanelError(what + ' has characters the console pipe will not carry'); }
    // quote multi-word names the way the bot does; ids and single tokens ride bare
    return s.includes(' ') ? '"' + s + '"' : s;
}

// Save strings are the game's comma-separated encoding, two halves joined by
// '|' — numbers and separators only. Anything else does not go into the pipe.
const SAVE_STRING_RE = /^[\d,|\s.+-]+$/;

function saveString(v, what) {
    const s = String(v == null ? '' : v).trim();
    if (s === '' || s.length > 200000 || !SAVE_STRING_RE.test(s)) {
        throw new PanelError(what + ' is not a valid save string (digits/commas/pipe only)');
    }
    return s;
}

function libName(v) {
    const s = String(v == null ? '' : v).trim().toLowerCase();
    if (!/^[a-z0-9_-]{1,40}$/.test(s)) {
        throw new PanelError('string names: letters/digits/dash/underscore, 1-40 chars');
    }
    return s;
}

const builders = {
    spawn: (b) => 'spawn ' + nameOrId(b.player, 'player') + ' ' + nameOrId(b.prefab, 'prefab'),
    select: (b) => 'select ' + intId(b.id, 'id'),
    selectGet: () => 'select get',
    selectMove: (b) => 'select move exact ' + num(b.x, 'x') + ',' + num(b.y, 'y') + ',' + num(b.z, 'z'),
    selectRotate: (b) => 'select rotate exact ' + num(b.x, 'x') + ',' + num(b.y, 'y') + ',' + num(b.z, 'z'),
    selectDestroy: () => 'select destroy',
    destroy: (b) => 'wacky destroy ' + intId(b.id, 'id'),
    selectToString: () => 'select tostring',
    spawnString: (player, s) => 'spawn string ' + nameOrId(player, 'player') + ' ' + saveString(s, 'string'),
    editPlayers: () => 'edit players',
    editScan: (b) => 'edit scan ' + nameOrId(b.player, 'player') + ' ' + coord(b.radius == null ? 30 : b.radius, 'radius'),
    editScanAt: (b) => 'edit scanat ' + coord(b.x, 'x') + ' ' + coord(b.y, 'y') + ' ' + coord(b.z, 'z')
        + ' ' + coord(b.radius == null ? 30 : b.radius, 'radius'),
    editInfo: (b) => 'edit info ' + intId(b.id, 'id'),
    editMove: (b) => 'edit move ' + intId(b.id, 'id') + ' ' + coord(b.x, 'x') + ' ' + coord(b.y, 'y') + ' ' + coord(b.z, 'z'),
    editMoveMany: (b) => {
        const ids = String(b.ids == null ? '' : b.ids);
        if (!/^\d+(,\d+){1,99}$/.test(ids)) throw new Error('ids must contain 2 to 100 comma-separated entity ids');
        return 'edit movegroup ' + ids + ' ' + coord(b.dx, 'dx') + ' ' + coord(b.dy, 'dy') + ' ' + coord(b.dz, 'dz');
    },
    editRotate: (b) => 'edit rotate ' + intId(b.id, 'id') + ' ' + coord(b.x, 'x') + ' ' + coord(b.y, 'y') + ' ' + coord(b.z, 'z'),
    editRotateMany: (b) => {
        const ids = String(b.ids == null ? '' : b.ids);
        if (!/^\d+(,\d+){1,99}$/.test(ids)) throw new Error('ids must contain 2 to 100 comma-separated entity ids');
        return 'edit rotategroup ' + ids + ' ' + coord(b.x, 'x') + ' ' + coord(b.y, 'y') + ' ' + coord(b.z, 'z');
    },
    editTransform: (b) => 'edit transform ' + intId(b.id, 'id') + ' ' + coord(b.x, 'x') + ' ' + coord(b.y, 'y') + ' ' + coord(b.z, 'z')
        + ' ' + coord(b.ex, 'rotation x') + ' ' + coord(b.ey, 'rotation y') + ' ' + coord(b.ez, 'rotation z'),
    editScale: (b) => 'edit scale ' + intId(b.id, 'id') + ' ' + coord(b.factor, 'factor'),
    editGround: (b) => 'edit ground ' + intId(b.id, 'id'),
    // the literal `confirm` only ever comes from the explicit boolean, never
    // from user text - the two-step stays two-step
    editDelete: (b) => 'edit delete ' + intId(b.id, 'id') + (b.confirm === true ? ' confirm' : ''),
    editDeleteGroup: (b) => {
        const ids = String(b.ids == null ? '' : b.ids);
        if (!/^\d+(,\d+){0,299}$/.test(ids)) throw new Error('ids must contain 1 to 300 comma-separated entity ids');
        return 'edit deletegroup ' + ids + (b.confirm === true ? ' confirm' : '');
    },
    editDuplicateGroup: (b) => {
        const ids = String(b.ids == null ? '' : b.ids);
        if (!/^\d+(,\d+){0,299}$/.test(ids)) throw new Error('ids must contain 1 to 300 comma-separated entity ids');
        return 'edit duplicategroup ' + ids + ' ' + coord(b.dx, 'dx') + ' ' + coord(b.dy, 'dy') + ' ' + coord(b.dz, 'dz');
    },
    editUndo: () => 'edit undo',
    editRedo: () => 'edit redo',
    editHistory: () => 'edit history',
    // one-shot save-string export by id via the blueprint module — avoids the
    // `select` + `select tostring` pair that NREs over REST (selection is
    // per-request, so it is gone by the second call)
    editToString: (b) => 'blueprint string ' + intId(b.id, 'id'),
    // respawn one exported string at a point (the in-game editor's paste). The
    // string must ride the pipe as ONE console token — strip any whitespace a
    // copy/paste dragged in before validating.
    editPaste: (b) => 'edit paste ' + coord(b.x, 'x') + ' ' + coord(b.y, 'y') + ' ' + coord(b.z, 'z')
        + ' ' + saveString(String(b.string == null ? '' : b.string).replace(/\s+/g, ''), 'string'),
    // in-place swap: replace entity <id> with an edited save string at its
    // exact spot (the in-game editor's Replace). Same one-token rule as paste.
    editReplace: (b) => 'edit replace ' + intId(b.id, 'id')
        + ' ' + saveString(String(b.string == null ? '' : b.string).replace(/\s+/g, ''), 'string'),
    // spawn a catalog prefab at an exact point (the in-game editor's
    // spawn-with-preview). The mod sends the HASH — one console token, no
    // name-charset worries; a name is accepted for hand-driven calls and rides
    // quoted the way spawn's does.
    editSpawnAt: (b) => 'edit spawnat ' + coord(b.x, 'x') + ' ' + coord(b.y, 'y') + ' ' + coord(b.z, 'z')
        + ' ' + (/^\d+$/.test(String(b.prefab == null ? '' : b.prefab).trim())
            ? String(b.prefab).trim() : nameOrId(b.prefab, 'prefab'))
};

// The named save-string library, persisted as one JSON file.
class StringLibrary {
    constructor(dir) {
        this._file = path.join(dir, 'strings.json');
        this._dir = dir;
        this._items = [];
        try {
            this._items = JSON.parse(fs.readFileSync(this._file, 'utf8')).strings || [];
        } catch { /* fresh library */ }
    }
    list() {
        return this._items.map((s) => ({ name: s.name, length: s.string.length, savedAt: s.savedAt }));
    }
    get(name) {
        return this._items.find((s) => s.name === name) || null;
    }
    put(name, string) {
        this._items = this._items.filter((s) => s.name !== name);
        this._items.push({ name, string, savedAt: new Date().toISOString() });
        this._save();
    }
    remove(name) {
        const before = this._items.length;
        this._items = this._items.filter((s) => s.name !== name);
        this._save();
        return this._items.length < before;
    }
    _save() {
        fs.mkdirSync(this._dir, { recursive: true });
        fs.writeFileSync(this._file, JSON.stringify({ strings: this._items }, null, 1));
    }
}

class PanelError extends Error { }

// ---- http plumbing -------------------------------------------------------------

function readBody(req) {
    return new Promise((resolve, reject) => {
        let data = '';
        req.on('data', (c) => {
            data += c;
            if (data.length > 65536) { req.destroy(); reject(new PanelError('body too large')); }
        });
        req.on('end', () => {
            if (data === '') { return resolve({}); }
            try { resolve(JSON.parse(data)); }
            catch { reject(new PanelError('body is not JSON')); }
        });
        req.on('error', reject);
    });
}

function json(res, status, obj) {
    const body = JSON.stringify(obj);
    res.writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) });
    res.end(body);
}

// opts: { panelPort, bindHost, consoleDir, consoleHost, consolePort, catalogFile, stateDir, signer, pipe }
// Returns the http.Server (unstarted); tests drive it on an ephemeral port.
function createServer(opts) {
    opts = opts || {};
    const catalogFile = opts.catalogFile || path.join(__dirname, '..', 'data', 'prefabs.json');
    const catalog = loadCatalog(catalogFile);
    const stateDir = opts.stateDir || process.env.PANEL_STATE_DIR || path.join(__dirname, 'state');
    const library = new StringLibrary(stateDir);
    const signer = opts.signer || new TokenSigner({ consoleDir: opts.consoleDir });
    const pipe = opts.pipe || new ConsolePipe({
        host: opts.consoleHost,
        port: opts.consolePort,
        consoleDir: opts.consoleDir || signer.consoleDir,
        signer
    });
    const indexHtml = fs.readFileSync(path.join(__dirname, 'public', 'index.html'));
    const editorHtml = fs.readFileSync(path.join(__dirname, 'public', 'editor.html'));

    async function run(res, command) {
        try {
            const r = await pipe.send(command);
            json(res, 200, { command, ok: r.ok, text: r.text, structured: r.structured, exception: r.exception });
        } catch (e) {
            json(res, 502, { command, ok: false, error: e.message });
        }
    }

    // Edit-module commands answer with one JSON line in the result text —
    // parse it here so the editor page gets structure, not a string.
    async function runEdit(res, command) {
        try {
            const r = await pipe.send(command);
            if (r.exception) { return json(res, 200, { command, ok: false, error: r.exception }); }
            let data = null;
            try { data = JSON.parse(String(r.text).trim()); }
            catch {
                return json(res, 200, {
                    command, ok: false,
                    error: 'edit module answered non-JSON (module missing from the DLL?): '
                        + String(r.text).slice(0, 160)
                });
            }
            return json(res, 200, { command, ok: data.ok !== false, data });
        } catch (e) {
            json(res, 502, { command, ok: false, error: e.message });
        }
    }

    // One-shot save-string export by entity id (blueprint module). The command
    // answers "<name> (<id>):\n<savestring>" — raw string on the LAST line — or
    // a single error sentence. Returns {ok,string,label} | {ok:false,error}.
    async function exportString(id) {
        const r = await pipe.send(builders.editToString({ id }));
        if (r.exception) { return { ok: false, error: r.exception }; }
        const lines = String(r.text).split(/\r?\n/).map((s) => s.trim()).filter(Boolean);
        const last = lines.length ? lines[lines.length - 1] : '';
        try {
            const str = saveString(last, 'tostring result');
            return { ok: true, string: str, label: lines.length > 1 ? lines[0].replace(/:\s*$/, '') : ('#' + id) };
        } catch (e) {
            return { ok: false, error: String(r.text).trim().slice(0, 200) || 'no string returned' };
        }
    }

    const server = http.createServer(async (req, res) => {
        const url = new URL(req.url, 'http://localhost');
        const route = req.method + ' ' + url.pathname;

        // DNS-rebinding guard: a hostile page can point its own hostname at
        // 127.0.0.1 and script requests here — the Host header gives it away.
        const hostname = String(req.headers.host || '').replace(/:\d+$/, '').toLowerCase();
        if (hostname !== '127.0.0.1' && hostname !== 'localhost' && hostname !== '[::1]'
            && !hostname.endsWith('.ts.net')) {
            return json(res, 403, { error: 'panel accepts loopback requests only' });
        }
        // Cross-site form posts can reach localhost without CORS ever applying;
        // requiring JSON keeps browser-borne CSRF out of the console pipe.
        if (req.method === 'POST') {
            const ct = String(req.headers['content-type'] || '').split(';')[0].trim().toLowerCase();
            if (ct !== 'application/json') {
                return json(res, 415, { error: 'POST requires Content-Type: application/json' });
            }
        }

        try {
            switch (route) {
                case 'GET /':
                    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
                    return res.end(indexHtml);

                case 'GET /editor':
                    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
                    return res.end(editorHtml);

                case 'GET /api/health': {
                    let console_ = { reachable: false, error: null };
                    try {
                        const r = await pipe.send('help');
                        console_ = { reachable: r.ok || r.exception != null, error: r.exception };
                    } catch (e) { console_.error = e.message; }
                    return json(res, 200, {
                        ok: true, kid: signer.kid,
                        consoleHost: pipe.host, consolePort: pipe.port,
                        prefabs: catalog.count, spawnable: catalog.spawnableCount,
                        console: console_
                    });
                }

                case 'GET /api/prefabs': {
                    const limit = Math.min(Math.max(parseInt(url.searchParams.get('limit') || '50', 10) || 50, 1), 500);
                    const results = searchPrefabs(catalog, url.searchParams.get('q') || '',
                        url.searchParams.get('spawnable') === '1', limit);
                    return json(res, 200, { count: results.length, results });
                }

                case 'POST /api/command':
                    return run(res, cleanCommand((await readBody(req)).command));
                case 'POST /api/spawn':
                    return run(res, builders.spawn(await readBody(req)));
                case 'POST /api/select':
                    return run(res, builders.select(await readBody(req)));
                case 'POST /api/select/get':
                    return run(res, builders.selectGet());
                case 'POST /api/select/move':
                    return run(res, builders.selectMove(await readBody(req)));
                case 'POST /api/select/rotate':
                    return run(res, builders.selectRotate(await readBody(req)));
                case 'POST /api/select/destroy':
                    return run(res, builders.selectDestroy());
                case 'POST /api/destroy':
                    return run(res, builders.destroy(await readBody(req)));

                case 'GET /api/edit/players':
                    return runEdit(res, builders.editPlayers());
                case 'POST /api/edit/scan': {
                    const b = await readBody(req);
                    return runEdit(res, b.player != null && b.player !== ''
                        ? builders.editScan(b) : builders.editScanAt(b));
                }
                case 'POST /api/edit/info':
                    return runEdit(res, builders.editInfo(await readBody(req)));
                case 'POST /api/edit/move':
                    return runEdit(res, builders.editMove(await readBody(req)));
                case 'POST /api/edit/move-many':
                    return runEdit(res, builders.editMoveMany(await readBody(req)));
                case 'POST /api/edit/rotate':
                    return runEdit(res, builders.editRotate(await readBody(req)));
                case 'POST /api/edit/rotate-many':
                    return runEdit(res, builders.editRotateMany(await readBody(req)));
                case 'POST /api/edit/transform':
                    return runEdit(res, builders.editTransform(await readBody(req)));
                case 'POST /api/edit/scale':
                    return runEdit(res, builders.editScale(await readBody(req)));
                case 'POST /api/edit/ground':
                    return runEdit(res, builders.editGround(await readBody(req)));
                case 'POST /api/edit/delete':
                    return runEdit(res, builders.editDelete(await readBody(req)));
                case 'POST /api/edit/delete-many':
                    return runEdit(res, builders.editDeleteGroup(await readBody(req)));
                case 'POST /api/edit/duplicate-many':
                    return runEdit(res, builders.editDuplicateGroup(await readBody(req)));
                case 'POST /api/edit/undo':
                    return runEdit(res, builders.editUndo());
                case 'POST /api/edit/redo':
                    return runEdit(res, builders.editRedo());
                case 'GET /api/edit/history':
                    return runEdit(res, builders.editHistory());
                case 'POST /api/edit/tostring': {
                    const id = intId((await readBody(req)).id, 'id');
                    const out = await exportString(id);
                    return json(res, 200, out.ok
                        ? { ok: true, id, string: out.string, label: out.label, length: out.string.length }
                        : { ok: false, error: out.error });
                }
                case 'POST /api/edit/paste':
                    return runEdit(res, builders.editPaste(await readBody(req)));
                case 'POST /api/edit/replace':
                    return runEdit(res, builders.editReplace(await readBody(req)));
                case 'POST /api/edit/spawnat':
                    return runEdit(res, builders.editSpawnAt(await readBody(req)));

                case 'GET /api/strings':
                    return json(res, 200, { strings: library.list() });

                case 'POST /api/strings/capture': {
                    const b = await readBody(req);
                    const name = libName(b.name);
                    const out = await exportString(intId(b.id, 'id'));
                    if (!out.ok) { return json(res, 200, { ok: false, error: out.error }); }
                    library.put(name, out.string);
                    return json(res, 200, { ok: true, name, length: out.string.length });
                }

                case 'POST /api/strings/spawn': {
                    const b = await readBody(req);
                    let s = b.string;
                    if (!s && b.name) {
                        const item = library.get(libName(b.name));
                        if (!item) { return json(res, 404, { error: 'no saved string: ' + b.name }); }
                        s = item.string;
                    }
                    return run(res, builders.spawnString(b.player, s));
                }

                case 'POST /api/strings/delete': {
                    const b = await readBody(req);
                    const gone = library.remove(libName(b.name));
                    return json(res, gone ? 200 : 404, gone ? { ok: true } : { error: 'no saved string: ' + b.name });
                }

                default:
                    return json(res, 404, { error: 'no such route: ' + route });
            }
        } catch (e) {
            if (e instanceof PanelError) { return json(res, 400, { error: e.message }); }
            console.error('[panel] ' + route + ' failed:', e);
            return json(res, 500, { error: 'internal error' });
        }
    });

    return server;
}

// Loopback is a hard rule, not a default — new capability never means a new
// exposed port. Refuse anything that isn't 127.0.0.1.
function start(server, port, bindHost) {
    const host = bindHost || '127.0.0.1';
    if (host !== '127.0.0.1' && host !== 'localhost') {
        throw new Error('panel binds loopback only (asked for ' + host + ')');
    }
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, '127.0.0.1', () => resolve(server.address().port));
    });
}

module.exports = { createServer, start, searchPrefabs, loadCatalog, builders, cleanCommand };

if (require.main === module) {
    const argPort = process.argv.indexOf('--port');
    const port = argPort !== -1 ? parseInt(process.argv[argPort + 1], 10)
        : parseInt(process.env.PANEL_PORT || '', 10) || DEFAULT_PANEL_PORT;
    const server = createServer({ consoleDir: process.env.ATT_CONSOLE_DIR });
    start(server, port).then((p) => {
        console.log('[panel] up on http://127.0.0.1:' + p
            + ' (remote? tunnel in with: ssh -L ' + p + ':127.0.0.1:' + p + ' user@your-server)');
    }).catch((e) => {
        console.error('[panel] failed to start: ' + e.message);
        process.exit(1);
    });
}
