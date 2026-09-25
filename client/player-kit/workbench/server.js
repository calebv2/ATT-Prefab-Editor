#!/usr/bin/env node
// String Workbench v2 — the LOCAL save-string workstation.
//
//   cd workbench && npm install && node server.js     ->  http://localhost:1767
//
// Decode a pasted string, edit it with forms, build new objects from the
// prefab catalog, keep a named library (with notes), and export shop-catalog
// JSON. Binds 127.0.0.1 only.
//
// The ONE outbound path (v2): the "spawn in game" button proxies to the web
// panel's /api/strings/spawn — which you reach through your own SSH tunnel
// (ssh -L 1766:127.0.0.1:1766 user@your-server). The proxy refuses any non-loopback
// panel URL, so the workbench never talks past your own machine.
//
// Encoding/decoding is att-string-transcoder (MIT, npm) — the same library the
// community used against the cloud. We keep the DECODED Prefab instance in a
// server-side session and apply edits as operations against it, so component
// data the UI doesn't understand (Unknown components, exotic versions)
// round-trips untouched instead of being rebuilt lossily.

'use strict';

const fs = require('fs');
const http = require('http');
const path = require('path');

const lib = require('att-string-transcoder');
const { Prefab, ATTPrefabs, PhysicalMaterialPartHash, PresetHash } = lib;
const pouch = require('./tools/pouch-core');   // same encoder the CLI uses

const DEFAULT_PORT = 1767;
const DEFAULT_PANEL_URL = 'http://127.0.0.1:1766';

// Share mode: the shippable build for people who don't have the mod + SSH
// tunnel. It hides every feature that reaches OUR infrastructure (spawn /
// capture / replace all POST to the web panel over the tunnel) and leaves the
// pure string workstation — browse, build, decode, edit, library, export,
// copy. One codebase, one flag; the zip's start script sets WB_SHARE=1.
// Read live (not a load-time const) so a test can flip it per server instance.
function isShare() { return process.env.WB_SHARE === '1'; }

// Data lives at ../data in the repo and ./data in the shipped bundle. Try the
// repo layout first, fall back to alongside the server — so the same server.js
// runs in both without an env var.
const DATA_DIR = process.env.WB_DATA_DIR
    || (fs.existsSync(path.join(__dirname, '..', 'data', 'prefabs.json'))
        ? path.join(__dirname, '..', 'data')
        : path.join(__dirname, 'data'));

// name -> component class, from the library's own exports (FooComponent -> Foo)
const COMPONENT_CLASSES = {};
for (const key of Object.keys(lib)) {
    if (key.endsWith('Component') && key !== 'UnsupportedComponent' && typeof lib[key] === 'function') {
        COMPONENT_CLASSES[key.slice(0, -'Component'.length)] = lib[key];
    }
}

// hash -> latest known version (internal constant; fall back to 1 if the
// package layout ever changes)
let latestVersions = new Map();
try {
    latestVersions = require('att-string-transcoder/dist/cjs/constants.js').latestSupportedComponentVersions;
} catch {
    try {
        latestVersions = require(path.join(path.dirname(require.resolve('att-string-transcoder')), 'constants.js'))
            .latestSupportedComponentVersions;
    } catch { /* fall back to version 1 */ }
}

function latestVersionFor(componentName) {
    const Cls = COMPONENT_CLASSES[componentName];
    if (!Cls) { throw new WbError('unknown component: ' + componentName); }
    try {
        const probe = new Cls({ version: 1 });
        return latestVersions.get(probe.hash) || 1;
    } catch {
        return 1;
    }
}

// ---- catalog (vendored, same file the panel uses) ---------------------------

