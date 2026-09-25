// pouch-core.js — the pouch encoder. Shared by the CLI (tools/pouch.js) and the
// workbench server's POST /api/pouch. One implementation, two front ends.
//
// Decoded from a live flint pouch: a pouch's contents are NOT N
// children. They are
//
//   entities.Storage_36080.components.PickupDock = {
//       dockedTypeHash,   <- WHICH item type is inside
//       quantity,         <- HOW MANY  (this is the "x30")
//       childIndex: 0
//   }
//
// plus ONE child prefab docked at parentHash 36080 that acts as the visual
// representative of the pile. Change the count by changing `quantity`; change
// the item by changing the child prefab AND `dockedTypeHash`.

'use strict';

const fs = require('fs');
const path = require('path');
const { Prefab, ATTPrefabs } = require('att-string-transcoder');

// A real captured pouch, used as the structural template (its PickupDock entity
// and docked child are what get rewritten below). Inlined deliberately: this
// used to be read out of a sibling repo file, which broke
// the share bundle and couples a local tool to a tree it has no business
// reaching into. Verified byte-identical to that file's `flintpouch` item.
const FLINT_POUCH_TEMPLATE = '38942,406,38942,3293906793,1126242337,1124161821,3207681279,1058025869,3199010797,1050180414,1065353223,1454441398,354,3809569376,2,1073741824,2147483648,1582421644,2666786801,2656163468,3751884287,524574158,517973611,529202149,1646486529,3221225576,2970960346,1355302408,1354782279,1875662143,3485731939,1873494523,1336286927,2415919104,0,0,0,0,0,237578549,536870916,268435456,0,0,1127,2147483654,241830387,2348810241,2147484313,704643072,2046820352,0,0,33554713,3758096692,2038999579,2995143207,1485177509,2105252259,880590766,2105150644,1350496435,4168954675,890313504,234881027,1166584347,2995143207,1485177509,2105252259,880590766,2105150644,1350496435,4185915392,0,0,0,0,0,2840705,3674210304,2977007797,805306368,18874368,1040384,996684,2647485967,1548704244,2460810750,3054314545,3260013508,1502588359,757985657,1138753536,67629056,1856082,1782579200,136314880,0,0,1,3879731200,35127296,0,0,|165,272188517,1,237360636,2,391977879,2,277505782,2,1624211074,1,7704646,1,830106687,1,1651678475,1,22446553,2,403040752,2,2617495528,1,3445325106,1,2126500253,1,1871432223,1,3920618075,1,3665939353,1,701033518,1,3373651539,1,654225716,1,2624099526,1,2069630919,1,3146178080,1,1588536425,1,2576456808,1,2127962967,1,70871065,1,1063725326,1,1211178616,1,2951515968,1,661497638,1,2293737711,1,496827038,1,2912807649,3,2190886200,1,2978042925,1,2801168996,2,2495475500,2,1176706580,2,1001395212,2,1874870249,2,3230087383,1,2498617949,1,1714180166,2,3109677933,2,1509838052,1,320224849,1,3820454400,5,2400796504,1,3101665521,1,2975913730,1,3901697682,1,2272630171,1,1823429789,1,2363255897,1,3402094521,1,751359624,1,1217391130,1,276353327,1,1390862571,1,1085701614,1,3608460219,1,3801256786,3,1198377566,1,1454441398,2,3704379512,1,3257374625,1,2262399392,2,309083880,1,586602603,1,1934129787,1,1908922854,1,159391088,1,910018632,2,4144776006,3,2815374842,1,4081488368,1,566175523,1,3202828999,1,715394364,2,1756836969,1,1268269765,1,1558189723,1,623957243,1,392234266,3,1133328725,1,3307010681,1,1427693318,1,775321715,2,2169673426,1,3450348902,1,3751351177,1,1257282635,2,200292695,3,2700376822,1,3188272159,1,3245685963,1,2120963769,1,392344172,1,1391720462,2,2610542999,1,1081247904,1,2880587164,1,2563434699,1,2253011220,1,967932020,1,3171294583,1,2814234626,1,1964978567,1,1228539097,1,2026743731,1,3538443740,1,4134534481,1,3638500874,2,1098050191,3,43510150,2,1645673210,2,788405183,1,2563567105,2,1454955908,1,1931537627,1,259381630,1,3932346318,1,2978388169,1,1431397437,2,3070493599,2,1233775263,1,3674519521,1,2290978823,1,3431876266,1,4095875831,1,2314081177,1,2590537994,2,2443660852,1,3642863935,2,634164392,5,2759613175,1,875684520,1,3084373371,1,2833060406,1,1587058252,1,2592242915,2,2882590463,1,1499506132,1,205333986,1,4179293747,1,3561515449,1,34507654,1,1962842866,1,4282337604,1,1787084913,4,3588929783,1,3640332570,1,3236280681,3,2629079826,1,963907309,1,1753993206,2,1923918202,3,1776498660,3,3457519710,2,4109360768,2,2971871217,1,766675725,1,2450553269,1,902024186,1,2081565440,3,';

const STORAGE_ENTITY = 'Storage_36080';
const STORAGE_HASH = 36080;