const CATALOG_FILE = process.env.WB_CATALOG || path.join(DATA_DIR, 'prefabs.json');
const catalog = JSON.parse(fs.readFileSync(CATALOG_FILE, 'utf8')).prefabs.map((p) => ({
    name: p.name,
    hash: p.hash,
    spawnable: !!p.spawnable,
    key: p.name.toLowerCase().replace(/[_\s()'-]/g, '')
}));

// ---- item images (Hazy's Item List, harvested by data/fetch-item-images.js) --
// hash -> filename. The folder is gitignored (60 MB of community screenshots);
// a missing index just means no thumbnails, never an error.

// Share the staged preview images with the in-game mod so release kits only
// need to carry one copy. Set WB_IMAGES_DIR to use a separate image harvest.
const IMAGES_DIR = process.env.WB_IMAGES_DIR || path.join(__dirname, '..', 'dist', 'PrefabEditorImages');
const imageFiles = new Map();
const hazyNames = new Map();   // hash -> Hazy's display name (extra search alias)
try {
    const idx = JSON.parse(fs.readFileSync(path.join(IMAGES_DIR, 'index.json'), 'utf8'));
    for (const [hash, e] of Object.entries(idx.items || {})) {
        if (!e) { continue; }
        if (e.file) { imageFiles.set(parseInt(hash, 10), e.file); }
        if (e.name) { hazyNames.set(parseInt(hash, 10), e.name); }
    }
} catch { /* no images harvested yet */ }

const IMAGE_TYPES = { png: 'image/png', jpg: 'image/jpeg', webp: 'image/webp', gif: 'image/gif' };

function hasImage(hash) { return imageFiles.has(hash); }

// ---- slot references (Ethyn's Visual Slot Reference, data/fetch-slot-refs.ps1)
// prefab name -> { file, slots: [{color, slot, hash, comment}] }. The annotated
// image shows WHERE each colored slot sits on the item; also gitignored.

const SLOTREF_DIR = process.env.WB_SLOTREF_DIR || path.join(DATA_DIR, 'slot-refs');
const slotRefs = new Map();
try {
    // strip a UTF-8 BOM — the harvest script is PowerShell and 5.1 loves BOMs
    const raw = fs.readFileSync(path.join(SLOTREF_DIR, 'index.json'), 'utf8').replace(/^﻿/, '');
    const idx = JSON.parse(raw);
    for (const [name, e] of Object.entries(idx.prefabs || {})) { slotRefs.set(name, e); }
} catch { /* no slot refs harvested yet */ }

// ---- item codes (bot-dist/data/itemCodes.json) -------------------------------
// The bot's curated labels + categories, keyed by code — and bot item codes ARE
// prefab hashes. First entry wins, so curated categories beat the
// auto-generated "More Items" tail. Missing file just means plainer cards.

// The bot's copy is canonical in the repo; the bundle carries a copy in ./data.
const ITEMCODES_FILE = process.env.WB_ITEMCODES
    || (fs.existsSync(path.join(__dirname, '..', 'bot-dist', 'data', 'itemCodes.json'))
        ? path.join(__dirname, '..', 'bot-dist', 'data', 'itemCodes.json')
        : path.join(DATA_DIR, 'itemCodes.json'));
const itemMeta = new Map(); // hash -> { label, category }
try {
    const ic = JSON.parse(fs.readFileSync(ITEMCODES_FILE, 'utf8'));
    for (const [category, items] of Object.entries(ic)) {
        if (typeof items !== 'object' || items === null) { continue; }
        for (const [label, code] of Object.entries(items)) {
            const hash = parseInt(code, 10);
            if (!isNaN(hash) && !itemMeta.has(hash)) { itemMeta.set(hash, { label, category }); }
        }
    }
} catch { /* no bot catalog nearby (e.g. the share bundle) */ }

function searchCatalog(q, limit) {
    const key = String(q || '').toLowerCase().replace(/[_\s()'-]/g, '');
    const asHash = /^\d+$/.test(String(q || '').trim()) ? parseInt(q, 10) : null;
    const out = [];
    for (const p of catalog) {
        if (key !== '' && !p.key.includes(key) && p.hash !== asHash) { continue; }
        out.push({ name: p.name, hash: p.hash, spawnable: p.spawnable, img: hasImage(p.hash) });
        if (out.length >= limit) { break; }
    }
    return out;
}

// ---- liquid catalogs (data/liquids.json, vendored from att-liquids) ---------
// Named effects/presets/appearances/chunks so the designer and the library
// summaries never show a bare hash. Missing file degrades to raw hashes.

const liquidEnums = { effects: [], presets: [], appearances: [], chunks: [] };
const effectNames = new Map();   // hash -> name
const presetNames = new Map();
try {
    const lq = JSON.parse(fs.readFileSync(path.join(DATA_DIR, 'liquids.json'), 'utf8'));
    const toList = (o) => Object.keys(o || {}).sort().map((name) => ({ name, hash: o[name] }));
    // Merge the transcoder's preset enum (2 entries) with att-liquids' 9,
    // preferring att-liquids names, deduped by hash.
    const byHash = new Map();
    for (const name of Object.keys(PresetHash).filter((k) => isNaN(k))) byHash.set(PresetHash[name], name);
    for (const [name, hash] of Object.entries(lq.presets || {})) byHash.set(hash, name);
    liquidEnums.effects = toList(lq.effects);
    liquidEnums.presets = [...byHash.entries()].map(([hash, name]) => ({ name, hash }))
        .sort((a, b) => a.name.localeCompare(b.name));
    liquidEnums.appearances = toList(lq.visualAppearances);
    liquidEnums.chunks = toList(lq.visualChunks);
    for (const e of liquidEnums.effects) effectNames.set(e.hash, e.name);
    for (const p of liquidEnums.presets) presetNames.set(p.hash, p.name);
} catch { /* liquids.json optional */ }

// ---- browser catalog ---------------------------------------------------------
// Everything the visual browser shows about every prefab, joined once at boot
// and served in one shot — the client filters locally so search costs nothing.

let browserList = null;
const pouchNames = new Set(Object.keys(pouch.knownTypes()));   // items we can pouch

function browserCatalog() {
    if (!browserList) {
        browserList = catalog.map((p) => {
            const meta = itemMeta.get(p.hash);
            return {
                name: p.name,
                hash: p.hash,
                spawnable: p.spawnable,
                img: hasImage(p.hash),
                label: meta ? meta.label : null,
                category: meta ? meta.category : null,
                alias: hazyNames.get(p.hash) || null,
                slotRef: slotRefs.has(p.name),
                pouchable: pouchNames.has(p.name)
            };
        });
    }
    return browserList;
}

// ---- sessions ----------------------------------------------------------------

const sessions = new Map(); // docId -> { prefab, touched, history, future }
let nextDoc = 1;

function newSession(prefab) {
    const docId = 'd' + nextDoc++;
    sessions.set(docId, { prefab, touched: Date.now(), history: [], future: [] });
    // drop the oldest once we're hoarding
    if (sessions.size > 50) {
        let oldest = null;
        for (const [id, s] of sessions) { if (!oldest || s.touched < sessions.get(oldest).touched) { oldest = id; } }
        sessions.delete(oldest);
    }
    return docId;
}

function getSession(docId) {
    const s = sessions.get(docId);
    if (!s) { throw new WbError('unknown or expired docId — decode/new again'); }
    s.touched = Date.now();
    return s;
}

// resolve a node inside the document by child-index path, e.g. [] = root, [0,1] = second child of first child
function nodeAt(prefab, nodePath) {
    let node = prefab;
    for (const i of nodePath || []) {
        if (!node.children || !node.children[i]) { throw new WbError('bad node path'); }
        node = node.children[i].prefab;
    }
    return node;
}

// undo/redo: snapshot the encoded doc around every mutation (cap 60)
function snapshot(session) {
    try { return session.prefab.toSaveString(); } catch { return null; }
}

function pushHistory(session, before) {
    if (before == null) { return; }
    session.history.push(before);
    if (session.history.length > 60) { session.history.shift(); }
    session.future.length = 0;
}

// ---- view: what the UI renders -----------------------------------------------

function slotsFor(prefabName) {
    const entry = ATTPrefabs[prefabName];
    if (!entry || !entry.embedded) { return []; }
    return Object.entries(entry.embedded).map(([key, e]) => ({ key, hash: e.hash }));
}

function cloneProps(component) {
    const props = {};
    for (const [k, v] of Object.entries(component)) {
        if (k === 'hash' || k === 'name' || k === 'version') { continue; }
        if (typeof v === 'function') { continue; }
        try { props[k] = v === undefined ? null : JSON.parse(JSON.stringify(v)); }
        catch { props[k] = null; }
    }
    return props;
}

// ---- diff: what changed between two decoded strings --------------------------
// A flat, readable list: root prefab, transform, component add/remove, per-prop
// value changes, docked-child count. Compares the two roots (not recursively —
// deep-tree diffing is noise; the child count flags structural change).

function round(v) { return typeof v === 'number' ? Math.round(v * 1e4) / 1e4 : v; }

function diffPrefabs(A, B) {
    const diffs = [];
    const add = (kind, text) => diffs.push({ kind, text });
    if (A.name !== B.name) { add('root', 'prefab: ' + A.name + ' → ' + B.name); }

    const pa = A.getPosition(), pb = B.getPosition();
    for (const ax of ['x', 'y', 'z']) {
        if (round(pa[ax]) !== round(pb[ax])) { add('transform', 'position.' + ax + ': ' + round(pa[ax]) + ' → ' + round(pb[ax])); }
    }
    if (round(A.getScale()) !== round(B.getScale())) { add('transform', 'scale: ' + round(A.getScale()) + ' → ' + round(B.getScale())); }

    const names = new Set([...Object.keys(A.components), ...Object.keys(B.components)].filter((n) => n !== 'Unknown'));
    for (const n of [...names].sort()) {
        const ca = A.components[n], cb = B.components[n];
        if (ca && !cb) { add('component', n + ': removed'); continue; }
        if (!ca && cb) { add('component', n + ': added'); continue; }
        if (!ca || !cb) { continue; }
        const propsA = cloneProps(ca), propsB = cloneProps(cb);
        for (const k of [...new Set([...Object.keys(propsA), ...Object.keys(propsB)])].sort()) {
            const va = JSON.stringify(propsA[k]), vb = JSON.stringify(propsB[k]);
            if (va !== vb) {
                const short = (s) => s == null ? '∅' : (s.length > 60 ? s.slice(0, 57) + '…' : s);
                add('prop', n + '.' + k + ': ' + short(va) + ' → ' + short(vb));
            }
        }
    }
    if (A.children.length !== B.children.length) {
        add('children', 'docked children: ' + A.children.length + ' → ' + B.children.length);
    }
    return diffs;
}

function view(prefab) {
    const comps = [];
    let unknownCount = 0;
    for (const [name, c] of Object.entries(prefab.components)) {
        if (name === 'Unknown') { unknownCount = (c || []).length; continue; }
        if (!c) { continue; }
        comps.push({ name, version: c.version, props: cloneProps(c) });
    }
    const slots = slotsFor(prefab.name);
    const byHash = new Map(slots.map((s) => [s.hash, s.key]));
    return {
        name: prefab.name,
        hash: prefab.hash,
        img: hasImage(prefab.hash),
        slotRef: slotRefs.has(prefab.name),
        position: prefab.getPosition(),
        rotation: prefab.getRotation(),
        scale: prefab.getScale(),
        velocity: prefab.getVelocity(),
        angularVelocity: prefab.getAngularVelocity(),
        kinematic: prefab.getKinematic(),
        serverSleeping: prefab.getServerSleeping(),
        onFire: prefab.getOnFire(),
        integrity: prefab.getIntegrity(),
        material: PhysicalMaterialPartHash[prefab.getMaterial()] || null,
        servings: safeServings(prefab),
        components: comps,
        unknownComponents: unknownCount,
        slots,
        children: prefab.children.map((ch) => ({
            parentHash: ch.parentHash,
            slotName: byHash.get(ch.parentHash) || null,
            view: view(ch.prefab)
        }))
    };
}

function safeServings(prefab) {
    try { return prefab.getServings(); } catch { return null; }
}

// ---- summaries (library cards + doc header) ----------------------------------
// A compact digest of one decoded prefab: what is it, what's docked in it,
// what does the liquid do — plus the legacy-alpha flag (custom liquid alpha
// authored 255-style; renders broken in-game).

function summarize(prefab) {
    const s = {
        name: prefab.name,
        hash: prefab.hash,
        img: hasImage(prefab.hash),
        children: prefab.children.length,
        childNames: prefab.children.slice(0, 4).map((c) => c.prefab.name),
        components: Object.keys(prefab.components)
            .filter((k) => k !== 'Unknown' && prefab.components[k]).length,
        unknown: (prefab.components.Unknown || []).length,
        liquid: null,
        legacyAlpha: false
    };
    const lc = prefab.components.LiquidContainer;
    if (lc) {
        if (lc.isCustom && lc.customData) {
            const cd = lc.customData;
            const col = cd.color || {};
            const a = col.a == null ? 1 : col.a;
            s.liquid = {
                custom: true,
                color: { r: col.r | 0, g: col.g | 0, b: col.b | 0, a },
                effects: (cd.effects || []).filter(Boolean).map((e) => ({
                    hash: e.hash,
                    name: effectNames.get(e.hash) || null,
                    mult: e.strengthMultiplier
                })),
                chunks: (cd.foodChunks || []).length,
                skin: !!cd.isConsumableThroughSkin,
                level: lc.contentLevel
            };
            if (a > 1) { s.legacyAlpha = true; }
        } else {
            s.liquid = {
                custom: false,
                presetHash: lc.presetHash,
                presetName: presetNames.get(lc.presetHash) || null,
                level: lc.contentLevel
            };
        }
    }
    return s;
}

// Library summaries are decoded on demand and memoized by content (djb2 of
// the string) so listing stays instant however often the UI refreshes.
const summaryCache = new Map(); // contentKey -> { summary } | { error }

function contentKey(s) {
    let h = 5381;
    for (let i = 0; i < s.length; i++) { h = ((h << 5) + h + s.charCodeAt(i)) | 0; }
    return s.length + ':' + h;
}

function summaryOf(string) {
    const key = contentKey(string);
    let hit = summaryCache.get(key);
    if (!hit) {
        try { hit = { summary: summarize(Prefab.fromSaveString(string)) }; }
        catch (e) { hit = { error: 'undecodable: ' + e.message }; }
        if (summaryCache.size > 500) { summaryCache.clear(); }
        summaryCache.set(key, hit);
    }
    return hit;
}

// ---- result: string + round-trip verdict -------------------------------------
// Every op answers with the encoded string AND whether that string re-decodes
// and re-encodes byte-identical — the same proof the offline suite runs, now
// surfaced live in the UI.

function result(session) {
    let string = null, encodeError = null, roundTrip = null;
    try { string = session.prefab.toSaveString(); }
    catch (e) { encodeError = e.message; }
    if (string) {
        try { roundTrip = Prefab.fromSaveString(string).toSaveString() === string ? 'stable' : 'unstable'; }
        catch { roundTrip = 'redecode-failed'; }
    }
    return {
        view: view(session.prefab),
        summary: summarize(session.prefab),
        string,
        encodeError,
        roundTrip
    };
}

// ---- operations ----------------------------------------------------------------

class WbError extends Error { }

function num(v, what) {
    const n = typeof v === 'number' ? v : parseFloat(v);
    if (!isFinite(n)) { throw new WbError(what + ' must be a finite number'); }
    return n;
}

function vec3(a) { return { x: num(a.x, 'x'), y: num(a.y, 'y'), z: num(a.z, 'z') }; }

function applyOp(node, op, args) {
    args = args || {};
    switch (op) {
        case 'setPosition': return node.setPosition(vec3(args));
        case 'setRotation': return node.setRotation({ ...vec3(args), w: num(args.w, 'w') });
        case 'setVelocity': return node.setVelocity(vec3(args));
        case 'setAngularVelocity': return node.setAngularVelocity(vec3(args));
        case 'setScale': return node.setScale(num(args.value, 'scale'));
        case 'setKinematic': return node.setKinematic(!!args.value);
        case 'setServerSleeping': return node.setServerSleeping(!!args.value);
        case 'setOnFire': return node.setOnFire(!!args.value);
        case 'setIntegrity': {
            const v = num(args.value, 'integrity');
            if (v < 0 || v > 1) { throw new WbError('integrity is 0..1'); }
            node.setIntegrity(v);
            // The lib auto-creates DurabilityModule on prefabs that support it
            // and SILENTLY no-ops on ones that don't - make that loud.
            if (!node.components.DurabilityModule) {
                throw new WbError(node.name + " doesn't take durability (not in its savables; "
                    + 'and strings work fine without it - potions need only LiquidContainer). '
                    + 'Force it via "+ component" -> DurabilityModule if you really want.');
            }
            return node;
        }
        case 'setServings': return node.setServings(num(args.value, 'servings'));
        case 'setMaterial': {
            if (!(args.name in PhysicalMaterialPartHash)) { throw new WbError('unknown material: ' + args.name); }
            node.setMaterial(args.name);
            // Same deal: auto-creates PhysicalMaterialPart where supported.
            const pm = node.components.PhysicalMaterialPart;
            if (!pm || pm.materialHash !== PhysicalMaterialPartHash[args.name]) {
                throw new WbError(node.name + " doesn't take a material (not in its savables). "
                    + 'Force it via "+ component" -> PhysicalMaterialPart if you really want.');
            }
            return node;
        }
        case 'setGiftBoxLabel': return node.setGiftBoxLabel(String(args.value || ''));

        case 'setComponentProp': {
            const c = node.components[args.component];
            if (!c) { throw new WbError('component not on this prefab: ' + args.component); }
            if (!(args.prop in c) || typeof c[args.prop] === 'function') {
                throw new WbError('no such property: ' + args.component + '.' + args.prop);
            }
            const current = c[args.prop];
            let v = args.value;
            if (typeof current === 'number') { v = num(v, args.prop); }
            else if (typeof current === 'boolean') { v = !!v; }
            // arrays/objects/null arrive as parsed JSON and are assigned as-is
            c[args.prop] = v;
            return node;
        }

        case 'setLiquid': {
            let c = node.components.LiquidContainer;
            if (!c) {
                c = new lib.LiquidContainerComponent({ version: latestVersionFor('LiquidContainer') });
                node.addComponent(c);
            }
            for (const k of ['canAddTo', 'canRemoveFrom', 'hasContent', 'isCustom']) {
                if (args[k] !== undefined) { c[k] = !!args[k]; }
            }
            if (args.contentLevel !== undefined) { c.contentLevel = num(args.contentLevel, 'contentLevel'); }
            if (args.presetHash !== undefined) { c.presetHash = num(args.presetHash, 'presetHash'); }
            if (args.customData !== undefined) {
                // Colour in the save format is a Unity 0..1 float per channel.
                // Live-confirmed: 0-255 RGB does SPAWN fine, which is why the
                // old note here called it the community convention — but every
                // channel over 1 clamps to 1, so 9,170,9 renders WHITE, not
                // green. A working community poison potion decodes to 0,1,0.
                // Rescale the triplet together (a lone r=1 alongside g=170 is
                // 0-255 input too, so per-channel tests would corrupt it).
                if (args.customData && args.customData.color) {
                    const col = args.customData.color;
                    const rgb = ['r', 'g', 'b'].map((k) => num(col[k] == null ? 0 : col[k], k));
                    const scale = rgb.some((v) => v > 1) ? 255 : 1;
                    ['r', 'g', 'b'].forEach((k, i) => {
                        col[k] = Math.max(0, Math.min(1, rgb[i] / scale));
                    });
                    let a = num(col.a == null ? 1 : col.a, 'alpha');
                    if (a > 1) { a = a / 255; }
                    col.a = Math.max(0, Math.min(1, a));
                }
                c.customData = args.customData; // null clears it
            }
            return node;
        }

        case 'addComponent': {
            if (node.components[args.name]) { throw new WbError(args.name + ' already on this prefab'); }
            const Cls = COMPONENT_CLASSES[args.name];
            if (!Cls) { throw new WbError('unknown component: ' + args.name); }
            return node.addComponent(new Cls({ version: latestVersionFor(args.name) }));
        }
        case 'removeComponent': {
            if (!node.components[args.name]) { throw new WbError(args.name + ' is not on this prefab'); }
            return node.removeComponent(args.name);
        }

        // paste a whole component copied off another prefab: {name, version, props}
        // (replaces any existing one of the same type). The clipboard payload is
        // exactly what the view emits, so copy-here / paste-there round-trips.
        case 'putComponent': {
            const Cls = COMPONENT_CLASSES[args.name];
            if (!Cls) { throw new WbError('unknown component: ' + args.name); }
            if (node.components[args.name]) { node.removeComponent(args.name); }
            const c = new Cls({ version: args.version || latestVersionFor(args.name) });
            node.addComponent(c);
            for (const [k, v] of Object.entries(args.props || {})) {
                if (k === 'hash' || k === 'name' || k === 'version') { continue; }
                if (k in c && typeof c[k] !== 'function') { c[k] = v; }
            }
            return node;
        }

        case 'addChild': {
            const childName = String(args.prefab || '');
            if (!ATTPrefabs[childName]) { throw new WbError('unknown prefab: ' + childName); }
            const slot = args.slot ? String(args.slot) : null;
            return node.addChildPrefab(slot, new Prefab(childName));
        }
        case 'removeChild': {
            const i = num(args.index, 'index');
            if (!node.children[i]) { throw new WbError('no child at index ' + i); }
            node.children.splice(i, 1);
            return node;
        }

        default:
            throw new WbError('unknown op: ' + op);
    }
}

// ---- structural ops (two paths -> whole-doc; the blocks view's verbs) ---------

const DOC_OPS = new Set(['moveChild', 'duplicateChild', 'addChildString']);

function slotKeyFor(parentName, hash) {
    const s = slotsFor(parentName).find((x) => x.hash === hash);
    return s ? s.key : null;
}

function isPrefixPath(prefix, p) {
    if (prefix.length > p.length) { return false; }
    for (let i = 0; i < prefix.length; i++) { if (prefix[i] !== p[i]) { return false; } }
    return true;
}

function applyDocOp(root, op, args) {
    args = args || {};
    switch (op) {
        case 'moveChild': {
            const from = args.fromParent || [];
            const index = num(args.index, 'index');
            const to = args.toParent || [];
            if (isPrefixPath(from.concat(index), to)) { throw new WbError("can't dock a piece inside itself"); }
            const fromNode = nodeAt(root, from);
            const toNode = nodeAt(root, to);
            if (!fromNode.children[index]) { throw new WbError('no child at index ' + index); }
            const entry = fromNode.children.splice(index, 1)[0];
            try {
                if (args.slot) { toNode.addChildPrefab(String(args.slot), entry.prefab); }
                else { toNode.addChildPrefab(null, entry.prefab); }
            } catch (e) {
                fromNode.children.splice(index, 0, entry);   // put it back
                throw new WbError('move failed: ' + e.message);
            }
            return;
        }
        case 'duplicateChild': {
            const parentNode = nodeAt(root, args.parent || []);
            const index = num(args.index, 'index');
            const entry = parentNode.children[index];
            if (!entry) { throw new WbError('no child at index ' + index); }
            let clone;
            try { clone = Prefab.fromSaveString(entry.prefab.toSaveString()); }
            catch (e) { throw new WbError('piece does not round-trip, cannot duplicate: ' + e.message); }
            const key = slotKeyFor(parentNode.name, entry.parentHash);
            if (key) { parentNode.addChildPrefab(key, clone); }
            else { parentNode.children.push({ parentHash: entry.parentHash, prefab: clone }); }
            return;
        }
        case 'addChildString': {
            const parentNode = nodeAt(root, args.parent || []);
            let child;
            try { child = Prefab.fromSaveString(String(args.string || '').trim()); }
            catch (e) { throw new WbError('not a decodable string: ' + e.message); }
            parentNode.addChildPrefab(args.slot ? String(args.slot) : null, child);
            return;
        }
        default:
            throw new WbError('unknown doc op: ' + op);
    }
}

// ---- preflight: everything we can prove about a string WITHOUT the game ------
// Catches the failure classes we have actually seen live. What it cannot do is
// promise in-game behavior — that stays what the spawn button is for.

const catalogByHash = new Map(catalog.map((p) => [p.hash, p]));

function walkNodes(prefab, fn) {
    fn(prefab);
    for (const ch of prefab.children) { walkNodes(ch.prefab, fn); }
}

function preflight(prefab) {
    const checks = [];
    const add = (level, text) => checks.push({ level, text });

    let string = null;
    try { string = prefab.toSaveString(); add('pass', 'encodes cleanly'); }
    catch (e) { add('fail', 'does not encode: ' + e.message); }
    if (string) {
        try {
            add(Prefab.fromSaveString(string).toSaveString() === string ? 'pass' : 'warn',
                Prefab.fromSaveString(string).toSaveString() === string
                    ? 're-decodes byte-identical' : 're-encode differs (will normalize on first save)');
        } catch (e) { add('fail', 'encoded string does not re-decode: ' + e.message); }
    }

    const cat = catalogByHash.get(prefab.hash);
    if (!cat) { add('warn', 'root ' + prefab.name + ' is not in the vendored catalog'); }
    else if (!cat.spawnable) { add('warn', 'root ' + prefab.name + ' is not on the spawn list — spawn string may refuse it'); }
    else { add('pass', 'root is spawnable (' + prefab.name + ')'); }

    let pieces = 0, comps = 0, unknownComps = 0;
    walkNodes(prefab, (node) => {
        pieces++;
        unknownComps += (node.components.Unknown || []).length;
        for (const [cname, c] of Object.entries(node.components)) {
            if (cname === 'Unknown' || !c) { continue; }
            comps++;
            const latest = latestVersions.get(c.hash);
            if (latest && c.version > latest) {
                add('warn', node.name + '.' + cname + ' v' + c.version + ' is newer than the transcoder supports (v' + latest + ')');
            }
        }
        const declared = new Set(slotsFor(node.name).map((s) => s.hash));
        for (const ch of node.children) {
            if (ch.parentHash !== 0 && !declared.has(ch.parentHash)) {
                add('warn', ch.prefab.name + ' docked at slot #' + ch.parentHash + ' that ' + node.name + ' does not declare');
            }
        }
        const lc = node.components.LiquidContainer;
        if (lc && lc.isCustom && lc.customData) {
            const col = lc.customData.color || {};
            if (col.a != null && col.a > 1) {
                add('fail', node.name + ' liquid alpha ' + col.a + ' is 255-scale — renders broken in-game (use fix alpha)');
            }
            // Over-1 RGB spawns but clamps to white. This is the defect
            // that made the "green" poison potion render colourless.
            const over = ['r', 'g', 'b'].filter((k) => col[k] != null && col[k] > 1);
            if (over.length) {
                add('fail', node.name + ' liquid colour ' + over.join('/') + ' is 255-scale ('
                    + ['r', 'g', 'b'].map((k) => col[k]).join(',') + ') — every channel over 1 clamps to 1, so this renders WHITE. Divide by 255 (re-apply the colour to fix).');
            }
            for (const eff of lc.customData.effects || []) {
                if (eff && effectNames.size && !effectNames.has(eff.hash)) {
                    add('warn', node.name + ' liquid effect #' + eff.hash + ' is not in the known catalog');
                }
            }
            if (lc.contentLevel < 0) { add('warn', node.name + ' liquid level is negative'); }
        }
        const dm = node.components.DurabilityModule;
        if (dm && (dm.integrity < 0 || dm.integrity > 1)) {
            add('warn', node.name + ' durability ' + dm.integrity + ' is outside 0..1');
        }
    });
    add('pass', pieces + ' piece(s), ' + comps + ' component(s), ' + unknownComps + ' unknown-but-preserved');

    return { ok: !checks.some((c) => c.level === 'fail'), warns: checks.filter((c) => c.level === 'warn').length, checks };
}

// ---- library (named strings + notes, one local JSON file) -----------------------

const STATE_DIR = process.env.WB_STATE_DIR || path.join(__dirname, 'state');
const LIB_FILE = path.join(STATE_DIR, 'strings.json');

function libLoad() {
    try { return JSON.parse(fs.readFileSync(LIB_FILE, 'utf8')).strings || []; }
    catch { return []; }
}

function libSave(items) {
    fs.mkdirSync(STATE_DIR, { recursive: true });
    fs.writeFileSync(LIB_FILE, JSON.stringify({ strings: items }, null, 1));
}

function libName(v) {
    const s = String(v == null ? '' : v).trim().toLowerCase();
    if (!/^[a-z0-9_-]{1,40}$/.test(s)) {
        throw new WbError('names: letters/digits/dash/underscore, 1-40 chars');
    }
    return s;
}

function libNotes(v) {
    if (v == null) { return ''; }
    return String(v).replace(/[\r\n\t]+/g, ' ').replace(/[\0-\x1f]/g, '').trim().slice(0, 500);
}

function libTags(v) {
    const arr = Array.isArray(v) ? v : String(v == null ? '' : v).split(',');
    const out = [];
    for (const t of arr) {
        const s = String(t).trim().toLowerCase().replace(/[^a-z0-9-]/g, '').slice(0, 20);
        if (s && !out.includes(s)) { out.push(s); }
        if (out.length >= 8) { break; }
    }
    return out;
}

// ---- recipe library (named liquid recipes, one local JSON file) -----------------
// A recipe is just a saved liquid customData (colour + effects + chunks + look).
// Apply it to any container with the normal setLiquid op — this is only storage,
// so a brew you perfected once is one click away on the next potion.

const RECIPE_FILE = path.join(STATE_DIR, 'recipes.json');

function recipeLoad() {
    try { return JSON.parse(fs.readFileSync(RECIPE_FILE, 'utf8')).recipes || []; }
    catch { return []; }
}
function recipeSave(items) {
    fs.mkdirSync(STATE_DIR, { recursive: true });
    fs.writeFileSync(RECIPE_FILE, JSON.stringify({ recipes: items }, null, 1));
}

// A compact digest of a liquid customData for the recipe cards.
function recipeSummary(cd) {
    if (!cd || typeof cd !== 'object') { return null; }
    const col = cd.color || {};
    return {
        color: { r: col.r == null ? 0 : col.r, g: col.g == null ? 0 : col.g,
                 b: col.b == null ? 0 : col.b, a: col.a == null ? 1 : col.a },
        effects: (cd.effects || []).filter(Boolean).map((e) => ({
            hash: e.hash, name: effectNames.get(e.hash) || null, mult: e.strengthMultiplier
        })),
        chunks: (cd.foodChunks || []).length,
        skin: !!cd.isConsumableThroughSkin
    };
}

// Sanitise an incoming customData to the shape the transcoder writes, rescaling
// 0-255 colour to the 0-1 floats the game wants (same rule as setLiquid).
function cleanCustomData(cd) {
    if (!cd || typeof cd !== 'object') { throw new WbError('recipe needs a liquid (customData)'); }
    const col = cd.color || {};
    const rgb = ['r', 'g', 'b'].map((k) => num(col[k] == null ? 0 : col[k], k));
    const scale = rgb.some((v) => v > 1) ? 255 : 1;
    let a = num(col.a == null ? 1 : col.a, 'alpha');
    if (a > 1) { a = a / 255; }
    const out = {
        color: {
            r: Math.max(0, Math.min(1, rgb[0] / scale)),
            g: Math.max(0, Math.min(1, rgb[1] / scale)),
            b: Math.max(0, Math.min(1, rgb[2] / scale)),
            a: Math.max(0, Math.min(1, a))
        },
        isConsumableThroughSkin: !!cd.isConsumableThroughSkin,
        visualDataHash: Number.isFinite(+cd.visualDataHash) ? +cd.visualDataHash : 0,
        effects: (Array.isArray(cd.effects) ? cd.effects : []).filter(Boolean).slice(0, 12).map((e) => ({
            hash: num(e.hash, 'effect hash') | 0,
            strengthMultiplier: num(e.strengthMultiplier == null ? 1 : e.strengthMultiplier, 'strength')
        })),
        foodChunks: (Array.isArray(cd.foodChunks) ? cd.foodChunks : []).slice(0, 12).map((h) => num(h, 'chunk') | 0)
    };
    return out;
}

// ---- spawn-in-game: the panel proxy --------------------------------------------
// `spawn string <player> <s>` rides the panel's /api/strings/spawn, which the
// owner reaches over the SSH tunnel. Loopback-to-loopback only: the workbench
// binds 127.0.0.1 and refuses to proxy anywhere that isn't 127.0.0.1 either.

const PLAYER_RE = /^[A-Za-z0-9 _()'.-]{1,80}$/;
const SAVE_STRING_RE = /^[\d,|.+-]+$/;

function entityId(v) {
    const n = typeof v === 'number' ? v : parseInt(String(v == null ? '' : v).trim(), 10);
    if (!Number.isInteger(n) || n <= 0) {
        throw new WbError('entity id is a positive integer (the in-game editor shows it; so does "edit near")');
    }
    return n;
}

function playerName(v) {
    const s = String(v == null ? '' : v).trim();
    if (s === '' || !PLAYER_RE.test(s)) {
        throw new WbError('player name has characters the console pipe will not carry');
    }
    return s;
}

function spawnableString(v) {
    const s = String(v == null ? '' : v).replace(/\s+/g, '');
    if (s === '' || s.length > 200000 || !SAVE_STRING_RE.test(s)) {
        throw new WbError('not a valid save string (digits/commas/pipe only)');
    }
    return s;
}

function panelUrlOf(v) {
    let u;
    try { u = new URL(String(v || process.env.WB_PANEL_URL || DEFAULT_PANEL_URL)); }
    catch { throw new WbError('panel url is not a URL'); }
    if (u.protocol !== 'http:') { throw new WbError('panel url must be http (the tunnel is local)'); }
    if (u.hostname !== '127.0.0.1' && u.hostname !== 'localhost') {
        throw new WbError('panel url must be loopback — the SSH tunnel makes the VPS panel local (got ' + u.hostname + ')');
    }
    return u;
}

function postJson(u, pathName, body, timeoutMs) {
    return new Promise((resolve, reject) => {
        const data = JSON.stringify(body);
        const r = http.request({
            host: u.hostname, port: u.port || 80, path: pathName, method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) }
        }, (res) => {
            let out = '';
            res.on('data', (c) => { out += c; });
            res.on('end', () => {
                try { resolve({ status: res.statusCode, body: JSON.parse(out) }); }
                catch { resolve({ status: res.statusCode, body: { error: 'panel answered non-JSON' } }); }
            });
        });
        r.setTimeout(timeoutMs || 10000, () => r.destroy(new Error('timed out')));
        r.on('error', reject);
        r.write(data);
        r.end();
    });
}

// ---- shop-catalog export --------------------------------------------------------
// The two live formats, verbatim from the server modules:
//   tshop (tablet-shop.json items[]):
//     { id, title, price, maxQty, string }
//   shop  (shops.json products[]):
//     { label, title, description,
//       price:   { prefab: "GoldCoin", hash: 61648, count },
//       product: { string, count: 1 } }

const GOLD_COIN_HASH = 61648;

function exportShopItem(b, string) {
    const format = String(b.format || 'tshop');
    const id = libName(b.id);
    const title = String(b.title == null ? '' : b.title).trim().slice(0, 80) || id;
    const price = num(b.price == null ? 10 : b.price, 'price');
    if (!Number.isInteger(price) || price < 1) { throw new WbError('price must be a whole number of gold >= 1'); }
    if (format === 'tshop') {
        const maxQty = num(b.maxQty == null ? 1 : b.maxQty, 'maxQty');
        if (!Number.isInteger(maxQty) || maxQty < 1 || maxQty > 10) { throw new WbError('maxQty is 1..10'); }
        return { format, json: JSON.stringify({ id, title, price, maxQty, string }, null, 2) };
    }
    if (format === 'shop') {
        const description = String(b.description == null ? '' : b.description).trim().slice(0, 200);
        return {
            format,
            json: JSON.stringify({
                label: id, title, description,
                price: { prefab: 'GoldCoin', hash: GOLD_COIN_HASH, count: price },
                product: { string, count: 1 }
            }, null, 2)
        };
    }
    throw new WbError('format is tshop or shop');
}

// ---- http --------------------------------------------------------------------

function readBody(req) {
    return new Promise((resolve, reject) => {
        let data = '';
        req.on('data', (c) => {
            data += c;
            if (data.length > 1048576) { req.destroy(); reject(new WbError('body too large')); }
        });
        req.on('end', () => {
            if (data === '') { return resolve({}); }
            try { resolve(JSON.parse(data)); }
            catch { reject(new WbError('body is not JSON')); }
        });
        req.on('error', reject);
    });
}

function json(res, status, obj) {
    const body = JSON.stringify(obj);
    res.writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) });
    res.end(body);
}