// No encoder-side ceiling exists, so this bound is a judgement call, not a
// discovered limit. Only quantity 5 has ever been spawned and counted in-game
// (Flint x5). Anything above VERIFIED_QUANTITY builds fine and is reported as
// unverified rather than blocked.
const MAX_QUANTITY = 99;
const VERIFIED_QUANTITY = 5;

class PouchError extends Error { }

const TYPES_FILE = process.env.WB_DOCKED_TYPES
    || path.join(__dirname, '..', 'data', 'dockedTypes.json');

// Read fresh every call so a newly captured hash can be pasted into the json and
// picked up on the next request — no server restart, no code edit.
function knownTypes() {
    let raw;
    try { raw = fs.readFileSync(TYPES_FILE, 'utf8').replace(/^﻿/, ''); }
    catch { return {}; }
    let parsed;
    try { parsed = JSON.parse(raw); }
    catch (e) { throw new PouchError('dockedTypes.json is not valid JSON: ' + e.message); }
    const out = {};
    for (const [name, hash] of Object.entries(parsed)) {
        if (name.startsWith('_')) { continue; }          // _note and friends
        if (Number.isInteger(hash)) { out[name] = hash; }
    }
    return out;
}

function parseQuantity(raw) {
    // Number() rather than parseInt() on purpose: parseInt('1.5') is 1 and
    // parseInt('5abc') is 5, both of which would silently build a wrong pouch.
    const n = typeof raw === 'number' ? raw : Number(String(raw == null ? '' : raw).trim());
    if (!Number.isInteger(n)) { throw new PouchError('quantity must be a whole number'); }
    if (n < 1) { throw new PouchError('quantity must be at least 1'); }
    if (n > MAX_QUANTITY) { throw new PouchError('quantity must be ' + MAX_QUANTITY + ' or less'); }
    return n;
}

// null when the count is in verified territory, otherwise the caveat to show.
function quantityNote(n) {
    return n > VERIFIED_QUANTITY
        ? 'only x' + VERIFIED_QUANTITY + ' has been verified in-game — check this one before relying on it'
        : null;
}

function resolveTypeHash(itemName, captured) {
    if (captured != null && String(captured).trim() !== '') {
        const n = typeof captured === 'number' ? captured : Number(String(captured).trim());
        if (!Number.isInteger(n) || n < 0) {
            throw new PouchError('captured type hash must be a non-negative whole number');
        }
        return n;
    }
    const known = knownTypes()[itemName];
    if (known === undefined) { throw new PouchError(noTypeMessage(itemName)); }
    return known;
}

function noTypeMessage(itemName) {
    return 'no dockedTypeHash known for "' + itemName + '" — dock one in a pouch in-game, '
        + 'copy the pouch string, and run: node tools/pouch.js --decode "<string>"';
}

// The build. Returns a Prefab; callers encode it themselves.
function buildPouch(itemName, quantityRaw, capturedTypeHash) {
    const name = String(itemName == null ? '' : itemName).trim();
    if (!name) { throw new PouchError('pick an item to put in the pouch'); }
    if (!ATTPrefabs[name]) { throw new PouchError('unknown prefab: ' + name); }

    const quantity = parseQuantity(quantityRaw);
    const typeHash = resolveTypeHash(name, capturedTypeHash);

    const p = Prefab.fromSaveString(FLINT_POUCH_TEMPLATE);

    // 1. the count
    const dock = p.entities[STORAGE_ENTITY].components.PickupDock;
    dock.quantity = quantity;
    dock.dockedTypeHash = typeHash;

    // 2. the visual representative — swap the docked child for the new item
    p.children = [];
    p.addChildPrefab(null, new Prefab(name));
    // addChildPrefab with a null slot parents at 0; the pouch dock needs 36080
    p.children[p.children.length - 1].parentHash = STORAGE_HASH;

    return p;
}

// What a pouch string actually contains. Structured so the CLI can print it and
// the server can answer with it.
function inspect(str) {
    const p = Prefab.fromSaveString(str);
    const ent = (p.entities || {})[STORAGE_ENTITY];
    const dock = ent && ent.components && ent.components.PickupDock;
    const types = knownTypes();
    return {
        name: p.name,
        hash: p.hash,
        entities: Object.keys(p.entities || {}),
        dock: dock ? {
            dockedTypeHash: dock.dockedTypeHash,
            quantity: dock.quantity,
            childIndex: dock.childIndex
        } : null,
        children: (p.children || []).map((c) => ({
            name: c.prefab.name,
            hash: c.prefab.hash,
            parentHash: c.parentHash,
            // a docked child whose type we have never captured = a new mapping to record
            newMapping: dock && types[c.prefab.name] === undefined ? dock.dockedTypeHash : null
        })),
        prefab: p
    };
}

module.exports = {
    buildPouch, inspect, knownTypes, parseQuantity, quantityNote, resolveTypeHash,
    PouchError, MAX_QUANTITY, VERIFIED_QUANTITY, STORAGE_ENTITY, STORAGE_HASH,
    FLINT_POUCH_TEMPLATE
};