// Resolve a string from { string } | { name (library) } | { docId (current encode) }.
function resolveString(b) {
    if (b.string) { return String(b.string); }
    if (b.name) {
        const item = libLoad().find((s) => s.name === libName(b.name));
        if (!item) { throw new WbError('no saved string: ' + b.name); }
        return item.string;
    }
    if (b.docId) {
        const session = getSession(String(b.docId));
        try { return session.prefab.toSaveString(); }
        catch (e) { throw new WbError('current doc does not encode: ' + e.message); }
    }
    throw new WbError('give one of: string, name, docId');
}

function createServer() {
    const indexHtml = fs.readFileSync(path.join(__dirname, 'public', 'index.html'));

    return http.createServer(async (req, res) => {
        const url = new URL(req.url, 'http://localhost');
        const route = req.method + ' ' + url.pathname;
        try {
            // Loopback-only tool -> loopback-only requests. Two cheap guards keep
            // a random web page the owner is browsing from driving this server
            // (it binds 127.0.0.1, but the browser can still POST to localhost):
            //  - reject a non-loopback Host (closes DNS-rebinding, where an
            //    attacker domain resolves to 127.0.0.1 but sends its own Host);
            //  - reject any POST that isn't application/json. A cross-site
            //    "simple request" can't set that content type without a CORS
            //    preflight, which we never answer with allow headers, so this
            //    shuts the localhost-CSRF hole — it matters now that /api/replace
            //    can despawn a live entity by id.
            const host = (req.headers.host || '').split(':')[0];
            if (host && host !== '127.0.0.1' && host !== 'localhost') {
                return json(res, 403, { error: 'loopback only (unexpected Host: ' + host + ')' });
            }
            if (req.method === 'POST') {
                const ct = (req.headers['content-type'] || '').split(';')[0].trim().toLowerCase();
                const len = parseInt(req.headers['content-length'] || '0', 10) || 0;
                if (len > 0 && ct !== 'application/json') {
                    return json(res, 415, { error: 'POST body must be application/json' });
                }
            }
            // In the shared build, the in-game paths (which need the mod + tunnel)
            // are off. The UI hides their buttons too, but gate the routes so a
            // stale bookmark or direct call gets an honest answer, not a crash.
            if (isShare() && /^\/api\/(spawn|capture|replace|panel\/ping)$/.test(url.pathname)) {
                return json(res, 404, { error: 'in-game features are off in the shared build (no server tunnel). Use "copy string" and spawn it however your server allows.' });
            }
            // slot-reference image + legend: /api/slot-ref/<name>, /api/slot-info/<name>
            if (req.method === 'GET' && url.pathname.startsWith('/api/slot-ref/')) {
                const name = decodeURIComponent(url.pathname.slice('/api/slot-ref/'.length));
                const e = slotRefs.get(name);
                if (!e || !e.file) { return json(res, 404, { error: 'no slot reference for ' + name }); }
                try {
                    const buf = fs.readFileSync(path.join(SLOTREF_DIR, e.file));
                    res.writeHead(200, {
                        'Content-Type': IMAGE_TYPES[e.file.split('.').pop()] || 'image/png',
                        'Content-Length': buf.length,
                        'Cache-Control': 'max-age=86400'
                    });
                    return res.end(buf);
                } catch { return json(res, 404, { error: 'slot-ref file missing for ' + name }); }
            }
            if (req.method === 'GET' && url.pathname.startsWith('/api/slot-info/')) {
                const name = decodeURIComponent(url.pathname.slice('/api/slot-info/'.length));
                const e = slotRefs.get(name);
                if (!e) { return json(res, 404, { error: 'no slot reference for ' + name }); }
                return json(res, 200, { name, hasImage: !!e.file, slots: e.slots || [] });
            }
            // item thumbnails: /api/item-image/<hash>
            if (req.method === 'GET' && url.pathname.startsWith('/api/item-image/')) {
                const hash = parseInt(url.pathname.slice('/api/item-image/'.length), 10);
                const file = imageFiles.get(hash);
                if (!file) { return json(res, 404, { error: 'no image for ' + hash }); }
                try {
                    const buf = fs.readFileSync(path.join(IMAGES_DIR, file));
                    res.writeHead(200, {
                        'Content-Type': IMAGE_TYPES[file.split('.').pop()] || 'image/png',
                        'Content-Length': buf.length,
                        'Cache-Control': 'max-age=86400'
                    });
                    return res.end(buf);
                } catch { return json(res, 404, { error: 'image file missing for ' + hash }); }
            }
            switch (route) {
                case 'GET /':
                    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
                    return res.end(indexHtml);

                case 'GET /api/enums':
                    return json(res, 200, {
                        share: isShare(),
                        materials: Object.keys(PhysicalMaterialPartHash).filter((k) => isNaN(k)).sort(),
                        presets: liquidEnums.presets,
                        components: Object.keys(COMPONENT_CLASSES).sort(),
                        effects: liquidEnums.effects,
                        appearances: liquidEnums.appearances,
                        chunks: liquidEnums.chunks
                    });

                case 'GET /api/prefabs': {
                    const limit = Math.min(Math.max(parseInt(url.searchParams.get('limit') || '50', 10) || 50, 1), 500);
                    return json(res, 200, { results: searchCatalog(url.searchParams.get('q') || '', limit) });
                }

                // the visual browsers: full enriched catalog + slot-ref listing
                case 'GET /api/browser/prefabs':
                    return json(res, 200, { prefabs: browserCatalog() });

                case 'GET /api/browser/slot-refs':
                    return json(res, 200, {
                        refs: [...slotRefs.entries()].map(([name, e]) => ({
                            name,
                            hasImage: !!e.file,
                            hash: (catalog.find((p) => p.name === name) || {}).hash || null,
                            slots: (e.slots || []).map((s) => ({
                                color: s.color, slot: s.slot, hash: s.hash, comment: s.comment || ''
                            }))
                        })).sort((a, b) => a.name.localeCompare(b.name))
                    });

                case 'POST /api/new': {
                    const b = await readBody(req);
                    const name = String(b.prefab || '');
                    if (!ATTPrefabs[name]) { throw new WbError('unknown prefab: ' + name); }
                    const docId = newSession(new Prefab(name));
                    return json(res, 200, { docId, ...result(getSession(docId)) });
                }

                // ---- pouches -------------------------------------------------
                // The picker only offers items whose dockedTypeHash we have
                // actually captured from a real in-game pouch — see
                // data/dockedTypes.json. Anything else would encode a pouch
                // that spawns wrong, so it is not selectable.
                case 'GET /api/pouch/types': {
                    const types = Object.entries(pouch.knownTypes())
                        .map(([name, typeHash]) => {
                            const cat = catalog.find((p) => p.name === name);
                            return {
                                name, typeHash,
                                hash: cat ? cat.hash : null,
                                img: cat ? hasImage(cat.hash) : false
                            };
                        })
                        .sort((a, b) => a.name.localeCompare(b.name));
                    return json(res, 200, {
                        types,
                        maxQuantity: pouch.MAX_QUANTITY,
                        verifiedQuantity: pouch.VERIFIED_QUANTITY
                    });
                }

                case 'POST /api/pouch': {
                    const b = await readBody(req);
                    let prefab, quantity;
                    try {
                        quantity = pouch.parseQuantity(b.quantity);
                        prefab = pouch.buildPouch(b.item, quantity, b.typeHash);
                    } catch (e) {
                        if (e instanceof pouch.PouchError) { throw new WbError(e.message); }
                        throw e;
                    }
                    const docId = newSession(prefab);
                    return json(res, 200, {
                        docId, ...result(getSession(docId)), note: pouch.quantityNote(quantity)
                    });
                }

                case 'POST /api/decode': {
                    const b = await readBody(req);
                    let prefab;
                    try { prefab = Prefab.fromSaveString(String(b.string || '').trim()); }
                    catch (e) { throw new WbError('decode failed: ' + e.message); }
                    const docId = newSession(prefab);
                    return json(res, 200, { docId, ...result(getSession(docId)) });
                }

                // In-game editor scale buttons: edit the prefab's encoded scale, then
                // the client replaces it through the server's normal spawn/sync path.
                // Scale is float32 word 10 in the raw save string. Re-serializing the
                // prefab with att-string-transcoder can omit unknown component data,
                // so preserve the original string and replace only that packed word.
                case 'POST /api/scale-string': {
                    const b = await readBody(req);
                    const string = String(b.string || '').trim();
                    const factor = Number(b.factor);
                    if (!string || string.length > 200000 || !/^[0-9,.|+-]+$/.test(string))
                        throw new WbError('invalid save string');
                    if (!Number.isFinite(factor) || factor < 0.5 || factor > 2)
                        throw new WbError('scale factor must be from 0.5 to 2');
                    const pipe = string.indexOf('|');
                    const data = pipe >= 0 ? string.slice(0, pipe) : string;
                    const words = data.split(',');
                    if (words.length < 11) throw new WbError('save string has no scale word at index 10');
                    const scaleWord = Number(words[10]);
                    if (!Number.isInteger(scaleWord) || scaleWord < 0 || scaleWord > 0xffffffff)
                        throw new WbError('save string scale word is invalid');
                    const packed = Buffer.alloc(4);
                    packed.writeUInt32LE(scaleWord, 0);
                    const current = packed.readFloatLE(0);
                    if (!Number.isFinite(current) || current <= 0)
                        throw new WbError('prefab has an invalid scale');
                    const scale = Math.fround(Math.max(0.05, Math.min(20, current * factor)));
                    if (scale === current) throw new WbError('prefab is already at the scale limit');
                    packed.writeFloatLE(scale, 0);
                    words[10] = packed.readUInt32LE(0).toString();
                    const output = words.join(',') + (pipe >= 0 ? string.slice(pipe) : '');
                    return json(res, 200, { ok: true, scale, string: output });
                }

                // ---- diff: compare two save strings -------------------------
                case 'POST /api/diff': {
                    const b = await readBody(req);
                    let A, B;
                    try { A = Prefab.fromSaveString(String(b.a || '').trim()); }
                    catch (e) { throw new WbError('left string does not decode: ' + e.message); }
                    try { B = Prefab.fromSaveString(String(b.b || '').trim()); }
                    catch (e) { throw new WbError('right string does not decode: ' + e.message); }
                    const diffs = diffPrefabs(A, B);
                    return json(res, 200, { a: A.name, b: B.name, same: diffs.length === 0, diffs });
                }

                // ---- recipe library (named liquid recipes) ------------------
                case 'GET /api/recipes':
                    return json(res, 200, {
                        recipes: recipeLoad().map((r) => ({
                            name: r.name, notes: r.notes || '', savedAt: r.savedAt,
                            summary: recipeSummary(r.customData)
                        }))
                    });

                case 'POST /api/recipes/save': {
                    const b = await readBody(req);
                    const name = libName(b.name);
                    const customData = cleanCustomData(b.customData);
                    const items = recipeLoad().filter((r) => r.name !== name);
                    items.push({ name, customData, notes: libNotes(b.notes), savedAt: new Date().toISOString() });
                    recipeSave(items);
                    return json(res, 200, { ok: true, name });
                }

                case 'POST /api/recipes/get': {
                    const b = await readBody(req);
                    const r = recipeLoad().find((x) => x.name === libName(b.name));
                    if (!r) { return json(res, 404, { error: 'no saved recipe: ' + b.name }); }
                    return json(res, 200, { name: r.name, customData: r.customData, notes: r.notes || '' });
                }

                case 'POST /api/recipes/delete': {
                    const b = await readBody(req);
                    const items = recipeLoad();
                    const kept = items.filter((r) => r.name !== String(b.name || ''));
                    if (kept.length === items.length) { return json(res, 404, { error: 'no saved recipe: ' + b.name }); }
                    recipeSave(kept);
                    return json(res, 200, { ok: true });
                }

                // ---- panel reachability (full build only): the live dot ------
                case 'GET /api/panel/ping': {
                    if (isShare()) { return json(res, 200, { ok: false, share: true }); }
                    let u;
                    try { u = panelUrlOf(url.searchParams.get('panelUrl')); }
                    catch (e) { return json(res, 200, { ok: false, error: e.message }); }
                    try {
                        const out = await postJson(u, '/api/edit/tostring', { id: 0 }, 4000);
                        // any HTTP answer means the panel is up (id:0 will error, that's fine)
                        return json(res, 200, { ok: out.status > 0, origin: u.origin });
                    } catch (e) {
                        return json(res, 200, { ok: false, origin: u.origin, error: e.message });
                    }
                }

                case 'POST /api/op': {
                    const b = await readBody(req);
                    const session = getSession(String(b.docId || ''));
                    const opName = String(b.op || '');
                    const before = snapshot(session);
                    if (DOC_OPS.has(opName)) {
                        applyDocOp(session.prefab, opName, b.args);
                    } else {
                        applyOp(nodeAt(session.prefab, b.path), opName, b.args);
                    }
                    pushHistory(session, before);
                    return json(res, 200, result(session));
                }

                case 'POST /api/undo': {
                    const session = getSession(String((await readBody(req)).docId || ''));
                    const prev = session.history.pop();
                    if (prev == null) { throw new WbError('nothing to undo'); }
                    const cur = snapshot(session);
                    session.prefab = Prefab.fromSaveString(prev);
                    if (cur != null) { session.future.push(cur); }
                    return json(res, 200, result(session));
                }

                case 'POST /api/redo': {
                    const session = getSession(String((await readBody(req)).docId || ''));
                    const next = session.future.pop();
                    if (next == null) { throw new WbError('nothing to redo'); }
                    const cur = snapshot(session);
                    session.prefab = Prefab.fromSaveString(next);
                    if (cur != null) { session.history.push(cur); }
                    return json(res, 200, result(session));
                }

                case 'GET /api/string': {
                    const session = getSession(url.searchParams.get('docId') || '');
                    return json(res, 200, result(session));
                }

                // one docked piece as its own string (the shelf's copy source)
                case 'GET /api/substring': {
                    const session = getSession(url.searchParams.get('docId') || '');
                    const p = (url.searchParams.get('path') || '').split(',')
                        .filter((s) => s !== '').map((n) => parseInt(n, 10));
                    const node = nodeAt(session.prefab, p);
                    let s;
                    try { s = node.toSaveString(); }
                    catch (e) { throw new WbError('piece does not encode: ' + e.message); }
                    return json(res, 200, { name: node.name, hash: node.hash, img: hasImage(node.hash), string: s });
                }

                case 'GET /api/preflight': {
                    const session = getSession(url.searchParams.get('docId') || '');
                    return json(res, 200, preflight(session.prefab));
                }

                // ---- library ------------------------------------------------

                case 'GET /api/library':
                    return json(res, 200, {
                        strings: libLoad().map((s) => {
                            const sum = summaryOf(s.string);
                            return {
                                name: s.name,
                                length: s.string.length,
                                savedAt: s.savedAt,
                                notes: s.notes || '',
                                tags: s.tags || [],
                                summary: sum.summary || null,
                                decodeError: sum.error || null
                            };
                        })
                    });

                // backup: the whole library as a downloadable json
                case 'GET /api/library/export': {
                    const body = JSON.stringify({
                        format: 'att-string-workbench-library',
                        version: 1,
                        exported: new Date().toISOString(),
                        strings: libLoad()
                    }, null, 1);
                    res.writeHead(200, {
                        'Content-Type': 'application/json',
                        'Content-Length': Buffer.byteLength(body),
                        'Content-Disposition': 'attachment; filename="workbench-library-'
                            + new Date().toISOString().slice(0, 10) + '.json"'
                    });
                    return res.end(body);
                }

                // restore/merge: local entries always win; a name collision with a
                // DIFFERENT string imports as name-2 (never destroys local work)
                case 'POST /api/library/import': {
                    const b = await readBody(req);
                    const incoming = Array.isArray(b.strings) ? b.strings : null;
                    if (!incoming) { throw new WbError('not a library export (missing strings[])'); }
                    const items = libLoad();
                    const byName = new Map(items.map((s) => [s.name, s]));
                    let added = 0, skipped = 0, renamed = 0;
                    for (const inc of incoming) {
                        let name;
                        try { name = libName(inc && inc.name); } catch { skipped++; continue; }
                        const str = inc && typeof inc.string === 'string' ? inc.string : '';
                        if (!str || str.length > 200000) { skipped++; continue; }
                        const entry = {
                            name,
                            string: str,
                            notes: libNotes(inc.notes),
                            tags: libTags(inc.tags),
                            savedAt: typeof inc.savedAt === 'string' ? inc.savedAt : new Date().toISOString()
                        };
                        const existing = byName.get(name);
                        if (existing) {
                            if (existing.string === str) { skipped++; continue; }
                            let n = 2;
                            while (byName.has(name + '-' + n)) { n++; }
                            entry.name = (name + '-' + n).slice(0, 40);
                            renamed++;
                        } else {
                            added++;
                        }
                        items.push(entry);
                        byName.set(entry.name, entry);
                    }
                    libSave(items);
                    return json(res, 200, { ok: true, added, renamed, skipped });
                }

                case 'POST /api/library/tags': {
                    const b = await readBody(req);
                    const name = libName(b.name);
                    const items = libLoad();
                    const item = items.find((s) => s.name === name);
                    if (!item) { return json(res, 404, { error: 'no saved string: ' + name }); }
                    item.tags = libTags(b.tags);
                    libSave(items);
                    return json(res, 200, { ok: true, name, tags: item.tags });
                }

                case 'POST /api/library/save': {
                    const b = await readBody(req);
                    const name = libName(b.name);
                    if (!b.string || typeof b.string !== 'string') { throw new WbError('nothing to save'); }
                    const items = libLoad();
                    const old = items.find((s) => s.name === name);
                    const kept = items.filter((s) => s.name !== name);
                    kept.push({
                        name,
                        string: b.string,
                        notes: b.notes !== undefined ? libNotes(b.notes) : (old ? old.notes || '' : ''),
                        tags: b.tags !== undefined ? libTags(b.tags) : (old ? old.tags || [] : []),
                        savedAt: new Date().toISOString()
                    });
                    libSave(kept);
                    return json(res, 200, { ok: true, name });
                }

                case 'POST /api/library/get': {
                    const b = await readBody(req);
                    const item = libLoad().find((s) => s.name === String(b.name || ''));
                    if (!item) { return json(res, 404, { error: 'no saved string: ' + b.name }); }
                    return json(res, 200, { name: item.name, string: item.string, notes: item.notes || '', savedAt: item.savedAt });
                }

                case 'POST /api/library/notes': {
                    const b = await readBody(req);
                    const name = libName(b.name);
                    const items = libLoad();
                    const item = items.find((s) => s.name === name);
                    if (!item) { return json(res, 404, { error: 'no saved string: ' + name }); }
                    item.notes = libNotes(b.notes);
                    libSave(items);
                    return json(res, 200, { ok: true, name, notes: item.notes });
                }

                case 'POST /api/library/rename': {
                    const b = await readBody(req);
                    const from = libName(b.name);
                    const to = libName(b.to);
                    const items = libLoad();
                    const item = items.find((s) => s.name === from);
                    if (!item) { return json(res, 404, { error: 'no saved string: ' + from }); }
                    if (from !== to && items.some((s) => s.name === to)) {
                        throw new WbError('"' + to + '" already exists');
                    }
                    item.name = to;
                    libSave(items);
                    return json(res, 200, { ok: true, name: to });
                }

                case 'POST /api/library/delete': {
                    const b = await readBody(req);
                    const items = libLoad();
                    const kept = items.filter((s) => s.name !== String(b.name || ''));
                    if (kept.length === items.length) { return json(res, 404, { error: 'no saved string: ' + b.name }); }
                    libSave(kept);
                    return json(res, 200, { ok: true });
                }

                // ---- spawn in game (panel proxy over the owner's tunnel) ----

                case 'POST /api/spawn': {
                    const b = await readBody(req);
                    const player = playerName(b.player);
                    const s = spawnableString(resolveString(b));
                    const u = panelUrlOf(b.panelUrl);
                    let out;
                    try {
                        out = await postJson(u, '/api/strings/spawn', { player, string: s }, 12000);
                    } catch (e) {
                        return json(res, 200, {
                            ok: false,
                            error: 'panel unreachable at ' + u.origin + ' — is the SSH tunnel up? (' + e.message + ')'
                        });
                    }
                    const p = out.body || {};
                    return json(res, 200, {
                        ok: p.ok === true,
                        command: p.command || null,
                        text: p.text || null,
                        error: p.error || (p.ok === true ? null : (p.exception || 'panel answered HTTP ' + out.status))
                    });
                }

                // ---- in-game round trip: capture + replace (panel proxy) ----
                // The full loop the editor track built server-side: blueprint
                // string <id> to pull a LIVE entity into the workbench, edit
                // replace <id> <string> to push the edit back at the exact same
                // position+rotation (despawn+respawn under the hood — the game
                // only consumes strings at spawn time, so a true in-place apply
                // does not exist; see README "editing spawned strings").

                case 'POST /api/capture': {
                    const b = await readBody(req);
                    const id = entityId(b.id);
                    const u = panelUrlOf(b.panelUrl);
                    let out;
                    try {
                        out = await postJson(u, '/api/edit/tostring', { id }, 12000);
                    } catch (e) {
                        return json(res, 200, {
                            ok: false,
                            error: 'panel unreachable at ' + u.origin + ' — is the SSH tunnel up? (' + e.message + ')'
                        });
                    }
                    const p = out.body || {};
                    if (p.ok !== true || !p.string) {
                        return json(res, 200, { ok: false, error: p.error || ('panel answered HTTP ' + out.status) });
                    }
                    let prefab;
                    try { prefab = Prefab.fromSaveString(String(p.string).trim()); }
                    catch (e) { return json(res, 200, { ok: false, error: 'captured but not decodable: ' + e.message }); }
                    const docId = newSession(prefab);
                    return json(res, 200, {
                        ok: true, docId, entityId: id, label: p.label || null,
                        ...result(getSession(docId))
                    });
                }

                case 'POST /api/replace': {
                    const b = await readBody(req);
                    const id = entityId(b.id);
                    const s = spawnableString(resolveString(b));
                    const u = panelUrlOf(b.panelUrl);
                    let out;
                    try {
                        out = await postJson(u, '/api/edit/replace', { id, string: s }, 15000);
                    } catch (e) {
                        return json(res, 200, {
                            ok: false,
                            error: 'panel unreachable at ' + u.origin + ' — is the SSH tunnel up? (' + e.message + ')'
                        });
                    }
                    const p = out.body || {};
                    const d = p.data || {};
                    const ok = p.ok === true && d.ok !== false;
                    return json(res, 200, {
                        ok,
                        newId: (ok && d.entity && d.entity.id) || null,
                        note: d.note || null,
                        error: ok ? null : (p.error || d.error || ('panel answered HTTP ' + out.status))
                    });
                }

                // ---- shop-catalog export ------------------------------------

                case 'POST /api/export/shop': {
                    const b = await readBody(req);
                    const s = spawnableString(resolveString(b));
                    return json(res, 200, { ok: true, ...exportShopItem(b, s) });
                }

                default:
                    return json(res, 404, { error: 'no such route: ' + route });
            }
        } catch (e) {
            if (e instanceof WbError) { return json(res, 400, { error: e.message }); }
            console.error('[workbench] ' + route + ' failed:', e);
            return json(res, 500, { error: 'internal error: ' + e.message });
        }
    });
}

// Local tool, local only.
function start(server, port, bindHost) {
    const host = bindHost || '127.0.0.1';
    if (host !== '127.0.0.1' && host !== 'localhost') {
        throw new Error('workbench binds loopback only (asked for ' + host + ')');
    }
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, '127.0.0.1', () => resolve(server.address().port));
    });
}

module.exports = { createServer, start, searchCatalog, applyOp, view, summarize, exportShopItem };

if (require.main === module) {
    const argPort = process.argv.indexOf('--port');
    const port = argPort !== -1 ? parseInt(process.argv[argPort + 1], 10)
        : parseInt(process.env.WB_PORT || '', 10) || DEFAULT_PORT;
    start(createServer(), port).then((p) => {
        console.log('[workbench] String Workbench on http://localhost:' + p
            + '  (local only; "spawn in game" goes through your panel tunnel at '
            + (process.env.WB_PANEL_URL || DEFAULT_PANEL_URL) + ')');
    }).catch((e) => {
        console.error('[workbench] failed to start: ' + e.message);
        process.exit(1);
    });
}
